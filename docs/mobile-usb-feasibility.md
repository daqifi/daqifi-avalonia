# Phone → DAQiFi over USB (OTG): SD offload + lossless streaming

**Status:** feasibility confirmed from source + Android platform facts; **not yet
validated on device** (needs the phone + a USB-C OTG cable + a device).

**Bottom line**

| Capability | Verdict | Notes |
|---|---|---|
| **SD-card log offload over USB (Android)** | **Feasible** | The whole SD path already exists in Core and is USB-gated; only a new Android USB transport + device/manifest/UI wiring is needed. No ViewModel changes. |
| **Lossless USB streaming (Android)** | **Feasible for "lossless"; partial for "higher-speed"** | USB bulk is CRC'd, retransmitted, NAK-flow-controlled → genuinely lossless/back-pressured (unlike the WiFi/UDP path). Throughput is capped by the device's **Full-Speed** USB (~1 MB/s), not the phone. |
| **iOS** | **Not feasible** | No third-party USB host for non-MFi devices → stays WiFi-only (record as a divergence, sibling of `DIV-UI-003`). |

The mobile SD-offload UI already ships: `Views/Mobile/DeviceLogsMobileView.axaml`
binds `SelectedDevice.ConnectionType` + `ConnectionTypeMessage` and shows a
"SD-card offload needs a USB connection" callout over WiFi. It flips to the live
file list the moment a device connects with `ConnectionType.Usb` — which is what
the transport below delivers.

---

## 1. What the DAQiFi USB interface actually is

- **USB CDC-ACM (virtual serial), single function** — not raw bulk, **not**
  USB mass-storage. There is no device-side MSD; the SD card is never a mountable
  drive. Evidence: `daqifi-nyquist-firmware/firmware/src/config/default/usb_device_init_data.c`
  — one CDC function (`registeredFuncCount = 1`), CDC-ACM class/subclass, Comms
  interface (Interrupt IN EP1) + Data interface (Bulk OUT + Bulk IN EP2).
- **VID `0x04D8` (Microchip) / PID `0xF794`**, Product "Nyquist". **`iSerialNumber`
  index = 0** — no per-unit serial at USB enumeration, so an Android
  `device_filter.xml` and permission grant are per-VID/PID; disambiguate multiple
  units with `*IDN?` over the link, not USB iSerial.
- **Streaming is the same protobuf-over-serial on USB and WiFi.** The firmware
  encodes one `DaqifiOutMessage` and fans it out to USB, WiFi, and SD
  (`firmware/src/services/streaming.c`). Format is global (`SYSTem:STReam:FORmat`),
  not per-transport. One content nuance: USB streams **pre-scaled floats**
  (`AnalogInDataFloat`) while WiFi streams **raw ADC counts** — the decoder already
  handles both (`AbstractStreamingDevice.cs` decode branch), so no new scaling code.

### SD-card SCPI (already flows over both USB-CDC and WiFi/TCP at the firmware level)

| Purpose | SCPI string | Core producer |
|---|---|---|
| List files | `SYSTem:STORage:SD:LISt?` | `ScpiMessageProducer.cs:118` |
| Download file | `SYSTem:STORage:SD:GET "{file}"` | `ScpiMessageProducer.cs:130` |
| Delete file | `SYSTem:STORage:SD:DELete` | `ScpiMessageProducer.cs:164` |
| Enable SD | `SYSTem:STORage:SD:ENAble` | `ScpiMessageProducer.cs:100/109` |
| Free space / info / format | `...SD:SPACe?` / `...SD:INFO?` / `...SD:FORmat` | `ScpiMessageProducer.cs:175` |

Download appends the literal EOF marker **`__END_OF_FILE__`**
(`sd_card_manager.c`). The firmware routes SD replies back to whichever transport
issued the command — **so SD offload is not a firmware limitation over WiFi; the
USB-only gate is an app-side policy** (`DeviceLogsViewModel.CanAccessSdCard =>
SelectedDevice?.ConnectionType == ConnectionType.Usb`).

## 2. How the desktop does USB today

- App device `SerialStreamingDevice` (`ConnectionType => ConnectionType.Usb`) builds
  Core's `SerialStreamTransport(port, enableDtr: true)` → `CoreStreamingDevice`.
  **DTR must stay asserted or the device won't stream over USB.**
- The transport is the only USB-specific piece: `SerialStreamTransport.Stream`
  returns `SerialPort.BaseStream`, and the decoder
  (`StreamMessageConsumer<DaqifiOutMessage>(_transport.Stream, ProtobufMessageParser)`)
  is transport-agnostic. **Everything above `IStreamTransport` — the consumer,
  parser, `DaqifiDevice`, `AbstractStreamingDevice`, `DeviceLogsViewModel`,
  `LoggingManager` — works unchanged regardless of transport.**
- `System.IO.Ports` is referenced in the shared csproj but is a shim that throws
  `PlatformNotSupported` on Android — the USB transport below must **not** touch it.

## 3. Android USB-host feasibility (cited)

- **CDC-ACM works in userspace, no kernel driver.** `android.hardware.usb`
  (`UsbManager`/`UsbDeviceConnection.bulkTransfer`) drives the bulk endpoints
  directly. Manifest needs `<uses-feature android:name="android.hardware.usb.host"/>`
  + a `USB_DEVICE_ATTACHED` intent-filter + `res/xml/device_filter.xml`.
  ([Android USB host](https://developer.android.com/develop/connectivity/usb/host))
- **.NET for Android exposes it as `Android.Hardware.Usb.*`** — CDC-ACM can be
  implemented in C# with no Java. A maintained C# port of the reference driver
  exists: **`UsbSerialForAndroid`** (NuGet `UsbSerialForAndroid`, MIT, .NET 10 /
  .NET-for-Android, generic CDC-ACM). The Java original `mik3y/usb-serial-for-android`
  is LGPL-2.1; the C# port's LICENSE reads MIT (worth a legal confirmation before
  shipping).
- **Samsung Galaxy A16 (dev phone) supports USB-C OTG host.** OTG is broadly
  supported on modern Android; requires an OTG adapter/cable.
- **Lossless: yes.** USB bulk is CRC-checked, retransmitted, and NAK-flow-controlled
  — the back-pressure the WiFi/UDP path lacks. **Speed: capped by the device**
  (PIC32 CDC bulk is USB 2.0 **Full-Speed**, ~1 MB/s ceiling), and the host app
  must drain the bulk-IN endpoint fast enough (>3 Mbit/s CDC can overrun a slow
  host reader). *(FS-vs-HS inferred from the CDC bulk topology + PIC32MZ PHY; confirm
  from the raw `bcdUSB`/`wMaxPacketSize` descriptor bytes.)*

## 4. Smallest viable implementation in this codebase

The transport abstraction means the entire port is **one new transport class plus
device/UI/manifest wiring** — no streaming or SD-pipeline rework.

1. **`AndroidUsbStreamTransport : IStreamTransport`** (Android head / DI platform
   service). Its `.Stream` adapts `UsbDeviceConnection.bulkTransfer` over the CDC
   data-interface bulk IN/OUT endpoints (wrap `UsbSerialForAndroid`, or implement
   CDC directly). **Must assert the CDC line state DTR=1** (the desktop's
   hardest-won USB detail) and **never touch `System.IO.Ports`**.
2. **A mobile USB device** mirroring `SerialStreamingDevice`: sets
   `ConnectionType.Usb`, builds `CoreStreamingDevice` over the new transport, and
   overrides `CoreDeviceForSd => CoreDevice` so the SD path is wired. Cleanest is
   to make `SerialStreamingDevice`'s transport injectable (an `IStreamTransport`
   ctor arg) with a mobile factory.
3. **Wire `ConnectionType.Usb` into the mobile UI.** `MobileShellViewModel` today
   builds only the WiFi `DaqifiStreamingDevice`. Add: enumerate/attach a DAQiFi USB
   device (VID `0x04D8`/PID `0xF794`), request permission, construct the USB device,
   register it (the same `ConnectionManager.RegisterConnectedDevice` bridge the WiFi
   path already uses). `DeviceLogsMobileView` then lights up automatically.
4. **Manifest** (`Daqifi.Avalonia.Android`): add `uses-feature usb.host`, the
   `USB_DEVICE_ATTACHED` intent-filter, and `res/xml/device_filter.xml`
   (`vendor-id="1240"` = 0x04D8, `product-id="63380"` = 0xF794).

**What lights up SD offload:** nothing in `DeviceLogsViewModel` changes. Once
`SelectedDevice.ConnectionType == ConnectionType.Usb`, `CanAccessSdCard` flips true
and the existing `RefreshFilesAsync`/`ImportFile` flow (which sends `SD:LIST?` /
`SD:GET`, parsing `__END_OF_FILE__`) runs over the USB transport.

### Divergence to record

**iOS: USB host is unavailable to third-party apps for non-MFi devices** (External
Accessory needs MFi certification). Record a divergence like `DIV-UI-003`
("mobile is WiFi/TCP only") — USB-on-mobile is **Android-only**; the Browser/WASM
head also has no path.

### Must be validated on device (untestable from source)

1. A16 enumerates `0x04D8/0xF794`; the attach/`requestPermission` grant survives replug.
2. **DTR actually starts the stream** over the new transport.
3. Sustained bulk-IN throughput at the target sample rate without host overrun.
4. SD round-trip over USB: `SD:LIST?` parse, `SD:GET` download, `__END_OF_FILE__` framing.
5. No `System.IO.Ports` instantiation on the Android path.
6. Confirm the device's USB speed (FS vs HS) from the actual descriptor.
