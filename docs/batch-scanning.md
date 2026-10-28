# Batch scanning and document review

Choose scanner settings in the main window, then **Batch setup / review…**.
Ordinary **Scan pages** still performs a single scan. Batch scanning and guided
manual duplex cannot run together; complete/exit one workflow before starting
the other. Automatic feeder duplex, if selected in scanner settings, is a single
scan invocation, not the guided two-pass workflow.

## Setup and run

Set **Scan count** (1–1000), **Delay** (0–86400 seconds), and optional **Prompt
between scans**. One scan means one SDK scan invocation: a flatbed page, or the
entire feeder stack. Delays occur before each subsequent scan. A prompt stops
the workflow at a durable checkpoint; **Ready: continue to next scan** explicitly
starts the next delay/scan. There is no prompt before the first scan or after
the final one. Cancellation works during scanning, delays, barcode detection,
and export. The cancel button stays outside the scrollable content.

The batch uses the scanner/settings selected when its window is opened,
including custom paper dimensions, alignment and the chosen scan area. Close
the batch window to change those settings, then reopen it before resuming.
After reopening a session, check the scanner, paper source, color, size, DPI,
and auto-deskew before explicitly continuing. Settings are not automatically
replayed from an old physical job. No scan is automatically started by opening
a session or the batch window.

Completed pages are flushed to immutable PNG originals. Each page's filename,
scan membership, initial boundary and page-list entry commit in the same atomic
session manifest replacement. Empty successful invocations count toward the
requested number of scans but do not create empty documents. Existing session
pages are not members of the new batch.

## Interruption and recovery

Errors/cancellation retain completed pages and pause the batch. A failed or
interrupted physical invocation is never automatically retried. Choose either:

- **Scan only remaining sheets of interrupted scan**: retain its saved pages and
  append the next invocation's pages to that same logical scan. Remove already
  scanned sheets from the feeder first. Then explicitly resume the batch.
- **Accept partial scan; advance to next scan**: count the partial/empty invocation
  as one scan, retaining its pages, then explicitly resume the remaining scans.
- **Stop batch and review completed pages**: abandon remaining invocations and
  review every saved page. No new physical jobs are started.

Process termination during a physical invocation recovers as an interrupted
scan. Other unfinished phases recover paused. Delays restart their full duration
when explicitly resumed; they do not cause a catch-up scan. The workflow cannot
know which sheets remain physically loaded, or detect duplicate physical sheets
fed again by the operator. It never replays a completed invocation itself.

Close the batch window to keep the checkpoint for later. **Exit batch: keep all
pages; discard grouping** deliberately ends the workflow, retains every sheet
(including excluded separators), and enables ordinary editing/import/undo/redo.
Undo history lasts until session close, as before. Exit discards batch membership
and boundary annotations, so export grouped documents before exiting if needed.

## Grouping and barcode review

**One document per scan** starts a document at each scan. It never joins pages
across scans. Additional boundaries can split a scan during review.

**Separate at matching barcode** runs upstream NAPS2 barcode detection on the
saved PNGs after scanning. It supports QR and Code 128 and compares the detected
text exactly, case-sensitively, to the configured text (default
`ESYUSCAN-SEPARATOR`). An ordinary invoice barcode with different text is content.
esyuscan uses NAPS2 SDK 1.6.0 `FileImporter`, `ImportParams.BarcodeDetectionOptions`,
`ProcessedImage.PostProcessingData.Barcode`, and its bundled ZXing.Net decoder;
there is no custom decoder or new runtime dependency. See the upstream
[options](https://www.naps2.com/sdk/doc/api/NAPS2.Scan.BarcodeDetectionOptions.html),
[import API](https://www.naps2.com/sdk/doc/api/NAPS2.ImportExport.ImportParams.html),
and [barcode result](https://www.naps2.com/sdk/doc/api/NAPS2.Images.Barcode.html).

Detection is a suggestion, not proof that a sheet is disposable. The SDK returns
one detected text per page, so multiple codes, damaged/small codes, page contents,
and orientation can produce missed or unexpected suggestions. Select **every
page** in the batch list and inspect its preview, barcode result, document number,
and separator label. The list identifies pending detection. Correct **Start a
document before selected page** and **Selected page is a separator sheet**
independently, including missed separators. Detection only processes pending
pages; completed detections and manual corrections survive reopening. **Detect
pending barcodes** continues a cancelled/failed analysis without rescanning.

**Include separator sheets in documents** is on by default. If disabled, only
pages marked as separator sheets are excluded from grouped document exports.
They remain in the session and review list. Boundary and inclusion behavior:

- A boundary starts a document **before** that page. A kept separator is the
  first page of its document. An excluded separator starts the following group.
- Pages before the first separator form a document of their own.
- Consecutive kept separators create separate, potentially separator-only
  documents. Consecutive excluded separators do not create empty documents.
- A last kept separator can form a one-page document. An excluded final separator
  creates no empty trailing document.
- Without matching separators, all batch pages form one document, even across
  scans. Review can insert missing boundaries manually.
- No empty documents are exported. An all-excluded batch cannot export documents.

The summary shows document/page counts and excluded sheets. Confirm **I checked
every page, boundary and separator** before exporting. Pending barcode analysis
blocks confirmation. Changing any boundary/inclusion flag resets confirmation.
All page identities, detections, corrections and review confirmation persist.

## Export and ordinary editing

**Export reviewed documents…** writes `document-001.pdf`, `document-002.pdf`, etc.
in reviewed scan/page order into a new folder. The entire folder is staged beside
the target and renamed only after every document succeeds. Failure/cancellation
publishes no partial set, and existing destinations are never overwritten.
`documents.json` maps each document number to original page identities, without
using barcode text in filenames. Existing searchable-PDF options selected before
opening the window apply to document PDFs, using the same verified OCR pipeline.

The main window's three original export workflows remain available after batch
review is confirmed: **Separate images**, **Separate PDFs**, **One document**.
Those modes export **all session pages**, including pre-existing pages and all
separator sheets, in session order. They do not silently adopt document exclusions.
To edit/crop/rotate/reorder pages, exit the batch keeping all pages first.

CLI export of confirmed documents is also available (close the GUI session first):

```sh
esyuscan export SESSION --format documents --output NEW_FOLDER --json
esyuscan export SESSION --format documents --output NEW_FOLDER --searchable --ocr-language eng --json
```

CLI never starts/resumes physical scans or confirms an unreviewed batch. Existing
`images`, `pdfs`, and `pdf` formats retain their semantics.

## Storage and integration

Active/reviewed batch sessions use manifest version 3. Version 1/2 sessions still
open unchanged; older applications reject version 3. `Batch` stores options,
phase, ordered numbered scans and their status, and explicit page identities with
boundary, separator, detection and text fields. Review does not alter `Pages`,
PDF sources, rotations, crops or deskew values. Session locks remain exclusive.
The manifest validates unique membership, suffix scan order, workflow enums and
required detection completion before trusting export confirmation. Exiting the
batch removes this extra state; the session saves as version 1/2 as appropriate.

Integration touches: `Session`'s manifest/save/add path and shared editing guard;
`Scanner`'s durable capture, shared export pipeline and new document exporter;
`MainViewModel` guards; one main-window button; CLI format parsing. The dedicated
`BatchWindow` owns setup/run/review UI. Other feature branches should preserve
these guards and the optional `Batch` field when modifying session saves.

## Validation and limitations

Automated tests use a fake scan callback, generated QR/Code 128 sheets and
ordinary images. They cover explicit prompts, real cancellable delays, partial
failures, process-interruption recovery, membership validation, session locking,
first/last/consecutive/missing separators, manual corrections, safe inclusion,
real SDK PDF export order, cancellation cleanup, no-overwrite publication, and
headless narrow-window review. No physical scanner/printer job is run.

Still validate with actual hardware: feeder timing/empty-feeder behavior, scanner
cancellation latency, remaining-sheet reload after a jam, and barcode legibility
at the intended scan resolution. NAPS2/driver cancellation may finish an in-flight
page before stopping; each delivered completed page remains saved. Power loss
before a completed PNG is registered in the manifest is outside the transaction
boundary, as in ordinary scans; unrelated orphan originals are never deleted.
