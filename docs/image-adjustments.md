# Image adjustments and rendering contract

Select a page and choose **Adjust**. The editor provides brightness and contrast
from −1000 to 1000, sharpening from 0 to 1000, clockwise rotation from −180° to
180° (0.1° steps), and independent horizontal/vertical flips. Zero is neutral.
Sliders and number fields stay synchronized. Choose automatic correction only
when useful, and inspect the preview for lost details.

- **Document · tones + noise reduction** uses NAPS2's document correction:
  white/black point adjustment followed by an edge-aware bilateral filter.
  These are one upstream operation, with no independently adjustable noise level.
- **Photo · tones only** adjusts white/black points without the document filter.
- **No automatic correction** is the default. Pure black/white content is unchanged
  by either correction mode, as specified by the SDK.

This SDK does not expose a standalone noise/despeckle transform. esyuscan does not
present document correction as speck removal, background flattening, perspective
correction, border removal, dewarping, or automatic cropping. Noise reduction can
soften small text; sharpening can emphasize noise. Both should be judged on the
final result. Scan-time automatic deskew remains a separate option.

The preview is rendered at original resolution and scaled only for display.
Edits are drafts until **Apply**, which atomically autosaves one undoable action.
Apply waits for the current draft's preview to succeed. **Cancel**, Escape, and
closing the editor discard the draft. **Reset adjustments** clears only these new
controls; **Reset all page edits** also clears crop, deskew and quarter-turn
rotation. Both resets are drafts and require Apply. Undo/redo includes resets.
Undo history lasts until closing the session; the applied adjustments persist.
The controls scroll and buttons wrap at narrow window widths.

Rapid control changes cancel superseded previews and serialize image processing.
Closing cancels outstanding work without writing the session. The SDK transforms
are synchronous: cancellation is checked between transforms, after encoding, and
before publication; it cannot interrupt the middle of a single SDK operation.
Large document correction/sharpening operations can take time to finish safely.

## Fixed transform order and coordinates

Every pixel consumer goes through `PageRenderer`, before any thumbnail/display
scaling. The full-resolution order is:

1. Load the immutable original PNG (including PDF preview/fallback PNGs).
2. Deskew about its center, on the SDK white-filled original-size canvas.
3. Crop using integer pixel edges in that deskewed, **unrotated** canvas.
4. Apply the existing clockwise quarter-turn `Rotation` (0/90/180/270).
5. Apply the adjustment's clockwise arbitrary `Angle`.
6. Flip horizontally, then vertically, in the resulting displayed axes.
7. Apply automatic document/photo correction.
8. Apply brightness, then contrast, then sharpening.

Pixel origin is top left; X increases right, Y increases down. Crop right/bottom
edges are exclusive. Crop remains before the new adjustments so saved crops keep
their meaning. The crop editor's selection image omits the new adjustments and
shows the full deskewed canvas in its quarter-turn orientation; its result preview
includes all adjustments. Changing deskew retains crop coordinates, so check edges.
Deskew detection ignores crop and new adjustments and detects afresh on the full
original with only the existing quarter-turn orientation, preserving its contract.

The GTK SDK rotation uses a centered, white-filled canvas. It swaps width/height
(and horizontal/vertical DPI) for angles strictly between 45°–135° or 225°–315°
after normalization to 0°–360°. Otherwise it keeps the current canvas size.
It does **not** expand the canvas to fit every rotated corner: arbitrary rotation
can clip edges, including on cropped pages. Quarter-turn and arbitrary rotation
are separate operations; the latter can resample content. Rotating a page using
**Rotate**/R keeps the crop and adjustment values and recomputes this fixed order;
a later horizontal flip still means the new displayed horizontal axis.

Thumbnails, main preview, adjustment preview, crop result, PNG export, both PDF
export modes, CLI export and raster-page OCR receive this same final image.
Raster PDF exports preserve pixels losslessly. PDF viewing/rasterization at other
resolutions may resample them. Original PNGs and PDF sources are never overwritten.
Any non-neutral adjustment, crop or deskew disables PDF passthrough. Clearing all
raster edits (including through undo or reset) restores passthrough; the existing
quarter-turn alone retains PDF text/vectors. Even edits that happen to produce
identical pixels remain raster edits until their stored settings are cleared.

## Backward-compatible storage

`SessionPage` appends optional `Adjustments`; missing/null means identity. Other
fields retain their meanings. An example new page record is:

```json
{
  "FileName": "original.png",
  "Rotation": 90,
  "DeskewAngle": -2.5,
  "Crop": { "X": 20, "Y": 30, "Width": 500, "Height": 700 },
  "PdfSource": { "FileName": "source.pdf", "PageIndex": 0 },
  "Adjustments": {
    "Brightness": 75,
    "Contrast": 100,
    "Sharpness": 350,
    "Angle": 12.3,
    "FlipHorizontal": true,
    "FlipVertical": false,
    "Correction": 1
  }
}
```

Tone/sharpness use SDK integer thousandths, angle uses finite degrees, and
`Correction` is 0=None, 1=Document, 2=Photo. Values outside the editor bounds and
undefined correction values are rejected on load and before saving. An all-zero
adjustment is canonicalized to null when applied, so it adds no redundant edit.
Editor commits cannot replace `FileName` or `PdfSource` recovery references.

Manifests containing adjustments use **version 3**. Versions 1 and 2 still load;
older builds reject version 3 instead of ignoring these edits. Saves without
adjustments retain version 2 when crop/deskew/PDF sources require it, otherwise
version 1. The existing atomic replacement, session lock and undo/redo machinery
are unchanged. No pixel caches or OCR results are persisted in the manifest.

## Upstream APIs and integration points

The existing unmodified NAPS2 1.6.0 packages identify source commit
[`f914a4ec2af090aca4fa55eb3242ec91641482c3`](https://github.com/cyanfish/naps2/tree/f914a4ec2af090aca4fa55eb3242ec91641482c3).
No dependencies, system packages, or translations were added.

- Public `BrightnessTransform`, `TrueContrastTransform`, `SharpenTransform`,
  `RotationTransform` and `CorrectionTransform` do the processing. See the pinned
  [transform implementation](https://github.com/cyanfish/naps2/blob/f914a4ec2af090aca4fa55eb3242ec91641482c3/NAPS2.Images/Transforms/AbstractImageTransformer.cs).
- This SDK has no flip transform. `SdkFlip` uses its public `GtkImage.Pixbuf`
  backend and upstream [GdkPixbuf flip](https://docs.gtk.org/gdk-pixbuf/method.Pixbuf.flip.html),
  preserving resolution, logical pixel format and original format. It introduces
  no pixel algorithm and fails explicitly for another image backend.
- `SdkDeskew` and `SdkPdfText` retain their existing guarantees.
- SDK 1.6.0's `PdfExporter.InitOcrStep` JPEG-encodes raster OCR input even when
  exporting lossless images. `SdkLosslessOcr` binds the internal upstream
  `ScanningContext.OcrRequestQueue.Enqueue` API to queue the canonical PNG first.
  The exporter reuses that exact result with the same image, engine and `OcrParams`.
  `RequiredOcrEngine` still records failures, verifies the exact expected count
  before publication, and drains active processes before image/temp disposal.
  There is no OCR-failure fallback. See [searchable PDF](searchable-pdf.md).

Likely integration overlaps are `SessionPage`/manifest version selection,
`PageRenderer`, the page toolbar/dialog handler, and the raster branch of
`Scanner.Export`. Preserve optional trailing arguments and source metadata when
combining other feature work. Any new pixel export must call `PageRenderer`;
SDK/GTK upgrades must rerun transform, flip and lossless OCR adapter tests.

Tests use generated images/PDFs and temporary folders. Coverage includes legacy
sessions, persistence, failed atomic saves, immutable sources, undo/redo/reset,
validation, crop/deskew/quarter-turn combinations, mirrored pixels, SDK correction,
preview cancellation, Apply/Cancel, narrow layout, PNG/PDF/CLI equality and captured
real OCR input equality. No scanner/printer jobs or email are used.
Physical scan appearance and printer output remain manual validation tasks.
