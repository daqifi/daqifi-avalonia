# Runbook — testing the Android head on a real phone

**For:** anyone who needs to build `Daqifi.Avalonia.Android`, put it on a phone,
and drive it.

Everything here was done on a Mac with a **Samsung Galaxy A16 5G (SM-A166U1)**,
Android 14, on 2026-09-12. Other phones and Android versions will differ in the
menu names, not in the commands.

If the DAQiFi device is the thing misbehaving rather than the app, go to
[nyquist-bench-notes.md](nyquist-bench-notes.md) instead. Read that page's first
section before you conclude the app is broken — it saves an hour.

---

## 1. One-time Mac setup

You need the Android SDK. It is not part of the .NET `android` workload, and
without it the head restores but does not build.

```bash
brew install --cask android-commandlinetools

export ANDROID_HOME=/opt/homebrew/share/android-commandlinetools
sdkmanager --licenses          # accept all
sdkmanager --install "platform-tools" "platforms;android-36" "build-tools;36.1.0"
```

Put these in your shell profile:

```bash
export ANDROID_HOME=/opt/homebrew/share/android-commandlinetools
export PATH="$ANDROID_HOME/platform-tools:$PATH"
```

Check it worked:

```bash
adb version          # 1.0.41 / 37.0.1 or newer
java -version        # needs a JDK; Corretto 21 is known good
```

`platforms;android-36` matches the `net10.0-android36.0` target framework in
`Daqifi.Avalonia.Android.csproj`. If that TFM is bumped, install the matching
platform.

---

## 2. One-time phone setup

**1. Enable Developer options** — Settings → About phone → Software information
→ tap **Build number** seven times.

**2. Settings → Developer options:**

| Setting | Value | Why |
|---|---|---|
| USB debugging | ON | required |
| Stay awake | ON | stops the screen sleeping mid-test |
| Window animation scale | Animation off | |
| Transition animation scale | Animation off | |
| Animator duration scale | Animation off | makes screenshot-then-tap reliable instead of racing fades |

**3. Turn OFF Auto Blocker** — Settings → Security and privacy → **Auto Blocker**.

This one is not optional on a Samsung. It is on by default and it blocks both
sideloaded installs and USB connections while the phone is locked. It is the
usual reason `adb install` fails on a new Galaxy.

**4. Plug into the Mac** with a **data** USB-C cable, phone unlocked. Tap
**Allow** on "Allow USB debugging?" and tick *Always allow from this computer*.

```bash
adb devices -l       # expect: <serial>  device  model:SM_A166U1
```

If nothing appears, check the Mac's USB tree before blaming adb:

```bash
system_profiler SPUSBDataType | grep -i samsung
```

Absent there means it is physical — cable, port or a dock that does not pass
data. A charge-only USB-C cable looks exactly like a dead port.

---

## 3. Build and install

```bash
export ANDROID_HOME=/opt/homebrew/share/android-commandlinetools

~/.dotnet/dotnet build Daqifi.Avalonia.Android/Daqifi.Avalonia.Android.csproj \
  -c Debug \
  -p:AndroidSdkDirectory="$ANDROID_HOME" \
  -p:EmbedAssembliesIntoApk=true

adb install -r Daqifi.Avalonia.Android/bin/Debug/net10.0-android36.0/com.daqifi.app-Signed.apk
```

Two things that are easy to get wrong:

- **Use `~/.dotnet/dotnet`, not the one on `PATH`.** `global.json` pins SDK
  10.0.302 with `rollForward: disable`; Homebrew's dotnet does not have it.
- **`-p:EmbedAssembliesIntoApk=true` is mandatory for a sideloadable Debug
  build.** Fast Deployment otherwise ships an APK with no assemblies in it. The
  csproj comment says the same.

A clean Debug build takes about two minutes and produces a ~92 MB APK. Restore
does not modify any `packages.lock.json`, so the local loop is CI-safe.

Launch it:

```bash
ACT=$("$ANDROID_HOME/build-tools/36.1.0/aapt2" dump badging \
      Daqifi.Avalonia.Android/bin/Debug/net10.0-android36.0/com.daqifi.app-Signed.apk \
      | sed -n "s/.*launchable-activity: name='\([^']*\)'.*/\1/p")
adb shell am start -n "com.daqifi.app/$ACT"
```

**Do not hardcode the activity name.** It is a mangled Java name
(`crc64….MainActivity`) that changes when the SDK or workload is bumped — the
same hazard the manifest comments call out for the USB intent-filter and the
foreground service. Read it from the APK each time, as above.

---

## 4. Driving the phone

Two views of the screen. Use both — they answer different questions.

**What it looks like:**

```bash
adb exec-out screencap -p > shot.png
```

**What is on it, with tap targets:**

```bash
adb exec-out uiautomator dump /dev/tty | tr -d '\r' > tree.xml
```

Each element has `text=` / `content-desc=` and `bounds="[x1,y1][x2,y2]"`. Tap
the centre of the bounds:

```bash
adb shell input tap <x> <y>
adb shell input text "hello"
adb shell input keyevent KEYCODE_BACK
```

### ⚠️ The 96-pixel offset

**Avalonia's accessibility tree reports Y coordinates about 96 px above where
the element actually is on screen.** 96 px is the status-bar inset on this
phone: the app's window starts at y=96 (`mAppBounds=Rect(0, 96 - 1080, 2205)`),
and the tree measures from the window while `input tap` measures from the
screen.

So: **add the status-bar height to every Y from the tree before tapping it.**

```
tree says   'Logged Data' centre y=2033   →   adb shell input tap 405 2129
```

Get the inset for the phone you are on rather than assuming 96 — it is the top
of the app's own window bounds:

```bash
adb shell dumpsys activity com.daqifi.app \
  | grep -m1 -oE 'mAppBounds=Rect\(0, [0-9]+' | grep -oE '[0-9]+$'
# 96
```

Symptom if you forget: taps land just outside the control and nothing happens,
which reads exactly like a broken button. Verify a tap by its effect, not by
assuming it landed.

Two more notes on the tree:

- **System dialogs are not in it.** A runtime-permission prompt is plainly
  visible in a screenshot and entirely absent from the dump. Read permission
  state from `adb shell dumpsys package com.daqifi.app` instead.
- **Buttons whose content is an icon + label report their content class name**
  (`Avalonia.Controls.StackPanel`) rather than their label — see
  [#356](https://github.com/daqifi/daqifi-avalonia/issues/356). Match on the
  adjacent `TextBlock` until that is fixed.

### Rotation

```bash
adb shell settings put system accelerometer_rotation 0   # disable auto-rotate
adb shell settings put system user_rotation 1            # 0=portrait 1/3=landscape
adb shell settings put system user_rotation 0            # put it back
```

Record `accelerometer_rotation` before you change it, and restore it after.

---

## 5. Reading logs

**The app's own log** is the most useful one, and it is on the device. A Debug
build lets you read it directly:

```bash
adb shell run-as com.daqifi.app \
  cat /data/data/com.daqifi.app/files/DAQiFi/Logs/DAQifiAppLog.log | tail -40
```

This is where connect failures land, with the exception type and stack. Note
that `AppLogger.AddBreadcrumb` goes to Sentry, **not** to this file, so a missing
breadcrumb proves nothing.

**Android logs**, filtered to the app's process — an unfiltered grep drowns in
other processes:

```bash
PID=$(adb shell pidof com.daqifi.app | tr -d '\r')
adb logcat -d | awk -v p="$PID" '$3==p' | tail -30
adb logcat -d -b crash            # empty is what you want
```

The in-app **Logged Data → APP LOGS** tab is a list of recorded streaming
sessions, not a diagnostic log. It will not show you any of the above.

---

## 6. Checking the Android-specific machinery

These are the parts that only a real phone can prove. All verified working on
2026-09-12.

**Discovery holds a MulticastLock** — without it Android power-save-filters the
UDP broadcast replies and discovery silently finds nothing.

```bash
adb shell dumpsys wifi | grep -A3 "Multicast Locks held"
#   Multicaster{daqifi-discovery uid=NNNNN}      ← during a scan
adb shell dumpsys wifi | grep -E "mMulticastEnabled|mMulticastDisabled"
#   counters should end up equal after you stop scanning
```

**Streaming holds a WiFi lock and runs a foreground service:**

```bash
adb shell dumpsys wifi | grep "daqifi:streaming"
#   WifiLock{daqifi:streaming type=4 ...}        ← type 4 = FULL_HIGH_PERF

adb shell dumpsys activity services com.daqifi.app | grep -E "isForeground|types="
#   isForeground=true   types=00000010           ← CONNECTED_DEVICE
```

**Background streaming survives the app going off-screen:**

```bash
adb shell input keyevent KEYCODE_HOME
sleep 30
adb shell ss -tn | grep 9760                     # expect ESTAB throughout
```

Measured: 16 channels at 100 Hz gave 1,600 samples/s with no loss across a 30 s
background window (19,552 samples → 117,408 over ~61 s).

---

## 7. Known issues you will notice

| | |
|---|---|
| [#356](https://github.com/daqifi/daqifi-avalonia/issues/356) | icon+label buttons announce `Avalonia.Controls.StackPanel` to accessibility instead of their label |
| [#357](https://github.com/daqifi/daqifi-avalonia/issues/357) | landscape shows a 96 px white band down the cutout edge |
| [#358](https://github.com/daqifi/daqifi-avalonia/issues/358) | the silent-stream watchdog does not fire |
| [#359](https://github.com/daqifi/daqifi-avalonia/issues/359) | discovery keeps running after connect, holding the MulticastLock |
