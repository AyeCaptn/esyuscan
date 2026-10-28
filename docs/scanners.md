# Scanners, custom paper and scan areas

esyuscan uses the unmodified NAPS2 SDK 1.6.0 drivers. **Refresh** searches both
**AirScan / eSCL** (network discovery) and **SANE / USB** (installed SANE backends,
which can also expose network scanners). The scanner picker shows the driver;
hover for its ID. **Settings → Scanner discovery details** gives each driver's
count or an actionable failure. One
driver failing does not discard devices found by the other. A device exposed by
both drivers stays as two choices: they may report different capabilities.

Scanner identity is always driver + exact ID. Display names do not merge devices.
Saved choices and presets wait for capabilities for that identity. A changed
USB bus/device ID may require selecting or adding the new ID; esyuscan does not
silently bind the old settings to another scanner with the same name.

## Non-blocking startup and remembered devices

esyuscan restores discovered identities from `scanners.json` beside its preferences,
plus the last selected scanner and saved manual addresses. Discovery, the selected
device's live capability check, and OCR detection run independently on background
workers. Slow device searches do not disable new/open sessions, imports, page
editing, export, or Settings. A remembered scanner can become ready before the
full device search completes. Only scanning/settings that need live capabilities
wait for that selected device's check.

Refreshes retain unobserved/offline devices and do not reset an active selection
or user-edited settings. The status beneath the picker marks checking/unverified
devices; the Refresh buttons show their own activity. Cached identity is never
treated as proof that a scanner is online. Capabilities are not cached, and every
physical scan still revalidates its exact device and settings. Printer discovery
is on demand when Print is requested, never part of startup.

The versioned cache stores only driver, exact ID, and name, with private file
permissions and atomic replacement. Corruption or write failures appear in the
discovery report without blocking document work or changing session files.

## Add a manual scanner

Open **Settings → Manual scanner address**. Choose the driver, enter
the address, optionally name it, then **Validate and save**. This reads
capabilities, without starting a scan. Only a successful probe is saved.

- AirScan accepts a hostname, IPv4 address, bracketed IPv6 address, or HTTP/HTTPS
  URL, with an optional port. For example `scanner.local`, `192.0.2.4:8080`, or
  `https://scanner.local:8443/eSCL/`. A bare host uses HTTP and `/eSCL/`.
  A supplied path is retained, including its case; use the scanner's actual
  endpoint for devices with a different path. Credentials, query strings,
  fragments, control characters and invalid ports are rejected before connecting.
  esyuscan retains the SDK's default connection security policy; select HTTPS in the
  address if required. Do not put passwords or tokens in saved addresses.
- SANE accepts an exact backend device ID, for example
  `genesys:libusb:001:004`, `net:scanner.local:genesys:libusb:001:004`, or the
  exact ID reported by your backend's read-only device-list tool. A bare IP or
  `/dev/usb/...` path is not a backend ID. Backend-specific names may contain spaces.
  esyuscan does not edit SANE configuration or enable a sharing server.

Saved manual devices are appended even when discovery finds nothing, including
after restart. They are revalidated when selected and before scanning; a saved
address is not evidence that a device is currently reachable. **Forget selected
address** removes the configured address and its cached identity/last selection,
while preserving presets and sessions.
A subsequent discovery can still find that device. Explicitly saving the same
driver/address updates its name rather than creating another saved entry.

**Cancel operation** / Escape cancels foreground work such as address validation.
Background startup/refresh checks do not use that app-wide busy state. Switching
scanners supersedes the previous capability request, and closing the window
cancels its background work without waiting for discovery. Late results are
ignored. Capability queries have a 20-second cancellation deadline; some native
SANE calls must return before their worker can stop. Each worker owns its native
context until it finishes; it is never disposed underneath an active call.
Check USB power, backend, firmware, permissions or network reachability if a
backend does not return. No startup/discovery operation starts a physical scan.

## Paper, alignment and area

Source, color, DPI, size and geometry are checked against the selected source,
not the union of flatbed/feeder/duplex capabilities. The SDK's reported positive
DPI values are offered; missing color/DPI/source capabilities disable scanning.
Every job reads capabilities again before calling the SDK scan API. A device
whose capabilities changed fails before starting a job rather than sending the
old settings. NAPS2 still performs its own protocol/profile negotiation.

A4 and Letter remain available when they fit the SDK's bounds (the SDK permits
1% tolerance for reported standard sizes). If size bounds are missing, these
standard sizes remain available with a warning, as in earlier builds.

Choose **Custom** in **Page size**, then open **Settings → Paper geometry / area** to enter
width and height. Custom is offered only when the source reports custom-size
support and finite positive bounds. Small scanners can default to Custom at their
reported maximum size. Custom dimensions must stay within those bounds and be
positive, at most 2000 mm. Missing limits never authorize an unbounded custom
request. Unsupported restored custom paper falls back to a fitting standard size
(or the reported custom maximum) with an explanation; a preset is not rewritten.

Units can be **mm** or **in**. Switching units converts the numbers rather than
reinterpreting them. Decimal entry follows the current locale. Preferences store
millimetres; conversion back from inches rounds to six decimal places in mm,
far below a scanner pixel. Invalid or out-of-bounds text disables scanning and
preset saves without replacing the last valid preferences.

**Origin edge**, **Center**, and **Far edge** position the physical paper within
the source width. NAPS2 calls the zero-offset alignment `Right`, center `Center`,
and the opposite edge `Left`; esyuscan uses origin-based labels to avoid implying
that an on-screen image edge is a particular physical edge of every scanner.
The top of the paper is at the driver's minimum Y. Center/far-edge alignment
requires reported source bounds; Origin edge remains available when they are missing. SANE's SDK-reported bounds
are relative to its backend's minimum X/Y, rather than absolute backend units.

Enable **Keep selected scan area** for X/left, Y/top, width and height. X/Y are
relative to the top-left of the aligned paper, in the selected physical unit.
Coordinates must be nonnegative, dimensions positive, and the right/bottom edges
must remain within the selected paper. The scale diagram shows source bounds,
aligned paper, its origin, and the kept region. Turn off area selection to keep
the whole paper. Changing paper or source can invalidate an existing region;
esyuscan explains the error and requires correcting it rather than silently clipping.

The public SDK does not expose arbitrary origins consistently across these
drivers. esyuscan therefore asks NAPS2 to acquire the **whole aligned paper**, then
uses its public `CropTransform` to retain the chosen region **before** saving a
new session original. Area selection does not reduce scanner travel or feeder
movement. The crop uses the response's horizontal/vertical DPI (requested DPI if
absent), rounds physical pixel edges, and refuses an empty or out-of-response
region rather than stretching it. Auto-deskew remains available and occurs before
this crop on the same fixed-size canvas. This acquisition crop is baked into the
original; to expand it, scan again. Later page crop/deskew/rotation remain
undoable and never modify that original. Guided manual duplex uses the same
validated size/alignment/area options for each user-triggered pass.

## Runtime and packaging (Linux ARM64 and x64)

AirScan/eSCL uses NAPS2's managed protocol implementation and does not require
SANE. SANE/USB needs the distribution's native **`libsane.so.1`**, a compatible
backend, any required scanner firmware, libusb dependencies, and permission to
open the USB device. All native libraries must match the process architecture:
aarch64 for `linux-arm64`, x86_64 for `linux-x64`. esyuscan does not bundle SANE or
install drivers, firmware, packages, udev rules, or system configuration.

The Arch package declares `sane` as optional for USB/SANE support and
`sane-airscan` as optional for additional SANE network backends. A vendor backend
may be separate from those packages; check its architecture and model support.
The distribution manages licenses and native dependencies. There is no reason
to enable `saned` for local USB scanning. Network AirScan can be used while SANE
is unavailable. The existing GTK 3, HarfBuzz, fontconfig, native Wayland and bundled
PDFium requirements remain unchanged.

A missing library/backend, inaccessible device or offline manual address is
reported with the driver and remedial checks. Empty SANE discovery can also mean
an unsupported model, missing firmware or USB permissions; it does not prove a
hardware fault. `esyuscan check --json` reports driver counts/errors; `esyuscan devices
--json` retains its array output and adds `driver` alongside `id` and `name`.
These commands discover devices only. The GUI also lists configured addresses.

## SDK integration and validation

Pinned upstream source:
[NAPS2 SDK commit f914a4ec](https://github.com/cyanfish/naps2/tree/f914a4ec2af090aca4fa55eb3242ec91641482c3).
`ScannerDevices` isolates discovery, capability probes, cancellation and driver
errors. `ScanValidation` and `ScanSettings.ToOptions` are shared by normal and
guided scans. No SDK packages or source were modified.

SDK 1.6.0 `ScanController.Scan` retries an offline SANE ID by display name. esyuscan
passes an empty *transport-only* name on SANE jobs, retaining driver and ID.
Upstream SANE discovery always appends a backend suffix to its names, so no
candidate can match the empty name. Saved identities and UI names stay intact.
Tests bind the installed SDK's name formatter and clone operation to guard this
assumption; recheck this adapter when upgrading NAPS2.

Preferences version 1 gains optional `ManualDevices` and per-setting `Geometry`
fields; old preferences without them load unchanged. Geometry is included in
last settings and presets. Session schemas, locks, atomic saves and history are
unchanged. Invalid geometry edits do not enter session history. All three output
workflows remain accessible, and source-preserving import/export is unaffected.

Automated tests use fake drivers and generated images to cover aggregation,
manual validation, cancellation/timeouts, identity guards, capability changes,
source-specific limits, geometry conversion, pixel crops, preferences/restart,
old session loading, SDK option mapping and tiled-window controls. They never
start a real scanner/printer job. Remaining physical checks:
SANE/USB backend and permissions on native ARM64/x64, each model's physical
origin/alignment and DPI metadata, custom sizes on flatbed/feeder, selected areas
with auto-deskew, cancellation responsiveness, and guided duplex reload/orientation.

## Integration handoff

This branch adds optional preferences fields without changing the version or
session schema. Likely merge overlap points are `MainWindow.axaml` (scanner
sidebar and Settings menu), `MainWindow.axaml.cs` (discovery and the two scan entry points),
`MainViewModel.ScanSettings.cs`, the `ScanSettings` record and `Scanner` constructor,
CLI diagnostic output, README, THIRD-PARTY and PKGBUILD (release 4 and optional
SANE dependencies). Keep both scan entry points using `CurrentScanSettings()`
and preserve the scan-time capability check and exact SANE identity guard.
The import/export algorithms and `PageRenderer` are unchanged.

Validation: locked dependency restore; `make build`; focused `make test` runs;
`make format`; `make check-all` (including `make check`, full generated-file,
fake-driver and headless UI tests, dependency audit, desktop entry and shell
checks). The completed suite has 220 tests and zero skips. Native ARM64/x64
physical scanner validation and package installation were deliberately not run.
