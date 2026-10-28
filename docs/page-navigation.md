# Page selection, order, export, and zoom

Click a thumbnail to select one page. **Ctrl+click** toggles that page;
**Shift+click** selects the range from the last ordinary or Ctrl click.
**Ctrl+Shift+click** adds that range to the existing selection. **Ctrl+A** selects
all pages. The preview toolbar shows **n of m selected** counts. Highlighted
thumbnails are selected; the accent outline identifies the focused page shown
in the preview. Ctrl-toggling a page off can leave it focused but unselected.

Outside text fields and scanner/OCR pickers:

- Left/Right, Up/Down or Page Up/Down browses and selects one page;
  Home/End goes to an edge. This works from the preview and toolbar too.
- Shift+arrow (or Shift+Home/End) extends a range from the selection anchor.
- Ctrl+arrow moves focus without changing the selection. Ctrl+Shift+arrow adds
  the anchor range. With the thumbnail sidebar focused, Ctrl+Space toggles the
  focused page and Space selects it.
- R rotates all selected pages clockwise; Delete removes all selected pages.
  Each is one undoable edit. Originals remain intact.
- Alt+Left/Right moves the selected block one position earlier/later, preserving
  its internal document order. At the corresponding edge it does nothing.

Drag any selected thumbnail vertically to move the entire selection. A plain
press on an already selected thumbnail preserves the group until release, so
it can be dragged without losing the other pages. A click without a drag then
selects just that page. The preview displays the insertion position during a
drag. Top/bottom sidebar edges scroll while the pointer moves. The top half of a
thumbnail inserts before it, the bottom half after it; the sidebar's empty end area
inserts at the end. Release outside the sidebar, or press Escape, to
cancel. Dropping within an unchanged contiguous block does nothing and leaves
redo available. Noncontiguous selections become a single block in document
order. A successful move saves once and undoes/redoes in one step.

Crop and Deskew apply to the focused preview page, even with a larger selection.
Rotate, remove and reorder apply to the highlighted selection. With no selection,
these group edits are disabled; the focused page can still be inspected or cropped.
Text fields and scanner/OCR pickers retain their own keys; page shortcuts do not
intercept their editing/navigation. Space outside the tray retains its Scan action.

## Selection recovery

Pages normally use their immutable original filename as identity, independent of
index, rotation, crop, and deskew. Page instances sharing an original receive a
unique optional `Id` field, preserved by edits and saved on the next session write.
Old sessions with shared originals load without a migration step; duplicate
identities are assigned IDs in memory. Ordinary sessions omit the field, existing
manifest versions remain 1/2, and older readers safely ignore this navigation
field. Importing/scanning appends pages while keeping the current selection and
focus; the first completed page of an empty session becomes selected. Rotate and
reorder retain selected identities and the focused identity. After removal,
surviving selected identities remain selected. If none survive, the nearest page
at the previous focus index is selected (or the last remaining page). An empty
document has no selection or focus.

Undo/redo restore the selected identities, focus, and range anchor last associated
with that in-memory document revision. This also recovers a removed selection,
including after removing every page. Selection/navigation and zoom themselves
are not undoable document edits. Opening another session starts with its first
page selected; selection, zoom and undo history are window-local and not persisted.
If a revision has not been displayed before (for example, intermediate pages of
a fast import), selection reconciles against the identities that remain.

During guided manual-duplex review, thumbnail selection, keyboard browsing and
zoom remain available in review order. Crop, deskew, rotate, removal, reorder,
undo/redo and all exports remain blocked until Apply or Keep scan order finishes.

## Export scope

The default is the whole document. Enable **Selected pages only** in the export
bar to export only the highlighted pages. The header count identifies the scope;
with zero pages selected all export buttons are disabled until pages are selected
or the checkbox is cleared. The focused page is included only if selected.
All three existing buttons remain accessible:

- **Separate images** produces consecutive numbered PNGs starting at page-001.
- **Separate PDFs** produces consecutive numbered one-page PDFs.
- **One document** produces one PDF containing the selected pages.

Every mode uses current document order, regardless of click order. Export does
not change the document, selection, or originals. Both PDF modes retain source-PDF
text/vectors where supported, quarter-turn rotations, and optional searchable OCR.
Edited pages follow PageRenderer's deskew/crop/rotation pipeline and deliberately
rasterize as before. OCR still verifies expected recognition counts for the pages
actually included. Cancellation, session locks, staging and no-overwrite publication
are unchanged. Selection is captured before opening the export dialog. The CLI's
existing export commands continue to export the whole document.

The additive `Scanner.Export(..., selectedPages: identities)` API accepts a set of
`SessionPage.Identity` values (normally original filenames). Null means all pages;
an empty or unknown selection fails
before staging or OCR. Duplicates are deduplicated; caller order is ignored.
Integration points for other feature areas are this optional final export argument,
Session's `MovePages`/`RotatePages`/`RemovePages` helpers, and the separate
`MainWindow.Navigation.cs` file. Keep page identity unchanged for edits.

## Preview zoom

**Fit page** (Ctrl+0) fits the rendered page to the current viewport and follows
window resizing and page rotation. **+ / −**, Ctrl+Plus / Ctrl+Minus (including
keypad variants), or Ctrl+mouse-wheel changes display scale by a factor of 1.25.
Manual zoom is limited to 10–400%; Fit can go below 10% for large scans and never
exceeds 400%. Minus at a Fit scale below 10% leaves Fit unchanged; Plus enters
manual zoom at 10%. The label shows Fit and/or the current percentage. Manual scale is
retained while browsing; each new page starts panning at its top left.

Two-finger touch pinch continuously scales from the start of each gesture;
native touchpad magnification is handled when provided by the platform. Avalonia
12.1.4's Wayland backend does not forward touchpad pinch gestures, so on Omarchy
use Ctrl+wheel or the zoom controls. Touchscreen pinch uses Avalonia's pinch
recognizer and does not require native touchpad support. Gesture zoom uses the
same bounds as manual zoom and never edits the document.

Drag the main preview to pan, or use its horizontal/vertical scrollbars and normal
wheel scrolling. Fit resets pan. Zoom changes only display dimensions; it never
writes to the session or changes crop coordinates, renderer input, output resolution
or PDF passthrough. The crop dialog continues to use its own full-canvas coordinates.

Tests use generated images/PDFs and Avalonia headless pointer/keyboard input,
including 760×680, 845×900 and 1280×900 layouts. No physical scanner/printer jobs
are used. Optional screenshot regeneration:

```sh
ESYUSCAN_NAVIGATION_SCREENSHOT_DIR=/tmp/esyuscan-navigation make test \
  ARGS='--filter FullyQualifiedName~ZoomPanAndNarrowLayout'
```
