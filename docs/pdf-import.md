# PDF source storage and integration

PDF import uses the unmodified NAPS2 SDK 1.6.0 `FileImporter` / `PdfImporter`.
The SDK owns PDF parsing, page selection, rendering, and content passthrough.
esyuscan uses the SDK's transitive `NAPS2.PdfSharp` package (`PdfSharpCore` namespace)
to prepare `/Rotate` metadata and normalize OCR coordinates before final PDF export. There is no custom PDF
parser, native wrapper, or modified SDK.

## Session representation

Image-only manifests still use version 1, and existing version 1 manifests without
any new fields deserialize unchanged. A manifest containing PDF references uses
version 2 (version 3 when image adjustments are present) so older esyuscan builds reject unsupported edits explicitly. `SessionPage` has one new,
optional `PdfSource` property; existing `FileName` and `Rotation` retain their
meanings:

```json
{
  "Version": 2,
  "Name": "Mixed documents",
  "Pages": [
    { "FileName": "scan.png", "Rotation": 0, "PdfSource": null },
    {
      "FileName": "preview.png",
      "Rotation": 90,
      "PdfSource": { "FileName": "source.pdf", "PageIndex": 2 }
    }
  ]
}
```

All filenames are session-relative basenames under `originals/`. PDF page indices
are zero-based. An immutable PNG is saved for each PDF page for the existing UI
preview and image-export path. The full PDF is copied once per imported file,
flushed to disk before importing, and shared by that file's page references.
Page PNGs are flushed before the atomic manifest save and page-added callback.
No absolute external file paths or SDK temporary storage paths enter the manifest.

The existing `Session` save, locking, undo, redo, move, remove, and rotate
operations apply to the whole page record. Sources and PNGs are never overwritten
by edits or removed when a page is removed. Import failures/cancellation before
any page is committed delete that import's unused PDF copy; once any page is
committed the whole PDF copy stays for recovery. Prior files/pages are retained.
As with scanned pages, a process crash between writing an original and saving its
manifest may leave an unreferenced original; do not garbage-collect these blindly.

Missing PDF sources fail at reopen; unavailable page indices fail at export.
Neither condition silently falls back to the PNG. The source PDF may be opened
manually from `originals/` to recover removed or not-yet-imported pages; importing
it into a new session also works. Keep the complete session folder when moving
or backing it up. esyuscan does not modify source files during reopen or export.

## Export and rotation

PDF export reimports each selected source page with `ImportParams.Slice`, in the
current session order, and passes it to NAPS2 `PdfExporter` without image
transforms. NAPS2 preserves text/vector pages through its PDFium passthrough
path. Its single-image extraction optimization may re-encode image-only PDFs.
This is content preservation, not preservation of the original document's
container bytes, global metadata, signatures, or interactive features.

NAPS2 1.6.0 rasterizes PDF pages when `RotationTransform` is applied. For PDF-source
pages esyuscan instead updates PDF `/Rotate` on prepared pages before final export through
upstream PdfSharp, adding to any existing page rotation. Text/path objects remain
PDF objects. Failure to process an unusual PDF aborts export and leaves the
session and destination untouched; there is no automatic raster fallback.
Scanned/image pages continue through the existing rotation transform. Image/TIFF export
uses the stored rendered page and applies rotation, explicitly losing searchable
text and vectors in that export only. No OCR is run during import. Export optionally uses the searchable-PDF settings.

All three export modes retain existing no-overwrite and staging semantics.
Cancellation disposes SDK images and removes unpublished staging output. Export
never changes either source PDFs or PNG originals.

## Raster edits and searchable export

`SessionPage` also stores crop, deskew and optional image adjustments. `PreservePdf`
is true only for a PDF source with no crop, no deskew, and no non-neutral adjustment. Edited pages use `PageRenderer` for both preview
and all export modes; they retain `PdfSource` as recovery metadata. Undoing the
raster edits restores passthrough. The caption identifies raster-edited PDF pages.
Quarter-turn rotation alone preserves PDF objects. See [the adjustment schema and
rendering order](image-adjustments.md).

Optional OCR recognizes raster-edited pages after rendering. Unedited PDF pages
retain existing text and vectors, and SDK PDFium checks which pages need OCR.
`SdkPdfText` uses the pinned SDK's internal reader in an isolated adapter to match
the expected recognition count; failures prevent publication. Before searchable
export, PdfSharp normalizes PDF rotation into content coordinates, retaining PDF
objects. This avoids an SDK 1.6.0 issue where OCR text on `/Rotate` pages falls
outside the visible page. Media/crop boxes and PdfSharp's imported orientation
are handled together. Ordinary PDF exports retain rotation metadata. Original
source files remain unchanged throughout.

## Upstream behavior checked

The package metadata pins SDK 1.6.0 to commit
`f914a4ec2af090aca4fa55eb3242ec91641482c3`:

- [FileImporter](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/ImportExport/FileImporter.cs)
  routes PDFs and images to their upstream importers.
- [PdfImporter](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/Pdf/PdfImporter.cs)
  selects pages, extracts image-only content, and stores other pages as PDFs.
- [PdfExporter](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Sdk/Pdf/PdfExporter.cs)
  uses PDFium passthrough only when PDF storage has an empty transform state.

Tests generate text, vector paths, image-only pages, and encrypted input locally.
They check text/path PDF operators, page counts/order, existing and added rotations,
source hashes, mixed exports, reopen after deleting inputs, undo/redo, cancellation,
failed imports, no-overwrite guarantees, and headless preview/edit controls.
Physical scanner and printer validation remains a separate hardware check.

PDF export now offers metadata, compression, PDF/A and encryption. Final security
is added after preparing rotations, and verified before publication. PDF/A with
otherwise-preserved PDF sources requires explicit rasterization; ordinary PDF
keeps text/vector passthrough. See [export controls and SDK limits](export-options.md).
