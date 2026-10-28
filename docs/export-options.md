# Export formats and PDF options

The three workspace actions remain distinct. Each opens a scrollable options dialog
before the destination picker. Settings in this dialog apply to that export only;
closing it, cancelling a picker, or failing export does not edit the session.

| Workspace action | Output | CLI |
|---|---|---|
| Separate images | New folder of numbered PNG, JPEG or single-page TIFF files | `--format images --image-format png\|jpeg\|tiff` |
| Separate PDFs | New folder of numbered one-page PDFs | `--format pdfs` |
| One document | One combined PDF or one multipage TIFF, in session order | `--format pdf` or `--format tiff` |

PNG and standard PDF with lossless raster compression remain the defaults. Existing
CLI commands continue to work. There is no new session schema or preferences
migration. JPEG files use `.jpg`, TIFF files `.tiff`, and numbered filenames use
`page-001`, `page-002`, etc. The CLI encodes the explicitly selected format even if
the supplied destination has a different extension; use the matching extension.

## Image formats and compression

- **PNG** preserves rendered pixels losslessly. There is no PNG quality control.
- **JPEG** is lossy; quality is an integer **1–100**, default **90**. Higher values
  usually increase size/detail. It does not preserve transparency or PDF text/vectors.
- **TIFF** supports separate files and multipage output through the SDK GTK
  `ITiffWriter`. Auto uses CCITT Group 4 for genuinely 1-bit pages and LZW for
  color/grayscale. LZW is lossless, and None is uncompressed. Forced Group 4 is
  accepted only when every output page is already black-and-white (1-bit).
  The SDK would otherwise threshold color/grayscale silently; esyuscan refuses it
  without publishing anything. Use Auto or LZW for mixed pages. JPEG-in-TIFF and
  a TIFF quality slider are not supported by this SDK. Classic TIFF is used;
  BigTIFF and outputs approaching the classic TIFF 4 GiB limit are not supported.

Images and TIFF always use `PageRenderer`, including deskew, crop and rotation.
PDF source pages become pixels at their stored preview resolution (normally 300
DPI); native text/vectors are retained only in the immutable session PDF source.
No OCR runs for PNG/JPEG/TIFF. In the combined-document dialog, selecting TIFF
shows TIFF controls and explains its pixel-only output. PDF-specific controls
apply when PDF is selected. Explicit PDF/OCR flags with image/TIFF CLI output fail.

For PDF raster pages choose **Lossless** or **JPEG**. Lossless retains the pixels
from the render pipeline. JPEG encodes those pages once using the selected quality
through the upstream image encoder, then the SDK embeds those JPEG bytes. Merely
setting `ImageMetadata.Quality` would leave PNG-origin pages lossless in SDK 1.6.0,
so esyuscan deliberately encodes JPEG first. This also supplies that image to OCR.
Unedited PDF passthrough content keeps its existing compression and image quality;
these controls do not recompress native PDF objects. Select **Rasterize PDF sources**
only when that transformation is wanted. **Allow lossless JBIG2** lets the SDK use
JBIG2 for 1-bit PDF images when smaller than its alternatives; disabling it does
not mean uncompressed PDF. It does not change TIFF compression.

## PDF/A and metadata

Standard PDF and the installed SDK's PDF/A-1b, 2b, 3b and 3u options are available.
The exporter writes the matching XMP identification, sRGB output intent and PDF
version. PDF/A cannot use encryption. esyuscan requires explicit **Rasterize PDF
sources** / `--rasterize-pdf` when PDF/A includes any otherwise-preserved source
PDF page: the SDK does not validate or convert arbitrary source fonts, color
spaces or other native content to archival conformance. This loses existing
searchable text and vectors in the export; optional OCR adds a new text layer.
Crop/deskew pages already use the raster pipeline. Originals remain unchanged.

The pinned SDK does not reliably flatten transparency for PDF/A-1b; esyuscan rejects
transparent raster pages in that mode. Choose PDF/A-2b/3b/3u or flatten the input
before import. The SDK also interpolates metadata directly into XMP. esyuscan rejects
XML punctuation (`< > & " '`) and control characters in PDF/A metadata rather than
publish malformed XMP. Standard PDF metadata supports these characters and Unicode.

Title, author, subject, keywords and creator apply to the combined PDF or to every
separate PDF. Creator defaults to esyuscan; an empty creator uses the SDK's NAPS2
default. This is output metadata, not copied source-document metadata.

These options invoke the upstream PDF/A exporter; they are **not an independent
conformance certification**. Tests inspect XMP, profiles and file versions, and
exercise OCR and rasterized PDF sources. A veraPDF compliance run is still needed
for archival acceptance; no independent validator is installed in this workspace.

## Passwords and permissions

Encryption uses NAPS2's `PdfExportParams.Encryption`: **128-bit RC4**, with no AES
option in SDK 1.6.0. It is a legacy compatibility feature. esyuscan accepts 1–32
printable ASCII password characters so PdfSharp cannot silently truncate or
misencode them. The **owner password is required**; the optional open password
must differ from it. With an empty open password, anyone can open the encrypted
file, subject to the reader's permission handling. An owner password permits
owner access. Permissions control printing, full-quality printing, copying,
accessibility extraction, modification, annotations, form filling and page assembly.
Denying printing also denies full-quality printing. Permissions are advisory and
some readers ignore them; they cannot prevent screenshots or all content copying.

Fields are masked. Passwords are not saved in sessions, scan presets, preferences,
JSON results or diagnostics. They are retained in managed memory only while the
export is configured/running; fields clear when the dialog closes, and the dialog
releases the accepted result when handed to export. Managed strings and upstream
SDK password objects cannot be securely zeroed; esyuscan does not claim that guarantee.
There is no remembered password, reveal control or direct password CLI argument.

For CLI use, populate environment variables privately (for example with a shell's
masked input facility), then pass **variable names**, not passwords:

```sh
esyuscan export SESSION --format images --image-format jpeg --quality 85 --output NEW_FOLDER --json
esyuscan export SESSION --format images --image-format tiff --tiff-compression auto --output NEW_FOLDER --json
esyuscan export SESSION --format tiff --tiff-compression lzw --output NEW_STACK.tiff --json
esyuscan export SESSION --format pdf --pdf-compression jpeg --quality 90 --title 'Receipts' --output NEW.pdf --json
esyuscan export SESSION --format pdfs --pdfa 2b --rasterize-pdf --searchable --ocr-language eng --output NEW_FOLDER --json
esyuscan export SESSION --format pdf --pdf-owner-password-env ESYUSCAN_OWNER_SECRET --pdf-open-password-env ESYUSCAN_OPEN_SECRET --deny print,copy,modify --output NEW_PRIVATE.pdf --json
```

Unset these variables when finished; environment values can be inspected by other
processes with sufficient access. `--deny` accepts comma-separated `print`,
`full-quality-print`, `copy`, `accessibility`, `modify`, `annotate`, `forms`,
`assemble`. Unset/empty named variables fail. `--deny` requires encryption.
`--pdfa none` selects standard PDF; `--no-jbig2` disables PDF JBIG2. Use
`esyuscan --help` for all flags. Unknown, duplicate, missing or incompatible options
fail before publication; errors do not echo supplied values.

## Safety and integration

All formats keep the existing session lock, immutable originals, undo/redo and
atomic session-save behavior. Export never updates the manifest or presets.
A private staging directory beside the destination holds every output until the
entire operation and requested OCR succeed. Publication uses a no-overwrite file
or directory move; a destination created during export wins. Cancellation before
publication removes all staged output. Ctrl+C cancels CLI export. TIFF checks
cancellation between frames; a current native frame encode can take time to return.
Multipage TIFF currently renders all frames into memory for the SDK writer, so
large color stacks can need considerable RAM. esyuscan never starts real scanner or
printer jobs to test export.

PDF rotations are now prepared **before** final export, through the existing
bundled PdfSharp rotation adapter. No PDF is rewritten after the final exporter
adds encryption, metadata or PDF/A fields. Searchable export keeps its coordinate
normalization and `RequiredOcrEngine` count/drain guarantees. Final encrypted PDFs
are reopened through upstream PdfSharp to verify both passwords and all permission
flags before publication; failure aborts. SDK errors from encrypted exports are
replaced with a credential-free explanation, without retaining inner exceptions.

No NuGet dependencies changed. Packaging explicitly lists system `libtiff`, used
by the already-installed GTK SDK for TIFF reads/writes, and includes this guide.
The source inspected is the pinned SDK revision
[`f914a4ec`](https://github.com/cyanfish/naps2/tree/f914a4ec2af090aca4fa55eb3242ec91641482c3):
[`PdfExportParams`](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/Pdf/PdfExportParams.cs),
[`PdfExporter`](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/Pdf/PdfExporter.cs),
[`PdfAHelper`](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/Pdf/PdfAHelper.cs),
[`LibTiffIo`](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Images.Gtk/LibTiffIo.cs).

Integration overlap is limited to `Scanner.Export`, the MainWindow export handlers
and button tooltips, CLI dispatch/help, README and packaging/docs. New option models,
parser, dialog and most tests are separate files. `ExportFormat.CombinedTiff` is an
additional enum value; exhaustive consumers must handle it as a single file.
`Scanner.Export` adds an optional trailing `ExportOptions` argument, preserving
existing callers. No translation, device discovery, scan preference, import schema
or other feature-area changes are included. Tests use generated credentials and
files; ARM64 local validation does not replace a future x64 runtime smoke test.
