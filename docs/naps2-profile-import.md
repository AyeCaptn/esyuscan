# Import NAPS2 scan profiles

Open **Scanner settings → Save / manage presets → Import NAPS2 profiles…**.
Choose a detected file and **Preview selected file**, or use **Choose profiles.xml…**
for another location, a backup, or a portable NAPS2 installation. Nothing is read
until you choose a file to preview. Detection only checks whether standard paths
exist; it does not start NAPS2, discover scanners, or search your disk recursively.

The preview shows every profile's name, driver, device ID, source, color,
resolution and paper size. Invalid profiles remain visible with an explanation
and cannot be selected. Expand each profile's **notes / options not applied** to
review omitted/defaulted settings and unmapped options. Choose the profiles you
want, confirm that you reviewed the mappings, then **Import selected**. For four
profiles, leave all four selected to import them together. Cancel saves nothing.

Import writes only esyuscan's saved presets. It never applies a preset, changes the
last-used scan settings, starts a job, changes a session, or modifies the source
XML. Select an imported preset and choose **Apply** separately when ready.
The user's real profiles are intentionally not imported during development;
tests use four fictional profiles and temporary esyuscan preferences.

## Discovery and supported formats

Detected paths include `$XDG_CONFIG_HOME/naps2/profiles.xml`,
`~/.config/naps2/profiles.xml`, the platform ApplicationData directory with
`naps2`/`NAPS2`, and `../Data/profiles.xml` relative to esyuscan's executable directory
(the layout used by portable NAPS2 when the applications are colocated).
Windows NAPS2 normally uses `%APPDATA%\NAPS2\profiles.xml`. Portable copies
elsewhere and application-level profiles are accessible through the picker.

esyuscan reads NAPS2's current `ArrayOfScanProfile` format (profile versions 0–2 or
omitted) and the legacy `ArrayOfExtendedScanSettings` and `ArrayOfScanSettings`
collections, including `xsi:type="ExtendedScanSettings"`. Newer profile versions,
foreign roots/namespaces and unknown polymorphic profile types are rejected.
No NAPS2 GUI assemblies or additional dependencies are required.

The parser prohibits DTDs and external entities and performs no XML network
resolution. It limits file size/document characters to 4 MiB, depth to 32, and
profile count to 256. Duplicate setting elements are rejected as ambiguous.
A malformed XML document rejects the file; an invalid individual profile leaves
valid siblings available. Missing names, device IDs, ambiguous/default drivers,
invalid resolutions, and unknown source/color/size values block that profile.

## Mapping and limitations

| NAPS2 field | esyuscan setting |
| --- | --- |
| `DriverName` (or legacy `Device/DriverName`) | `Escl`, `Wia`, `Twain`, `Sane`, or `Apple`, preserved exactly by driver type |
| `Device/ID`, `Device/Name` | Scanner identity; display names are never used for matching |
| `PaperSource`: Glass / Feeder / Duplex | Flatbed / Feeder / Duplex |
| `BitDepth`: C24Bit / Grayscale / BlackWhite | Color / Grayscale / BlackAndWhite |
| `Resolution`: DpiN | Integer DPI; scanner support checked when applied |
| `PageSize`: A4 / Letter | esyuscan's existing A4 / Letter choices |
| Legal, A5, A3, B5, B4, Custom, SystemDefault | Original size token retained; preview warns that esyuscan currently offers A4/Letter |

For missing basic settings, esyuscan uses the upstream NAPS2 profile defaults
(Glass, C24Bit, Dpi200, Letter) and lists each default in the preview. It does not
infer a missing driver or device ID. Legacy TWAIN version-1/native-only WIA
behavior is reported: esyuscan uses predefined settings and has no native scanner UI.

esyuscan discovers AirScan/eSCL and SANE devices. Other driver identities can be
saved, but remain unavailable in this Linux build.
Even the same physical scanner through another driver is a different identity.
An imported preset requires an **exact driver and device ID match**. If absent,
no scanner is selected and scanning stays disabled. Refresh can recover the
matching device. To use another scanner, explicitly choose it, review the
validated settings, and optionally **Update** the preset. The original imported
preset's identity stays intact until you explicitly update it. Exact matching
also applies to last-used settings remembered after applying an imported preset.
Ordinary pre-existing esyuscan presets keep their existing fallback behavior.

Applying a matching preset uses esyuscan's existing source-specific capability
validation. Unsupported source/color/DPI/size choices receive the same visible
substitution explanations as other presets; the saved preset retains its intent.
Imports do not set the global Auto-deskew checkbox. All settings outside the table
are listed as not applied, even if their values equal NAPS2's defaults. Examples
include alignment, custom dimensions, native UI, auto deskew/crop, rotation,
brightness, contrast, scaling, blank-page removal, auto-save and driver options.
Default-profile flags, icons and cached capabilities are metadata and are not
imported. Unmapped device connection/icon fields are reported too.

## Duplicates, persistence and integration

Duplicate names compare case-insensitively, both against existing esyuscan presets
and within the selected batch. The default **Skip existing names** leaves them
untouched. **Keep both** appends ` (2)`, ` (3)`, etc., shortening an 80-character
name only to accommodate the suffix. **Replace matching names** retains the
existing preset ID; the last selected profile with that name wins. The preview
shows each outcome before confirmation. Reimports use the same policy; import
never silently overwrites a matching preset.

The complete selected batch uses the existing flush-and-atomic-rename preferences
store once. On failure, no imported presets enter the in-memory list and the
dialog remains open for retry/cancel. Source XML is always opened read-only.
Preferences remain version 1 with optional `ImportedProfile` metadata and
`RequireExactDevice` on scan settings; existing files load with their old behavior.
As before, separate esyuscan processes use last-successful-write preference semantics.

`ImportedProfile` retains the source name, warnings, and unmapped option XML as
inert JSON strings. These strings are never deserialized into SDK types or used
as paths/commands. Custom paper and alignment XML remain reviewable import notes rather than
being automatically applied. Set these through the integrated geometry controls
and update the preset after checking the scanner capabilities. Imported settings and notes can contain device IDs,
network addresses, or original auto-save paths; preferences have the same private
file permissions as other saved settings.

Integration overlap: `ScanPreferences.cs` adds optional record properties;
`MainViewModel.ScanSettings.cs` adds atomic batch import and exact-device handling;
the main preset editor adds one button. Discovery support for additional drivers
and geometry's supported size catalog should reuse the preserved identity and
raw option metadata, then continue through normal capability validation.
Session manifests, rendering, OCR, exports and CLI are unchanged.

Automated validation uses generated current/legacy XML, four-profile UI import,
selection/cancel/duplicate flows, narrow-window rendering, unsafe/malformed XML,
missing devices, capability substitutions, restart, and failed atomic saves.
No physical scanner/printer jobs or real preference writes are performed. Native
file picker behavior and physical scanner use remain manual platform validation.

Upstream references inspected for this implementation:

- [Profile settings and standard file locations](https://www.naps2.com/doc/profile-settings)
- [User/application profile files](https://www.naps2.com/doc/org-use)
- [ScanProfile fields and defaults](https://github.com/cyanfish/naps2/blob/master/NAPS2.Lib/Scan/ScanProfile.cs)
- [ProfileSerializer legacy upgrades](https://github.com/cyanfish/naps2/blob/master/NAPS2.Lib/Config/ProfileSerializer.cs)
- [ScanResolution XML encoding](https://github.com/cyanfish/naps2/blob/master/NAPS2.Lib/Scan/ScanResolution.cs)
- [Upstream path discovery](https://github.com/cyanfish/naps2/blob/master/NAPS2.Lib/Paths.cs)
