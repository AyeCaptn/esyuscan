# esyuscan guide

A focused scanning app for Omarchy. C# and Avalonia provide the interface;
the unmodified NAPS2 SDK handles scanning, image processing, and PDF import/export.
No webview, NAPS2 fork, cloud account, or background service.

The Arch package, executable and launcher are named `esyuscan`.

## Scan a session

1. Choose **New session**, give it a name, and choose its parent folder.
2. Select the scanner and settings. Use the flatbed repeatedly, or scan a stack
   through the feeder. Only capabilities reported by the scanner are offered.
3. Review the pages. Adjust brightness/contrast, sharpen, correct documents/noise, flip,
   deskew, crop, rotate, reorder, remove, undo, or import additional PDFs and images.
4. Export all pages, or enable **Selected pages only** for the highlighted selection.
   Choose **Separate images** (numbered PNGs, JPEGs or TIFFs), **Separate PDFs**,
   or **One document** (combined PDF or multipage TIFF). Each action opens format/options
   controls before the destination picker. PDF options include compression/quality,
    PDF/A, metadata, passwords and permissions; see [export options](export-options.md).
   The same session can be exported more than once without rescanning.
   Optionally enable **Searchable PDF (OCR)** and choose an installed OCR language
   for either PDF mode. Separate images and multipage TIFF contain pixels only. Use **Refresh OCR**
    after provisioning components; see [searchable PDF setup](searchable-pdf.md).

Each session has its own folder, `session.json`, and an `originals/` directory.
Completed pages and edits are saved automatically. **Open session** resumes them
after an app restart. Removing a page does not delete its original; undo history
lasts until the session is closed. Sessions use `.esyuscan.lock` to prevent
concurrent edits. Do not open the same session in older prototype builds at the
same time; those builds use a different lock filename.
Exports never overwrite existing files and are published only when complete.

## Page selection and preview

Ctrl+click toggles thumbnails, Shift+click selects a range, and Ctrl+A selects all.
The preview toolbar shows selection counts; an accent outline marks the focused preview.
The vertical thumbnail sidebar stays to the left of the document. Arrow keys or
Page Up/Down browse pages outside text fields and pickers; Home/End goes to the
first/last page. Shift extends selection and Ctrl+arrow moves only the preview focus.
Rotate and Remove apply to the selection in one undoable step. Crop and Deskew
apply to the focused page. Drag a selected thumbnail to move the whole selection,
or use Alt+Left/Right. Pages keep their document order inside the moved block.
Import retains selection; undo/redo recover selection and focus with the document.

All three output modes support **Selected pages only**, in current document
order, including source-preserving PDFs and optional OCR. The default remains all
pages. During manual-duplex review, browsing and zoom work while edits and export
remain blocked. Use **Fit page**, **+ / −**, Ctrl+0 / Ctrl+Plus / Ctrl+Minus, or
Ctrl+wheel to zoom; drag the preview to pan. Two-finger pinch zoom is supported
when the input backend supplies pinch events (including touchscreens). Avalonia's
current Wayland backend does not forward touchpad pinch; use Ctrl+wheel there.
Display zoom never changes crop
coordinates or export resolution. See [navigation behavior and integration notes](page-navigation.md).

## Remembered settings and presets

The left sidebar keeps scanner/preset selection, source, color, resolution,
page size, and scan/duplex/batch controls. Open **Settings** at the top right for
preset management/import, manual scanner addresses, paper geometry, auto-deskew,
scanner discovery details, system printer settings, and theme status.

esyuscan remembers the last selected scanner, paper source, color, DPI, paper
size, alignment, units and selected scan area. Valid changes are saved
automatically, independently of scan sessions.
The scanner is matched by the NAPS2 driver and device ID, so a renamed scanner
still matches and scanners with the same display name remain distinct.

Choose **Save…** beside the sidebar's preset picker (Ctrl+Alt+P), or open
**Settings → Save / manage presets**, then use **Save new** with a preset name to
save the current settings. Select a preset and choose **Apply** to load
it. **Rename** uses the name field;
**Update** replaces the selected preset's settings with the current settings;
**Delete** removes the selected preset. Names must be unique (ignoring case)
and contain 1–80 characters. Selecting a preset alone does not change settings.

Use **Import NAPS2 profiles…** in the preset editor to preview a detected
`profiles.xml` or choose a file explicitly. Select the profiles to import, review
the mapped settings and unsupported options, and choose how duplicate names are
handled (skip, keep both, or replace). Importing saves presets without applying
them or changing your active settings; source files remain untouched. Imported
presets require an exact driver/device ID match and leave scanning disabled when
their scanner is unavailable. Choose a different scanner explicitly if needed.
See [NAPS2 profile import](naps2-profile-import.md) for formats, mappings,
limitations, and integration notes.

Known scanners and the last selected scanner appear immediately at startup.
Discovery, the selected scanner's capability check, and OCR detection run in the
background without locking sessions, import, editing, or export. Scan becomes
available after the selected device's live capabilities validate its settings;
it does not wait for full discovery. Offline remembered devices remain listed,
with an unverified/checking status. A late refresh does not replace your current
selection or settings. A changed device ID is a different scanner, even if its
display name is unchanged.
Unsupported settings fall back to the first available source/color, 300 DPI
(or the lowest supported positive DPI), and A4 (or Letter if only Letter fits).
The explanation identifies each substitution. A4 and Letter are checked against
the SDK's reported scan area; if no area is reported, esyuscan warns that the paper
size needs verification. Custom paper is offered when the source reports bounds
and custom-size support. Missing color/DPI capabilities or no fitting supported
page size disables scanning. Applying a preset or editing settings remembers the
validated result without changing the saved preset; use **Update** for that.

Preferences live in `~/.local/share/esyuscan/preferences.json` (under
`$XDG_DATA_HOME/esyuscan` when configured). They contain no session pages or edits.
Writes flush to a temporary file in the same directory and atomically replace
the preferences file. A missing file uses defaults. An unreadable, corrupt, or
unsupported file shows an explanation and uses defaults; discovery alone keeps
the original file untouched. An explicit settings edit or preset save replaces
it. If saving fails, esyuscan reports the error and keeps current scan settings
usable; failed preset changes are not applied. Preferences are separate from
session locking; concurrent esyuscan windows use the last successful preferences
write. NAPS2 profiles are read only when chosen in the import dialog; they and desktop
settings are never modified.

Discovered scanner identities are cached separately in
`~/.local/share/esyuscan/scanners.json` (or `$XDG_DATA_HOME/esyuscan/scanners.json`).
This private, atomically replaced cache contains names, drivers, and exact IDs,
not capabilities or session pages. A missing/broken cache does not block startup;
successful discovery rebuilds it. Printers are queried only when **Print…** is
requested, not at startup.

## Scanner drivers and geometry

**Refresh** searches both AirScan/eSCL and SANE backends for USB/network scanners,
with separate driver reports under **Settings → Scanner discovery details**.
Add an undiscovered device under **Settings → Manual scanner
address** using an AirScan host/HTTP(S) URL or an exact SANE backend ID. Validation
reads capabilities only. Saved manual addresses survive restart and empty discovery.

Choose **Custom** page size and open **Settings → Paper geometry / area** for mm/inch
width/height, origin/center/far-edge alignment, and X/Y/width/height area selection.
The scale diagram shows source bounds, paper origin and kept region. Invalid
geometry disables scanning; settings are checked against each source and rechecked
before a job. Area selection acquires the whole aligned paper, then crops with
NAPS2 before saving the original, after optional auto-deskew. It cannot be expanded
later without rescanning. Guided duplex and all three export choices remain available.

SANE needs system `libsane.so.1`, model-specific backends/firmware and USB permissions
for the matching ARM64 or x64 architecture. AirScan works without SANE. esyuscan does
not install drivers or change system configuration. See [scanner setup and geometry](scanners.md)
for addresses, cancellation limitations, persistence and hardware checks.

## Guided double-sided scanning

For a single-sided feeder such as the HP OfficeJet Pro 8020, select **Document
feeder · a stack**, then **Guided double-sided…**. Scan fronts, wait for esyuscan's
reload instructions, reload the unscanned sides, and scan backs. esyuscan never
starts the second pass automatically.

The guide previews Front 1 / Back 1, Front 2 / Back 2, and so on. Select every
side in the page tray; use **Reverse back stack** and **Rotate all backs (°)**
to correct the second pass. Confirm the pairs and orientation, then **Apply
reviewed order**. Existing session pages stay first and unchanged. Applying is
one undoable action; undo restores fronts followed by backs, with their original
rotations. All three export modes remain available after finishing the guide.

Unequal counts block Apply. Scan only remaining sides if the continuation is
known, or choose **Keep scan order / exit** and correct missing sheets manually.
Cancellation, scanner errors, and reopening retain completed sides and recover
the reload/review step. See [the workflow and recovery guide](manual-duplex.md),
including the remaining HP reload/orientation hardware validation.

## Batch scanning and document separation

Choose **Batch setup / review…** to scan a set number of times with cancellable
seconds between scans and optional prompts. Use one document per scan, or QR /
Code 128 separator sheets with exact matching text. Review every captured page,
correct document boundaries and separator flags, choose whether to include those
sheets (included by default), then export numbered document PDFs in a new folder.
No sheet is deleted from the session. Partial scans and interrupted batches pause
for an explicit recovery choice; opening a session never restarts physical jobs.
The three existing export buttons remain available after review and export all
session pages, including separators. See [batch setup, recovery, grouping and
export details](batch-scanning.md).

## Deskew and crop

**Settings → Auto-deskew scans** passes `ScanOptions.AutoDeskew` to NAPS2 SDK 1.6.0.
It is off by default and affects subsequent scans. The SDK straightens the scan
before esyuscan saves its first PNG, so that scan-time correction is baked into the
session original and cannot be undone in esyuscan. This is a lossless PNG encoding
of the processed scan, not a promise of untouched scanner response bytes.
Already saved original PNGs are never changed by edits or exports.

Select a page and choose **Deskew** to detect its angle with NAPS2's upstream
algorithm. This is an automatically saved, undoable edit. For sideways text,
rotate the page upright first. Detection uses the full original in the current
quarter-turn orientation, and replaces the previous deskew angle rather than
stacking corrections. It is intended for text and horizontal lines, within
±20 degrees; inspect the result and undo if it misidentifies the angle.

Choose **Crop** and drag on the full-page image, or enter **Left**, **Top**,
**Width**, and **Height** in displayed image pixels. The adjacent result preview
shows exactly what will be kept. **Apply crop** saves one undoable edit;
**Cancel**, Escape, or closing the dialog leaves the session unchanged.
**Full page** restores the whole canvas; apply it to remove an existing crop.
Reopening the editor always shows the full canvas, so a crop can be expanded.

Choose **Adjust** for brightness/contrast, sharpening, automatic document/noise
correction, arbitrary-angle rotation, and horizontal/vertical flips. The editor
previews the final pixels; **Apply** saves one undoable action and **Cancel**
discards it. **Reset adjustments** clears those controls; **Reset all page edits**
also clears crop, deskew and quarter-turn rotation. Apply either reset to save it.
Document correction combines tone correction and noise reduction; it can soften
fine text. Photo correction adjusts tones only. There is no standalone despeckle
operation in this SDK. See [image adjustment controls and guarantees](image-adjustments.md).

The rendering order is **deskew → crop → clockwise 90° page rotation → arbitrary
angle → horizontal/vertical flips → automatic correction → brightness → contrast
→ sharpening**.
Deskew rotates about the original canvas center using the SDK's white-filled,
fixed-size canvas (edges can be clipped). Crop stores integer pixel edges in
that deskewed, unrotated canvas, with top-left origin and exclusive right/bottom
edges. The crop editor selects before new adjustments and converts its displayed,
quarter-turned selection back to those coordinates; its result preview includes
all edits. Rotating an already cropped page preserves its selected content.
Changing deskew retains the crop rectangle in canvas coordinates, so inspect
its edges afterward. Thumbnails, main preview, crop result, image/TIFF exports, both
PDF exports, and CLI exports all use `PageRenderer` before display scaling.

Sessions with crop, deskew or PDF sources use manifest version 2. New image
adjustments use version 3; versions 1 and 2 still open with no migration step.
Sessions without these fields continue to save as version 1. Older esyuscan builds
reject unsupported versions rather than silently exporting pages without edits.
Undo/redo history still lasts only until the session is closed.

SDK integration: the installed package identifies upstream commit
[`f914a4ec`](https://github.com/cyanfish/naps2/tree/f914a4ec2af090aca4fa55eb3242ec91641482c3).
Public SDK transforms perform the image edits; flipping uses its GTK backend.
The deskew detector is internal, so `SdkDeskew` binds the upstream `GetDeskewTransform`
method through a small isolated reflection adapter; the package is unmodified.
Generated-content tests guard that binding and correction behavior. Recheck it
when upgrading NAPS2. The scan option uses the public SDK API directly.

## Print and email

Use **Print…** (Ctrl+P) to choose a CUPS printer, copies, sides, paper size,
and page fitting. esyuscan prepares the current document with its saved edits and
submits a held job, then releases it after receiving its job ID. The status
confirms queue submission. **Cancel last print job** requests cancellation from
CUPS; pages already printed cannot be recalled. **Settings → System printer
settings ↗** opens the system printer manager. CUPS commands and a configured
printer are required.

**Email PDF…** prepares the same combined document, honoring the searchable PDF
option, and asks `xdg-email` to open your mail composer with the PDF attached.
Check the attachment in your client before sending. esyuscan never sends mail.
Attachments have a session-based filename and private storage under
`$XDG_DATA_HOME/esyuscan/outgoing` (normally `~/.local/share/esyuscan/outgoing`). They
survive esyuscan closing. Files older than seven days are removed on the next print
or email preparation; save a permanent export for long-lived drafts.

See [printing and email notes](printing-email.md) for setup and lifecycle details.

## Omarchy integration

esyuscan reads `~/.local/state/omarchy/current/theme/colors.toml` every second,
including custom palette overrides. Colors update without restarting the app,
even when Omarchy replaces the theme directory. Fonts follow `fc-match monospace`;
corners follow Hyprland's effective rounding, refreshed every five seconds.
These are read-only, app-local integrations. Other apps and desktop configuration
are not changed. A built-in palette is used if Omarchy is unavailable.

esyuscan uses Avalonia 12.1.4's native Wayland backend, with the app ID `esyuscan`
matching its launcher. The GUI requires a Wayland compositor; it does not fall
back to XWayland. Display scaling comes from the compositor, without translating
GTK or Qt scale variables. Avalonia currently labels this backend experimental.
The command-line tools do not require a display.

File and folder dialogs use the desktop's configured XDG portal backend, including
Nautilus when selected. esyuscan exports a Wayland parent handle so the chooser can
attach to its window without X11 interoperability. The picker follows its own
desktop theme rather than esyuscan's Avalonia theme.
**System printer settings ↗** launches `system-config-printer`, a separate GTK app
that also follows the desktop theme. These windows are supplied by the desktop;
esyuscan's live Omarchy palette applies to its own windows.

PDFium is bundled for both Linux ARM64 and x64 for PDF import, previews, and
lossless AirScan responses.

## Import and extract PDF pages

Choose **Import PDFs / images** (Ctrl+I) to append PDFs, PNGs, JPEGs, TIFFs, or
BMPs to the current session. Files follow the order supplied by the picker;
pages within a PDF or TIFF keep their source order. You can mix these with scans,
move pages, remove pages, and rotate them in 90-degree steps. Import runs in the
background; Escape or **Cancel** stops it while keeping every completed page.
If a later file fails, earlier completed pages remain saved. Errors identify the
file. Password-protected PDFs require an unlocked copy; esyuscan does not prompt
for or store passwords. Damaged or unsupported PDFs produce an error.

esyuscan copies each PDF into the session before importing pages. Reopening and
exporting never depend on the external file, even after it is moved or deleted.
The preview caption identifies PDF pages. PNG previews serve the page tray and
large preview; they do not replace the PDF source for unedited PDF exports.

- **One document** combines the current page order, including mixed scans and PDFs.
- **Separate PDFs** extracts each remaining page into a numbered one-page PDF.
- **Separate images** renders each remaining page as PNG, JPEG or TIFF; images
  cannot retain PDF text or vectors. **One document** also offers multipage TIFF. The PDF source stays in the session for later PDF exports.

For unedited text/vector pages, NAPS2 passes PDF content through to both PDF export modes.
Quarter-turn rotations preserve text and vectors using PDF page rotation metadata.
Crop, deskew and image adjustments deliberately rasterize that edited page for export; the caption
identifies it, and undoing those edits restores PDF passthrough. Source PDFs stay
intact. Searchable export preserves existing PDF text and recognizes pages that
need it, after all edits. Rotated PDF coordinates are normalized before OCR so
recognized text stays within the visible page.
NAPS2 can extract and re-encode an image-only PDF page as an image; byte-for-byte
PDF output, document metadata, bookmarks, signatures, attachments, and interactive
form behavior are not guaranteed. Original PDF bytes are always kept in the
session's `originals/` folder, including after page removal or cancelled partial
imports. Undo/redo works for PDF pages as it does for scans.

Existing image-only sessions remain compatible. Sessions containing PDF sources
use format version 2 (version 3 with image adjustments); older esyuscan versions refuse unsupported sessions rather than
silently exporting their previews. See [PDF storage and integration notes](pdf-import.md)
for the schema, recovery, and raster-edit behavior.

## Current scope

- Linux ARM64 and x64, .NET 10, AirScan/eSCL network scanners and SANE USB/network backends.
- HP OfficeJet Pro 8020 discovery and capability queries verified locally.
- Import: PDF, PNG, JPEG, TIFF, and BMP. Export: PNG, JPEG, TIFF (including
  multipage), and PDF with compression, PDF/A, metadata and security controls.
- Translations are not implemented. SANE support depends on the installed backend and scanner model.
- Direct CUPS printing and PDF mail composition.
  Physical printer/scanner and desktop mail interoperability still need validation.
- No physical scan or print job is part of the automated tests.

## Develop

Requires the .NET 10 SDK, GTK 3, libtiff, HarfBuzz, fontconfig, Wayland,
libxkbcommon, and EGL/OpenGL (libglvnd). Running the GUI requires a Wayland compositor.
On Omarchy, install the SDK with `omarchy pkg add dotnet-sdk-bin`.

```sh
dotnet restore Scan.slnx --locked-mode
make build
make run
make check       # Formatting, build, focused tests
make check-all   # Full tests and dependency audit
make format
```

Tests use generated documents, temporary directories, and a headless renderer.
To regenerate the screenshot with fictional content:

```sh
ESYUSCAN_SCREENSHOT_DIR="$PWD/docs" make test ARGS='--filter FullyQualifiedName~RenderDocumentWorkspace'
```

`NativeText.cs` makes Avalonia and GTK use the same system HarfBuzz on Linux.
This avoids a native allocator conflict observed when GTK and Avalonia loaded
different HarfBuzz implementations into the same process.

## Install locally

```sh
make package    # Build an Arch package without installing it
make install    # Build and install; asks for sudo authorization
```

The package supplies `esyuscan`, an icon, and an app-launcher entry. It does not
replace NAPS2 or modify the Omarchy menu. No public repository or release is
created by these commands.

## Commands

```sh
esyuscan open [session-folder]
esyuscan check --json
esyuscan devices --json
esyuscan ocr --json
esyuscan export SESSION --format images --output NEW_FOLDER --json
esyuscan export SESSION --format pdfs --output NEW_FOLDER --json
esyuscan export SESSION --format pdf --output NEW_FILE.pdf --json
esyuscan export SESSION --format images --image-format jpeg --quality 85 --output NEW_FOLDER --json
esyuscan export SESSION --format tiff --tiff-compression lzw --output NEW_STACK.tiff --json
esyuscan export SESSION --format pdf --pdfa 2b --rasterize-pdf --title Receipts --output NEW_ARCHIVE.pdf --json
esyuscan export SESSION --format documents --output NEW_FOLDER --json
esyuscan export SESSION --format pdf --output SEARCHABLE.pdf --searchable --ocr-language eng --json
esyuscan export SESSION --format pdfs --output SEARCHABLE_FOLDER --searchable --ocr-language nld --json
esyuscan skill install
```

Close a session in the GUI before exporting it from another process. CLI errors
exit with status 1. `--searchable` defaults to English (`eng`); `--ocr-language` requires
`--searchable`. Ctrl+C cancels CLI export; progress goes to stderr, JSON results to stdout.
`esyuscan ocr` only probes local OCR components and never discovers scanners.
Image format defaults to PNG; PDF raster compression defaults to lossless. Explicit
PDF/OCR flags require PDF output. PDF/A and encryption are mutually exclusive.
PDF/A with preserved PDF sources requires explicit rasterization, losing text/vectors
in that export; optional OCR adds new text. Passwords are runtime-only and masked;
CLI takes environment variable names via `--pdf-owner-password-env` and optionally
`--pdf-open-password-env`. Encryption uses the SDK's legacy 128-bit RC4 handler.
See [formats, validation and SDK limits](export-options.md).
`skill install` creates an agent skill and refuses to overwrite an existing one.
Scanner diagnostics discover devices but never start a scan.

## Keyboard

Primary buttons show their shortcuts below their labels. Hover any shortcut-enabled
control for its keys and details; **F1** or **?** opens the complete, grouped reference.
Tab / Shift+Tab moves between controls. Text fields retain normal typing, selection,
clipboard, and undo keys. Space activates a focused button or checkbox; in the page
tray it selects pages rather than scanning. **F6** scans from any control when ready.

| Shortcut | Action |
|---|---|
| Space / F6 | Scan (Space only outside controls and the page tray) |
| Ctrl+N / Ctrl+O | New / open session |
| Ctrl+I | Import PDFs or images |
| Ctrl+S / Ctrl+Shift+S / Ctrl+Shift+I | Export one document / separate PDFs / separate images |
| Ctrl+P / Ctrl+M | Print / prepare email PDF |
| Ctrl+Shift+P | Cancel last print job |
| Ctrl+D / Ctrl+B | Guided double-sided scan / batch setup and review |
| Alt+F / Alt+B / Ctrl+Enter | Duplex fronts / backs / apply confirmed order |
| F5 / Shift+F5 | Refresh scanners / OCR languages |
| Alt+1 / Alt+2 / Alt+3 | Choose scanner / preset / paper source |
| Alt+4 / Alt+5 / Alt+6 | Choose color / resolution / page size |
| Alt+Enter / Ctrl+Alt+P | Apply preset / open preset management |
| Alt+S / Alt+O / Alt+D | Toggle selected pages only / OCR / automatic scan deskew |
| Alt+L | Choose OCR language |
| Ctrl+Alt+L | Browse Tesseract OCR models, availability, and setup |
| Ctrl+A | Select all pages |
| Shift+arrows / Home / End | Extend selection in the page tray |
| Ctrl+arrows / Ctrl+Space | Move tray focus / toggle focused page |
| Ctrl+Plus / Ctrl+Minus / Ctrl+0 | Zoom in / out / fit page |
| R / C / A / K | Rotate selection / crop / adjust / deskew focused page |
| Alt+Left / Alt+Right | Move selected block |
| Delete | Remove selected pages, keeping originals |
| Ctrl+Z / Ctrl+Shift+Z | Undo / redo |
| Esc | Close an open picker or Settings; otherwise cancel the active operation |
| Ctrl+, | Open / close Settings |
| F1 / ? | Show shortcuts |
| Ctrl+Q / Q | Quit; completed pages are already saved |

Dialogs show **Enter** / **Esc** on their confirm/cancel buttons. In the batch
window, **Ctrl+Enter** explicitly starts or resumes scans, **Ctrl+S** exports
reviewed documents, and **Esc** cancels work or closes an idle window. Shortcuts
respect the same enabled state and review requirements as clicking their controls.

## License

MIT for esyuscan's original code. See [THIRD-PARTY.md](../THIRD-PARTY.md) for dependencies.
