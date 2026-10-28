# Searchable PDF export

Enable **Searchable PDF (OCR)** in the export bar, choose an installed language, then
use **Separate PDFs** or **One document**. OCR is off by default. This export
preference is kept for the current window; session files and originals are unchanged.
**Separate images** offers PNG/JPEG/TIFF and contains pixels only. **One document**
also offers multipage TIFF. The per-export dialog can adjust OCR for PDF output;
TIFF shows its own controls. See [formats and PDF options](export-options.md). Refresh OCR
languages after installing components. Hover over the OCR status for full setup errors.

The CLI uses the same export implementation:

```sh
esyuscan ocr --json
esyuscan export SESSION --format pdf --output NEW.pdf --searchable --ocr-language eng --json
esyuscan export SESSION --format pdfs --output NEW_FOLDER --searchable --ocr-language fra --json
```

`--searchable` defaults to `eng`. One installed language is supported per export;
`osd` (orientation detection) is not a text-recognition language. `--ocr-language`
requires `--searchable`; requesting OCR for `--format images` or `--format tiff` is an error.
The PDF still displays the page image, with an invisible recognized text layer.
OCR is approximate: verify important names/numbers against the image. A genuinely
blank/unrecognized page may contain no text even when the engine ran successfully.

## Available languages

Language pickers show readable names and exact Tesseract codes, sorted by name.
**Settings → OCR languages / models…** (Ctrl+Alt+L), also available in
the PDF export dialog, lists all 124 upstream Tesseract language models and variants with **Installed**
or **Not detected** status. Search by language name or code. **Use** selects one
installed language; unavailable models cannot be selected. Refreshing languages
keeps the exact selection if still installed, otherwise defaults to English or
the first available language. Custom installed models remain available under
their codes; the catalogue is not an allowlist.

The list covers every root language model in official
[tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd),
including historical and vertical variants. See the upstream
[language table](https://tesseract-ocr.github.io/tessdoc/Data-Files-in-different-versions.html)
for codes. `osd` (orientation) and `equ` (equation detection) are not text languages
and are not offered in the catalogue. Script bundles in subdirectories are not
offered. esyuscan does not invent models for unsupported languages, silently
substitute a related language, or combine all models into one OCR pass.

The legacy `frk` model is labelled German Fraktur (legacy); new provisioning
should use `deu_latf`. Some historical/regional models may not have distribution
packages; use official `.traineddata` files when necessary. **Installed** means
reported by Tesseract, not independently verified recognition quality. No models
are bundled or downloaded.

## Deliberate Linux provisioning

esyuscan never installs packages, downloads engines/models, or requires network access
for OCR. The Arch package declares optional engine and English-data dependencies,
and installs this guide at `/usr/share/doc/esyuscan/searchable-pdf.md`. A normal app
install and all ordinary exports work without OCR components.

Choose one of these user/admin-managed workflows:

1. **Distribution packages (recommended):** install the native `tesseract` package
   and only the language packages needed, such as `tesseract-data-eng`,
   `tesseract-data-fra`, or `tesseract-data-nld` on Arch/Omarchy. Review and perform
   this separately through your normal package manager; esyuscan does not invoke it.
   Arch Linux supplies x86_64 binaries; Arch Linux ARM supplies aarch64 binaries.
   Do not copy an x86_64 executable to ARM64. The distribution also supplies native
   Leptonica and engine runtime dependencies. Check `tesseract --version` and
   `tesseract --list-langs`, then restart esyuscan or use **Refresh OCR**.
2. **User-managed installation:** supply a trusted native Tesseract 4.x or 5.x
   executable (and its shared-library dependencies), plus compatible `.traineddata`
   files. Select only required models from the official
   [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) or
   [tessdata_best](https://github.com/tesseract-ocr/tessdata_best) repository after
   reviewing their sizes/licenses. Record the source revision and SHA-256 when
   provisioning; keep the upstream LICENSE with the models. No download-all step
   is needed. Models are architecture independent; the executable is not.

For a user-managed installation, launch esyuscan with absolute paths, for example:

```sh
ESYUSCAN_TESSERACT="$HOME/.local/opt/tesseract/bin/tesseract" \
ESYUSCAN_TESSDATA="$HOME/.local/share/esyuscan/tessdata" esyuscan ocr --json
```

Use the same environment when launching the GUI/export command. `ESYUSCAN_TESSDATA`
points directly to the folder containing `eng.traineddata` etc., not its parent.
It is passed to the SDK as the data directory and to probes as `TESSDATA_PREFIX`.
With no overrides, esyuscan uses `tesseract` on PATH and its default data search path
(including any existing `TESSDATA_PREFIX`). Overrides do not alter system settings.
Simple language identifiers such as `eng`, `chi_sim`, and `srp_latn` are supported;
combined codes (`eng+fra`) and script subdirectories are not currently offered.

The dependency probe runs the selected executable's `--version` and `--list-langs`
with ten-second timeouts. A wrong architecture, missing executable/shared library,
unsupported version, or absent language produces an actionable error before OCR.
Corrupt/incompatible data or an engine failure during recognition aborts the whole
export. There is no fallback that reports an image-only PDF as successful OCR.
See [THIRD-PARTY.md](../THIRD-PARTY.md) before redistributing binaries or models.

## Export and cancellation guarantees

For raster pages, OCR receives the exact lossless PNG from `PageRenderer`, after
deskew, crop, quarter-turns, arbitrary rotation, flips and image adjustments.
Unedited PDF pages retain the existing SDK PDF object/OCR path.
Progress covers preparation, recognition/PDF writing, and publication. Escape/the
GUI Cancel button or CLI Ctrl+C cancels the export, including an active Tesseract
process. Recognition has a five-minute timeout per page. The SDK may perform pages
in parallel, so progress advances as whole pages complete; a complex page can take
time before the next update.

All output is staged in a fresh directory beside the destination. Only a complete,
uncancelled export is moved into place, with no overwrite if another file/folder
appears while OCR runs. Errors and cancellation remove staging. Saved session data,
original images, undo/redo, and session locking are unaffected.

## SDK verification and integration points

This feature uses **unmodified NAPS2 SDK 1.6.0**, not NAPS2 desktop OCR dialogs.
The inspected package source revision is
[`f914a4ec2af090aca4fa55eb3242ec91641482c3`](https://github.com/cyanfish/naps2/tree/f914a4ec2af090aca4fa55eb3242ec91641482c3):
[`OcrSample`](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk.Samples/OcrSample.cs),
[`TesseractOcrEngine`](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/Ocr/TesseractOcrEngine.cs),
[`PdfExporter`](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/Pdf/PdfExporter.cs).

`TesseractOcrEngine.Custom(executable, dataDirectory)` implements `IOcrEngine` via
an external native process and returns hOCR-derived results. esyuscan passes
`OcrParams(language, TimeoutInSeconds: 300)` to `PdfExporter.Export`. No P/Invoke
Tesseract library, NAPS2 GUI installation, or optional Tesseract NuGet binary package
is required. System-native executables avoid assumptions about bundled binary RIDs;
the same managed API works with ARM64 and x64 Tesseract. Local execution is verified
on ARM64; an x64 runtime smoke test remains necessary before an x64 release.

The SDK permits null OCR results and can still return a successful PDF. esyuscan's
`RequiredOcrEngine` records every completion/failure and checks the expected page
count before publication. It also drains active engine calls on cancellation before
releasing the context's images/temp directory, because the SDK request queue can
return before the native process has stopped. Blank-page results remain valid.

SDK 1.6.0 normally JPEG-encodes raster OCR input inside `PdfExporter.InitOcrStep`,
even for a lossless PNG. `SdkLosslessOcr` isolates a reflection binding to the
upstream internal `OcrRequestQueue.Enqueue` API: it queues the canonical PNG
before export, using the same processed image, required engine and OCR parameters.
The exporter reuses the upstream cached recognition result. Exact recognition
counts are still checked after staging and before publication, including separate
PDFs with mixed preserved and raster pages. Cancellation still drains the required
engine before releasing images/temp files. The adapter is guarded by tests that
capture real Tesseract input and compare every pixel with `PageRenderer`; SDK
upgrades must revalidate this internal binding.

Integration with the other independent feature branches:

- `Scanner.Export` keeps its existing arguments and adds optional `PdfOcrOptions`
  and progress callback parameters. Its final `ProcessedImage` list is the OCR
  boundary: raster pages use `PageRenderer` and lossless OCR preparation **before** PDF export.
- No session schema changes, persisted OCR text, preset storage, or import logic.
  A presets integration may choose to store the optional export language later.
- PDF import is integrated: unedited PDF objects and existing searchable text
  survive export. `SdkPdfText` matches upstream PDFium's existing-text bypass so
  skipped pages do not count as missing recognition. The internal adapter is
  guarded by mixed-document tests against the pinned SDK.
- PdfSharp normalizes rotated PDF coordinates before OCR without rasterizing
  their objects. This prevents SDK 1.6.0 from placing OCR text outside the visible
  page. Crop/deskew/adjustment pages pass through `PageRenderer` before recognition.
- UI additions are limited to export preferences/status; no physical scan/print
  operations are introduced. Hardware scanning/printing still needs validation.

## Meaningful verification

`make test ARGS='--filter FullyQualifiedName~Ocr'` runs language naming/selection,
catalogue availability and layout, detection, actual OCR/PDF
text extraction, missing/broken engine/data, cancellation during OCR and just before
publication, no-overwrite races, and ordinary export coverage. Real OCR tests need
Tesseract 4/5 plus English data; text extraction needs Poppler's `pdftotext`. These
are deliberately provisioned developer tools, not auto-installed test dependencies;
missing prerequisites produce an explicit test skip. Generated documents contain
known text, including a sideways immutable original rotated upright at export.
`make check-all` includes this suite along with existing session/UI/export tests.
