# Printing and email

Print and email respect **Selected pages only** when enabled, using the current
document order and final edits. Otherwise they include every current page.
Batch review must be confirmed before delivery. Delivery is disabled during
manual-duplex review.

## Direct printing

**Print…** / Ctrl+P discovers queues using `lpstat -e`. Choose a queue explicitly;
esyuscan initially selects the first alphabetic queue. Configure missing printers
through **Settings → System printer settings ↗**. Optional `cups`
provides `lpstat`, `lp`, and `cancel`; neither esyuscan nor its package configures or
starts CUPS. Printer discovery does not submit jobs.

The dialog prints the complete document in current order. Options are 1–999
collated copies, one-sided / two-sided long edge / two-sided short edge,
CUPS default paper / A4 / Letter, and fit-to-printable-area. Other settings come
from CUPS. Unsupported printer options are subject to CUPS and the printer's
behavior; the dialog does not claim device capability detection or provide
page-range printing. Searchable PDF OCR is unnecessary for printing and is not
run. Existing source PDF text and vectors remain intact through the established
combined-PDF exporter; deskew/crop pages use `PageRenderer` exactly as exports do.

esyuscan spools a PDF with `lp -H hold`, parses the job ID under the C locale, then
releases that exact job with `lp -i ID -H resume`. No `-s` file-reference option
is used: CUPS copies the data before the private PDF is removed. Cancellation
before submission creates no job. During submission esyuscan waits for the bounded
command to return its ID, then cancels that job instead of killing `lp` and losing
its identity. Cancellation or failure during release also attempts `cancel ID`.
If CUPS times out or returns an unrecognized ID, esyuscan reports uncertainty and
asks you to inspect the queue for a held job before retrying. It never retries
submission automatically. After release, **Cancel last print job** targets the
last successfully submitted ID in this esyuscan window, even after a session change.
Only CUPS can determine whether a job is already complete; physical pages cannot
be recalled. Submission success means queued, not printed.

Commands use argument lists without a shell. Executable availability, nonzero
exit codes, timeouts, and cancellation errors are reported. Session locks remain
held throughout preparation; originals, edits, undo history, and no-overwrite
export staging are unchanged.

Primary references: [CUPS command-line printing and options](https://www.cups.org/doc/options.html),
[CUPS lp](https://www.cups.org/doc/man-lp.html),
[CUPS cancel](https://www.cups.org/doc/man-cancel.html).

## PDF email composition

**Email PDF…** prepares a combined PDF with the current page order, rotations,
crops, deskew, and optional searchable-PDF language. It uses `Scanner.Export`,
including PDF passthrough and the existing OCR validation. It then runs
`xdg-email --utf8 --subject SESSION_NAME --attach ABSOLUTE_PDF_PATH`, without
recipients or a send operation. This opens a desktop composition workflow; esyuscan
cannot verify that a particular client accepted the attachment. Inspect the draft
before sending. Missing/default-client and launcher failures are visible.
A browser-only mail handler may ignore attachments: use a compatible desktop
client or attach a permanent **One document** export manually.

`xdg-utils` and a configured mail client are optional runtime dependencies, not
bundled. Once the launcher starts, Escape cannot reliably retract an opened
composer; esyuscan reports the launch result rather than promising cancellation of
a desktop draft. Launcher commands have a 30-second bound.

Temporary PDFs use private mode-0700 UUID directories in
`$XDG_DATA_HOME/esyuscan/outgoing` (normally `~/.local/share/esyuscan/outgoing`), with
filenames based on the session name, stripped of path separators and punctuation.
They are independent of session originals. A process-held lease prevents a
concurrent esyuscan window from cleaning an in-progress attachment. Cancelled or
failed preparation removes partial files. Completed attachments remain after
launcher failure as well as success, because a client may open a draft before
reporting an error. They survive esyuscan shutdown and restarts.

On subsequent print/email preparations, esyuscan deletes inactive attachment folders
older than seven days. It ignores unrelated folders and symbolic-link folders.
This is not a permanent export: save **One document** for drafts that will be
kept longer. You can remove old outgoing UUID folders yourself after confirming
that no draft still needs them. No email messages or address books are read.

Primary reference: [xdg-email manual](https://portland.freedesktop.org/doc/xdg-email.html),
which explicitly warns that attachments may need to remain after the command exits.

## Validation and remaining interoperability work

Automated tests use generated mixed PDFs/images and fake CUPS/mail launchers.
UI tests check busy/duplex guards, print options, and tiled layout.
Tests never submit real print jobs, send mail, or scan physical pages.

Physical validation is still required, by an operator who deliberately starts it:

- CUPS/printer interoperability: A4/Letter, copies/collation, both duplex binding
  edges, crop/rotation/page fitting, held-job release, cancel before/after queueing,
  offline queues, authentication, and non-ASCII titles.
- Mail clients: attachment presence and filename/Unicode subject, cancellation
  of the draft, delayed attachment reads and reopening a saved draft after esyuscan
  exits. Check Thunderbird and the actual desktop/default-client combination;
  browser handlers are not guaranteed to support attachments.
