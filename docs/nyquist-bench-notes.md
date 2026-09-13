# Bench notes — when the Nyquist looks broken

**For:** anyone testing this app against real hardware, on any head.

The app cannot tell you whether the device is healthy, so when something does not
work, check the device first. An hour was lost on 2026-09-12 debugging the app
for a fault that was entirely device-side.

Reference unit: **Nq1, firmware 3.8.0, WINC 19.7.7**, station mode on a normal
AP at `192.168.1.30`.

---

## Start here: is the device actually alive?

Run this before debugging anything in the app.

```bash
# 1. Does it answer discovery?
python3 - <<'PY'
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1); s.settimeout(4)
s.sendto(b"DAQiFi?\r\n", ("192.168.1.255", 30303))       # your subnet broadcast
try:
    d, a = s.recvfrom(2048); print("discovery OK from", a[0], len(d), "bytes")
except socket.timeout:
    print("discovery: NO REPLY")
PY

# 2. Does it answer on TCP?
python3 - <<'PY'
import socket, time
c = socket.create_connection(("192.168.1.30", 9760), timeout=5); c.settimeout(3)
c.sendall(b"SYSTem:ECHO -1\r\n"); time.sleep(0.3)
c.sendall(b"SYSTem:SYSInfoPB?\r\n"); time.sleep(1)
try: print("TCP reply:", len(c.recv(4096)), "bytes")     # healthy is ~590
except socket.timeout: print("TCP: connected but NO RESPONSE")
PY

# 3. Does it answer ping?
ping -c 3 192.168.1.30
```

Healthy looks like: discovery replies, TCP returns ~590 bytes, ping replies.

---

## Three traps, in the order they will catch you

### 1. A connected-looking device may have a dead TCP link

**The app can show a fully populated device card without any TCP exchange at
all.** The UDP discovery reply already carries the IP, MAC, hostname, part
number and firmware version, so the device list, the name and the version can
all be right while nothing works.

Never read a populated device card as proof the link is good. Run the TCP check
above.

### 2. `SYSTem:REboot` leaves the device switched off

A soft reboot comes back in **STANDBY** (`powerState` 0) rather than
POWERED_UP (1). The MCU runs and answers SCPI over USB, but the radio and the
front end are unpowered — the unit looks dead and reports a nonsense IP (it
returns the gateway address). This is not a DHCP failure; the radio has no power.

Filed as
[daqifi-nyquist-firmware#1071](https://github.com/daqifi/daqifi-nyquist-firmware/issues/1071).

**Recovery, over USB, no trip to the bench:**

```python
import serial, time
s = serial.Serial("/dev/cu.usbmodem2101", 115200, timeout=2)
s.dtr = True; time.sleep(0.5)                 # DTR must be asserted
s.write(b"SYSTem:ECHO -1\r\n");        time.sleep(0.5)
s.write(b"SYSTem:POWer:STATe 1\r\n");  time.sleep(2)
s.close()
```

WiFi re-associates about 35 seconds later. **Always check `SYSTem:POWer:STATe?`
before concluding a unit is wedged** — this masquerades as the wedge below.

### 3. The network stack can wedge while USB stays alive

Seen twice. The device stops serving the network entirely while the application
firmware keeps running:

| Probe | Wedged |
|---|---|
| ICMP ping | 100% loss |
| UDP discovery | no reply |
| TCP connect to 9760 | **succeeds in ~7 ms** |
| SCPI over that socket | 0 bytes |
| SCPI over **USB** | fully normal |

The TCP handshake completing is what makes this confusing — the WINC does
handshakes on its own, so a connection "succeeds" over a link that serves
nothing. `LAN:GETChipInfo?` also still answers over USB, so the PIC32 and the
WINC are both alive; only the datapath is dead.

Filed as
[daqifi-nyquist-firmware#881](https://github.com/daqifi/daqifi-nyquist-firmware/issues/881).

**Recovery: power-cycle the unit.** Try `POWer:STATe 1` first in case it is
actually trap 2, but a real wedge needs the power switch.

---

## Useful facts

**One TCP client at a time.** The device accepts a second connection and then
closes it. So a probe from your laptop while the phone is connected returns zero
bytes and looks like a dead device. Disconnect the app before probing.

**Enable channels before starting a stream.** By hand,
`SYSTem:StartStreamData` on its own returns `**ERROR: -221, "Settings conflict"`
when no analog channel is enabled. The app does this for you; a manual probe
must not forget it.

```
ENAble:VOLTage:DC 65535        # bitmask, all 16 analog channels
SYSTem:STReam:FORmat 0         # protobuf
SYSTem:StartStreamData 10      # Hz
```

**Discovery is two UDP payloads** to port 30303 on the *subnet* broadcast
(`192.168.1.255`, not `255.255.255.255` — Android drops the limited broadcast):

```
DAQiFi?\r\n
Discovery: Who is out there?\r\n
```

**Useful queries** (USB or TCP):

| Command | Tells you |
|---|---|
| `SYSTem:POWer:STATe?` | 0 = STANDBY, 1 = POWERED_UP, 2 = partial/low battery |
| `SYSTem:StreamData?` | 1 while streaming |
| `SYSTem:STReam:INTerface?` | 0=USB 1=WiFi 2=SD 3=USB+SD |
| `SYSTem:COMMunicate:LAN:GETChipInfo?` | WINC chip id and firmware |
| `SYST:ERRor?` | the SCPI error queue |

**Serial gotcha.** Closing the serial port immediately after `write()` drops the
command — no flush, no delay, and DTR de-asserts. It looks exactly like the
device ignoring a valid command while reporting `0,"No error"`. Sleep before
`close()`, and confirm device-side state (`SYSTem:StreamData?` going 1 → 0)
rather than inferring from the app's behaviour.
