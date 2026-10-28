# Manual double-sided scanning

esyuscan uses two ordinary single-sided NAPS2 SDK 1.6.0 feeder passes. It does not
request automatic duplex from the device, change the SDK, or infer an HP-specific
paper path. Automatic duplex scanning remains a separate paper-source choice.

## Scan and review

1. Create or open a session. Existing pages can remain in it.
2. Select the scanner, **Document feeder · a stack**, color, resolution, and size.
   Choose **Guided double-sided…**. This opens the guide without starting a job.
3. Put fronts in reading order, with the printed side facing the feeder's scan
   side as shown by its loading marks. Choose **Scan fronts**. Blank sides count
   as pages; retain them to avoid shifting later pairs.
4. esyuscan pauses. Keep sheets together, reload the unscanned sides toward the
   scan side, and choose **Scan backs / more** when ready. esyuscan never
   starts this pass automatically. Expand **Scanner settings** if needed.
5. Select every Front / Back in the page tray and inspect the large preview.
   **Reverse back stack** pairs the first front with the last captured back.
   Rotation choices 0°, 90°, 180°, and 270° apply only to this operation's backs,
   clockwise, and appear immediately in both previews. They do not change originals.
6. Check **I checked every pair and orientation**, then **Apply reviewed order**.
   Changing reversal or rotation clears the confirmation. Confirmation is also
   cleared after another pass and after reopening.

Only the operation's captured file identities are paired. Earlier session pages
retain their order and rotation, and the new interleaved pages follow them.

Apply saves the complete order, rotations, and end of the guide together. One
Undo restores the operation's raw scan order and rotations; one Redo reapplies
the complete result. Undo does not reopen or start a scanning operation. As with
other edits, undo history lasts only until the session closes.

## Count mismatches and recovery

Apply requires equal, nonzero front/back counts. A mismatch preview includes
every captured side; it is provisional and cannot be applied, even if confirmed.
Matching counts still require visual inspection: a double feed and a duplicated
sheet could otherwise cancel each other numerically.

- Cancel or Escape stops the active pass. Closing during scanning requests a
  safe stop; close again after it ends. Every completed side is already saved.
- A stopped or failed front pass returns to the reload step. **Scan fronts** can
  append remaining fronts before any backs have been captured.
- A stopped or failed back pass returns to review. **Scan backs / more**
  can append remaining backs when there are fewer backs than fronts.
- Resume with only the sheets/sides that have not completed. Do not feed the
  completed stack again. Check the feeder/output trays and page previews after
  a jam before deciding which sheets remain.
- If a missing side's position is uncertain, a sheet was skipped in the middle,
  or too many backs arrived, use **Keep scan order / exit**. This preserves all
  existing pages, then all captured fronts, then all captured backs, without
  applying reversal or rotation. Use normal page tools and additional scans to
  correct the document manually. The guide does not invent blanks, discard
  surplus pages, or guess where a missing sheet belonged.

Reopening a session restores its explicit front/back identities, counts, review
options, and pause. A process terminated during a pass recovers at reload (fronts)
or review (backs). Before Apply, the manifest always contains the raw scan order;
review is a display-only projection. Normal edits, undo/redo, import, ordinary
scan, and all exports are blocked until Apply or **Keep scan order / exit**, so
previewed order cannot accidentally be exported as a finished document. Scanner
settings remain available to reconnect or select the single-sided feeder.

If a disk write fails, esyuscan reports the error and retains the last successfully
saved manifest. Original image files are never overwritten or removed by the
guide. An interrupted image write may leave an unlisted original, as with an
ordinary scan; do not delete original files while recovering a storage failure.

## Remaining HP OfficeJet Pro 8020 hardware validation

No physical scan or print job was run to implement or test this feature. Before
using it for important documents, validate the actual reload procedure:

1. Mark two disposable sheets clearly with Front 1 / Back 1 and Front 2 / Back 2,
   plus an arrow toward the top of each side. Include an existing session page.
2. Scan fronts using the feeder's loading marks. Record which edge enters first
   and how the output stack lands. Reload backs using those marks; record the
   actual face/edge direction rather than assuming the output tray order.
3. Check whether reversal and 0° or 180° rotation produce the expected four-page
   sequence. Inspect 90°/270° if the sheets were loaded sideways. Confirm all
   existing pages remain unchanged and that Apply, Undo, Redo, and export agree.
4. On disposable pages, cancel each pass after a completed page and test a feeder
   interruption. Verify which sheets actually completed before appending only
   the remainder, including with reversed back order. Reopen between passes.
5. Test a blank back, an unequal count, and any device-side blank suppression or
   double-feed behavior. esyuscan must block unequal counts; correct a skipped
   middle side manually instead of treating the mismatch as a missing final back.

## Persistence and integration

The existing version-1 session manifest gains an optional `ManualDuplex` field.
Old version-1 sessions open without migration. The field stores phase, ordered
front/back filenames, back reversal, and back rotation; the original `Pages`
array remains the source of durable page order. Page addition and side identity
are saved in the same atomic manifest replacement. Apply clears the field in
that same replacement. Corrupt recovery references are rejected on open.

The implementation is in `ManualDuplex.cs` (a focused `Session` partial), with
small hooks in session saving, the existing view model/window, and the export
guard. The normal scanner capture path is reused unchanged. There are no new
packages, SDK changes, export formats, background services, or external tools.
Future import or page-edit features should honor the unfinished-duplex guard and
use the normal session mutation path once the guide ends.

Automated tests use generated images and injected pass callbacks, never a
physical scanner. They cover both stack directions, existing pages, rotations,
unequal/empty counts, undo/redo, cancellation, failed passes and failed Apply,
reopening during each pass and review, export blocking, confirmation controls,
and rendered review layouts at full and tiled window widths.
