# Play Store listing copy — DAQiFi

Paste-ready. Character counts verified against Play limits.

Adapted from `daqifi-android`'s `qa-captures/store/listing-copy.md`, which was written for the
native app. That file was gitignored and had never been committed to any branch, so it existed
only on one developer machine — it is committed here because this repo is now the one that ships.
See "What changed from the native app's copy" at the bottom for the three claims that were not
true of this build.

---

## App name (30 max)

```
DAQiFi
```
`6 / 30`

---

## Short description (80 max)

```
Stream and record live data from your DAQiFi Nyquist over Wi-Fi.
```
`64 / 80`

Alternates, if the first reads too flat:

```
Live data acquisition from your DAQiFi Nyquist, straight to your phone.
```
`71 / 80`

```
Connect to a DAQiFi Nyquist over Wi-Fi. Plot live. Record to your phone.
```
`72 / 80`

---

## Full description (4000 max)

```
DAQiFi turns your Android phone into a front end for DAQiFi Nyquist wireless
data-acquisition hardware. Connect over Wi-Fi, configure your channels, watch
signals live, and save recordings straight to your phone.

REQUIRES DAQiFi HARDWARE
This app is a companion for DAQiFi Nyquist devices. It is not a standalone
measurement tool and does nothing useful without a Nyquist on your network.
Hardware is available at daqifi.com.

DISCOVER AND CONNECT
• Nyquist devices on your Wi-Fi network are found automatically
• Or join the device's own DAQiFi-XXXX access point and connect directly,
  with no router involved
• Manual IP entry for fixed-address setups
• Works with more than one device at a time

CONFIGURE YOUR CHANNELS
• Enable and disable analog input channels individually
• Set sample frequency
• Per-channel range and scaling
• Digital I/O configuration

SEE YOUR DATA
• Live plotting while the device streams
• Numeric readouts for every enabled channel
• Plot several channels together, or focus on one
• Acquisition keeps running when you switch away from the app

RECORD
• Save streamed data to your phone as CSV
• Export to a location you choose, ready to share or open on a computer

PRIVACY
The app talks only to your Nyquist over your local network. There are no
accounts, no ads, and no advertising or analytics tracking. The app sends
anonymous crash diagnostics so we can find and fix defects; no personal
information is attached to them.

SUPPORTED DEVICES
Nyquist 1 (16 analog inputs, 16 digital I/O). Nyquist 2 and 3 share the same
protocol family.

Questions or problems: daqifi.com/contact
```
`1616 / 4000`

---

## Graphics

| File | Notes |
|---|---|
| `play-icon-512.png` | 512x512, fully opaque as Play requires. Device glyph on brand blue. |
| `play-feature-graphic-1024x500.png` | Brand-blue gradient, device glyph, wordmark, tagline. **Repaired here** — the inherited version had the tagline truncated at the canvas edge ("Record to your p"). The band was repainted by interpolating the gradient from the clean rows above and below, and the text redrawn at 21px so it fits with a symmetric right margin. |

Screenshots are NOT in this folder. Play caps phone screenshots at **2:1**; raw captures from the
bench A16 are 1080x2340 (2.17:1) and are rejected unless cropped to at most 1080x2160.

---

## Notes for whoever pastes this

- **Play strips most formatting.** Plain text with `•` bullets survives; markdown does not.
- The **"REQUIRES DAQiFi HARDWARE"** paragraph is deliberately near the top. This app is useless
  without a device, and being blunt up front is the cheapest defence against one-star
  "doesn't work / won't connect to anything" reviews from speculative installs.
- The privacy paragraph must stay consistent with the Data safety answers and the published
  privacy policy. Play checks them against each other.
- Update the supported-devices line once Nyquist 2 / 3 are hardware-verified.

---

## What changed from the native app's copy

Three claims in the inherited text were not true of this build.

**1. The location-permission explanation — removed.**
The native app scanned for Wi-Fi SSIDs, which Android gates behind location permission, so its
listing had to explain why a data-acquisition app wanted location. This build requests **no
location permission at all** (verified against the shipping artifact with `aapt2 dump badging`).
Discovery is UDP broadcast on the joined network, confirmed working on hardware with no location
granted. Explaining a permission the app does not request would only confuse.

**2. "no accounts, no analytics, and no ads" — reworded.**
Sentry crash reporting is now active, and the Data safety form declares Crash logs and
Diagnostics. Claiming "no analytics" beside that declaration reads as a contradiction. The
replacement text distinguishes tracking (none) from crash diagnostics (yes, anonymous).

**3. The OPEN SOURCE paragraph — removed.**
It advertised GPL-3.0 with source at `github.com/daqifi/daqifi-android`. That is the wrong repo
for this app, and `daqifi/daqifi-avalonia` is currently **private with no LICENSE file**. Restore
the paragraph only once the repo is public and licensed, pointing at the right URL.

Two further claims were softened rather than removed, because they are plausible but were not
verified on this build:

- **Pinch and drag to zoom the time axis** — dropped. Not tested on the mobile plot.
- **"Recordings land in your Documents folder"** — replaced with "Export to a location you
  choose", because export goes through a file picker rather than writing to a fixed folder.

Both are quick to confirm on the bench, and worth confirming before promising them in a listing.
