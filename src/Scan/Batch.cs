namespace Scan;

public enum BatchGrouping { PerScan, Barcode }
public enum BatchPhase { Ready, Scanning, Delay, Prompt, Paused, Review }
public enum BatchScanStatus { Pending, Running, Interrupted, Complete }
public sealed record BatchOptions(int ScanCount, int DelaySeconds = 0, bool PromptBetweenScans = false,
    BatchGrouping Grouping = BatchGrouping.PerScan, string SeparatorText = "ESYUSCAN-SEPARATOR", bool IncludeSeparators = true)
{
    public void Validate()
    {
        if (ScanCount is < 1 or > 1000 || DelaySeconds is < 0 or > 86400 || !Enum.IsDefined(Grouping)
            || string.IsNullOrWhiteSpace(SeparatorText) || SeparatorText.Length > 200)
            throw new ArgumentException("Use 1–1000 scans, 0–86400 seconds, and separator text of 1–200 characters.");
    }
}
public sealed record BatchPage(string FileName, bool StartsDocument, bool Separator = false,
    bool DetectionDone = false, string? DetectedText = null);
public sealed record BatchScan(int Number, BatchScanStatus Status, BatchPage[] Pages);
public sealed record BatchState(BatchOptions Options, BatchPhase Phase, BatchScan[] Scans, bool Reviewed = false);
public sealed record BatchDocument(int Number, string[] PageIds);

public sealed partial class Session
{
    public BatchState? Batch { get; private set; }
    private bool batchRunning;
    public bool BatchNeedsDecision => Batch?.Scans.Any(scan => scan.Status == BatchScanStatus.Interrupted) == true;

    public void BeginBatch(BatchOptions options)
    {
        RequireNoDuplex();
        options.Validate();
        SetBatch(new(options, BatchPhase.Ready, []));
    }

    // The callback is one physical invocation. Completed pages enter Add(), which saves membership atomically.
    public async Task RunBatch(Func<CancellationToken, Task> scan, CancellationToken token,
        Action? changed = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (Batch is null || Batch.Phase == BatchPhase.Review || BatchNeedsDecision || batchRunning)
            throw new InvalidOperationException("Start a batch or resolve the interrupted scan before continuing.");
        batchRunning = true;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var state = Batch!;
                var pending = state.Scans.LastOrDefault()?.Status == BatchScanStatus.Pending;
                if (!pending && state.Scans.Length >= state.Options.ScanCount) break;
                if (!pending && state.Scans.Length > 0 && state.Options.DelaySeconds > 0)
                {
                    SetBatch(state with { Phase = BatchPhase.Delay });
                    changed?.Invoke();
                    await (delay ?? Task.Delay)(TimeSpan.FromSeconds(state.Options.DelaySeconds), token);
                }
                token.ThrowIfCancellationRequested();
                state = Batch!;
                var scans = state.Scans.ToArray();
                if (!pending) scans = [.. scans, new BatchScan(scans.Length + 1, BatchScanStatus.Pending, [])];
                scans[^1] = scans[^1] with { Status = BatchScanStatus.Running };
                SetBatch(state with { Phase = BatchPhase.Scanning, Scans = scans });
                changed?.Invoke();
                await scan(token);
                token.ThrowIfCancellationRequested();
                state = Batch!;
                scans = state.Scans.ToArray();
                scans[^1] = scans[^1] with { Status = BatchScanStatus.Complete };
                var finished = scans.Length == state.Options.ScanCount;
                SetBatch(state with { Scans = scans, Phase = finished ? BatchPhase.Review : BatchPhase.Prompt });
                changed?.Invoke();
                if (finished || state.Options.PromptBetweenScans) return;
            }
            SetBatch(Batch! with { Phase = BatchPhase.Review });
        }
        catch
        {
            var state = Batch!;
            var scans = state.Scans.Select(item => item.Status == BatchScanStatus.Running
                ? item with { Status = BatchScanStatus.Interrupted } : item).ToArray();
            SetBatch(state with { Phase = BatchPhase.Paused, Scans = scans, Reviewed = false });
            throw;
        }
        finally { batchRunning = false; changed?.Invoke(); }
    }

    public void ResolveInterruptedBatchScan(bool continueSameScan)
    {
        RequireBatchIdle();
        if (!BatchNeedsDecision) throw new InvalidOperationException("No interrupted scan to resolve.");
        var scans = Batch!.Scans.ToArray();
        scans[^1] = scans[^1] with { Status = continueSameScan ? BatchScanStatus.Pending : BatchScanStatus.Complete };
        SetBatch(Batch with { Scans = scans, Phase = BatchPhase.Paused, Reviewed = false });
    }

    public void ReviewBatchNow()
    {
        RequireBatchIdle();
        SetBatch(Batch! with
        {
            Phase = BatchPhase.Review,
            Reviewed = false,
            Scans = Batch!.Scans.Select(scan => scan.Status is BatchScanStatus.Pending or BatchScanStatus.Interrupted
                ? scan with { Status = BatchScanStatus.Complete } : scan).ToArray()
        });
    }

    public void ExitBatchKeepingAllPages()
    {
        RequireBatchIdle();
        SetBatch(null);
    }

    public void SetBatchPage(string fileName, bool startsDocument, bool separator)
    {
        RequireBatchReview();
        if (Batch!.Options.Grouping == BatchGrouping.Barcode
            && Batch.Scans.SelectMany(scan => scan.Pages).Any(page => page.FileName == fileName && !page.DetectionDone))
            throw new InvalidOperationException("Finish detection for this page before correcting its suggestions.");
        UpdateBatchPage(fileName, page => page with { StartsDocument = startsDocument, Separator = separator });
    }

    public void RecordBatchBarcode(string fileName, string? text)
    {
        RequireBatchReview();
        var match = text == Batch!.Options.SeparatorText;
        UpdateBatchPage(fileName, page => page with
        {
            DetectionDone = true,
            DetectedText = text,
            Separator = match,
            StartsDocument = match
        });
    }

    public void SetBatchIncludeSeparators(bool include)
    {
        RequireBatchReview();
        SetBatch(Batch! with { Options = Batch!.Options with { IncludeSeparators = include }, Reviewed = false });
    }

    public void ConfirmBatchReview(bool reviewed)
    {
        RequireBatchReview();
        if (reviewed && Batch!.Options.Grouping == BatchGrouping.Barcode
            && Batch.Scans.SelectMany(scan => scan.Pages).Any(page => !page.DetectionDone))
            throw new InvalidOperationException("Finish barcode detection before confirming review.");
        SetBatch(Batch! with { Reviewed = reviewed });
    }

    public IReadOnlyList<BatchDocument> BatchDocuments()
    {
        if (Batch is null) throw new InvalidOperationException("No batch to review.");
        var documents = new List<BatchDocument>();
        var current = new List<string>();
        void Finish()
        {
            if (current.Count == 0) return;
            documents.Add(new(documents.Count + 1, current.ToArray()));
            current.Clear();
        }
        foreach (var scan in Batch.Scans)
        {
            if (Batch.Options.Grouping == BatchGrouping.PerScan) Finish();
            foreach (var page in scan.Pages)
            {
                if (page.StartsDocument) Finish();
                if (!page.Separator || Batch.Options.IncludeSeparators) current.Add(page.FileName);
            }
        }
        Finish();
        return documents;
    }

    private void AddBatchPage(string fileName)
    {
        if (!batchRunning || Batch!.Phase != BatchPhase.Scanning)
            throw new InvalidOperationException("Continue the batch before adding pages.");
        if (pages.Any(page => page.FileName == fileName)) throw new InvalidOperationException("This page was already captured.");
        var scans = Batch.Scans.ToArray();
        var active = scans[^1];
        scans[^1] = active with
        {
            Pages = [.. active.Pages, new BatchPage(fileName,
            Batch.Options.Grouping == BatchGrouping.PerScan && active.Pages.Length == 0)]
        };
        var next = pages.Append(new SessionPage(fileName)).ToList();
        var state = Batch with { Scans = scans, Reviewed = false };
        Save(next, ManualDuplex, state);
        undo.Push(pages);
        pages = next;
        Batch = state;
        redo.Clear();
    }

    private void UpdateBatchPage(string fileName, Func<BatchPage, BatchPage> update)
    {
        if (!Batch!.Scans.SelectMany(scan => scan.Pages).Any(page => page.FileName == fileName))
            throw new ArgumentException("Select a page captured by this batch.");
        SetBatch(Batch with
        {
            Reviewed = false,
            Scans = Batch.Scans.Select(scan => scan with
            { Pages = scan.Pages.Select(page => page.FileName == fileName ? update(page) : page).ToArray() }).ToArray()
        });
    }

    private void RequireBatchIdle()
    {
        if (Batch is null || batchRunning) throw new InvalidOperationException("Stop the batch before changing its workflow.");
    }
    private void RequireBatchReview()
    {
        RequireBatchIdle();
        if (Batch!.Phase != BatchPhase.Review) throw new InvalidOperationException("Finish or stop scanning and choose review first.");
    }
    private void SetBatch(BatchState? state)
    {
        Save(pages, ManualDuplex, state);
        Batch = state;
    }
    private void RecoverBatch()
    {
        if (Batch is null || Batch.Phase == BatchPhase.Review) return;
        SetBatch(Batch with
        {
            Phase = BatchPhase.Paused,
            Reviewed = false,
            Scans = Batch.Scans.Select(scan => scan.Status == BatchScanStatus.Running
                ? scan with { Status = BatchScanStatus.Interrupted } : scan).ToArray()
        });
    }

    private static void ValidateBatch(SessionManifest manifest)
    {
        if (manifest.Batch is not { } state) return;
        void Invalid() => throw new InvalidDataException("Invalid batch recovery information.");
        if (manifest.Version < 3 || manifest.ManualDuplex is not null || state.Options is null || state.Scans is null
            || !Enum.IsDefined(state.Phase)) Invalid();
        try { state.Options!.Validate(); } catch (ArgumentException) { Invalid(); }
        var scans = state.Scans!;
        if (scans.Length > state.Options!.ScanCount || scans.Where((scan, i) => scan is null || scan.Number != i + 1
            || scan.Pages is null || !Enum.IsDefined(scan.Status)
            || (i < scans.Length - 1 && scan.Status != BatchScanStatus.Complete)).Any()) Invalid();
        var captured = scans.SelectMany(scan => scan.Pages).ToArray();
        if (captured.Any(page => page is null || !IsFileName(page.FileName))
            || captured.Select(page => page.FileName).Distinct().Count() != captured.Length) Invalid();
        var ids = captured.Select(page => page.FileName).ToArray();
        if (!manifest.Pages.TakeLast(ids.Length).Select(page => page.FileName).SequenceEqual(ids)
            || ids.Any(id => manifest.Pages.Count(page => page.FileName == id) != 1)) Invalid();
        if ((state.Phase == BatchPhase.Scanning) != scans.Any(scan => scan.Status == BatchScanStatus.Running)
            || state.Phase == BatchPhase.Ready && scans.Length != 0
            || state.Phase is BatchPhase.Prompt or BatchPhase.Delay or BatchPhase.Review && scans.Any(scan => scan.Status != BatchScanStatus.Complete)
            || state.Reviewed && (state.Phase != BatchPhase.Review
                || state.Options.Grouping == BatchGrouping.Barcode && captured.Any(page => !page.DetectionDone))) Invalid();
    }
}
