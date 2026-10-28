using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using NUnit.Framework;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Scan.Tests;

[TestFixture]
public sealed class BatchTests : TestFiles
{
    private static Task Capture(Session session, int count)
    {
        AddPages(session, count);
        return Task.CompletedTask;
    }
    private static Task ScanAll(Session session, int pages = 1) => session.RunBatch(_ => Capture(session, pages), CancellationToken.None);

    [Test]
    public async Task RepeatedScansKeepExplicitIdentitiesExistingPagesAndDurableDocumentOrder()
    {
        string folder;
        string[][] documents;
        using (var session = CreateSession())
        {
            folder = session.DirectoryPath;
            AddPages(session, 2);
            session.BeginBatch(new(3));
            await ScanAll(session, 2);
            Assert.That(session.Batch!.Phase, Is.EqualTo(BatchPhase.Review));
            Assert.That(session.Batch.Scans.Select(scan => scan.Number), Is.EqualTo(new[] { 1, 2, 3 }));
            documents = session.BatchDocuments().Select(doc => doc.PageIds).ToArray();
            Assert.That(documents.SelectMany(ids => ids), Is.EqualTo(session.Pages.Skip(2).Select(page => page.FileName)));
            Assert.That(documents.Select(ids => ids.Length), Is.EqualTo(new[] { 2, 2, 2 }));
            session.ConfirmBatchReview(true);
        }
        using var recovered = Session.Open(folder);
        Assert.That(recovered.BatchDocuments().Select(doc => doc.PageIds), Is.EqualTo(documents));
        Assert.That(recovered.Batch!.Reviewed, Is.True);
        Assert.That(recovered.Pages.Count, Is.EqualTo(8));
    }

    [Test]
    public async Task PromptStopsBeforeNextPhysicalInvocationUntilExplicitContinue()
    {
        using var session = CreateSession();
        session.BeginBatch(new(3, PromptBetweenScans: true));
        var invocations = 0;
        Task Scan(CancellationToken _) { invocations++; return Capture(session, 1); }
        await session.RunBatch(Scan, CancellationToken.None);
        Assert.That((invocations, session.Batch!.Phase), Is.EqualTo((1, BatchPhase.Prompt)));
        await session.RunBatch(Scan, CancellationToken.None);
        Assert.That((invocations, session.Batch!.Phase), Is.EqualTo((2, BatchPhase.Prompt)));
        await session.RunBatch(Scan, CancellationToken.None);
        Assert.That((invocations, session.Batch!.Phase), Is.EqualTo((3, BatchPhase.Review)));
    }

    [Test]
    public async Task TimedDelayCancellationDoesNotStartAnotherScanAndResumeNeverReplaysCompletedScan()
    {
        var session = CreateSession();
        var folder = session.DirectoryPath;
        session.BeginBatch(new(2, DelaySeconds: 60));
        using var cts = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        Task Scan(CancellationToken _) { invocations++; return Capture(session, 1); }
        async Task Delay(TimeSpan duration, CancellationToken token)
        {
            Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(60)));
            waiting.SetResult();
            await Task.Delay(duration, token);
        }
        var running = session.RunBatch(Scan, cts.Token, delay: Delay);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        Assert.That(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
        var first = session.Pages[0];
        Assert.That(invocations, Is.EqualTo(1));
        Assert.That(session.BatchNeedsDecision, Is.False);
        session.Dispose();
        using var recovered = Session.Open(folder);
        Assert.That(recovered.Batch!.Phase, Is.EqualTo(BatchPhase.Paused));
        await recovered.RunBatch(_ => Capture(recovered, 1), CancellationToken.None,
            delay: (_, _) => Task.CompletedTask);
        Assert.That(recovered.Pages[0], Is.EqualTo(first));
        Assert.That(recovered.Pages.Count, Is.EqualTo(2));
        Assert.That(recovered.Batch.Scans.Length, Is.EqualTo(2));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task PartialFailuresRequireExplicitDecisionAndPreserveEveryPage(bool cancel, bool continueSameScan)
    {
        var session = CreateSession();
        var folder = session.DirectoryPath;
        session.BeginBatch(new(2));
        using var cts = new CancellationTokenSource();
        Task Fail(CancellationToken token)
        {
            AddPages(session, 1);
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("Fake feeder jam");
        }
        if (cancel) Assert.ThrowsAsync<OperationCanceledException>(() => session.RunBatch(Fail, cts.Token));
        else Assert.ThrowsAsync<IOException>(() => session.RunBatch(Fail, cts.Token));
        var first = session.Pages[0];
        session.Dispose();
        using var recovered = Session.Open(folder);
        Assert.That(recovered.BatchNeedsDecision, Is.True);
        Assert.ThrowsAsync<InvalidOperationException>(() => ScanAll(recovered));
        Assert.That(recovered.Pages[0], Is.EqualTo(first));
        recovered.ResolveInterruptedBatchScan(continueSameScan);
        await ScanAll(recovered);
        Assert.That(recovered.Pages.Count, Is.EqualTo(continueSameScan ? 3 : 2));
        Assert.That(recovered.BatchDocuments()[0].PageIds.Length, Is.EqualTo(continueSameScan ? 2 : 1));
        Assert.That(recovered.Pages.Select(page => page.FileName).Distinct().Count(), Is.EqualTo(recovered.Pages.Count));
    }

    [Test]
    public void AlreadyCancelledBatchDoesNotInvokeScanner()
    {
        using var session = CreateSession();
        session.BeginBatch(new(2));
        var invoked = false;
        Assert.ThrowsAsync<OperationCanceledException>(() => session.RunBatch(_ =>
        { invoked = true; return Task.CompletedTask; }, new CancellationToken(true)));
        Assert.That(invoked, Is.False);
        Assert.That(session.Batch!.Phase, Is.EqualTo(BatchPhase.Paused));
        Assert.That(session.Batch.Scans, Is.Empty);
    }

    [Test]
    public async Task AbruptTerminationDuringScanRecoversAsInterruptedWithoutStartingJobs()
    {
        string folder;
        using (var session = CreateSession())
        {
            folder = session.DirectoryPath;
            session.BeginBatch(new(2, PromptBetweenScans: true));
            await ScanAll(session);
        }
        var file = Path.Combine(folder, "session.json");
        var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(file))!;
        var scan = manifest.Batch!.Scans[0] with { Status = BatchScanStatus.Running };
        File.WriteAllText(file, JsonSerializer.Serialize(manifest with
        { Batch = manifest.Batch with { Phase = BatchPhase.Scanning, Scans = [scan] } }));
        using var recovered = Session.Open(folder);
        Assert.That(recovered.Batch!.Phase, Is.EqualTo(BatchPhase.Paused));
        Assert.That(recovered.BatchNeedsDecision, Is.True);
        Assert.That(recovered.Pages.Count, Is.EqualTo(1));
        recovered.ReviewBatchNow();
        recovered.ConfirmBatchReview(true);
        Assert.That(recovered.BatchDocuments().Single().PageIds.Single(), Is.EqualTo(recovered.Pages[0].FileName));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task FirstLastConsecutiveMissingAndNonMatchingSeparatorsHaveDeterministicGroups(bool include)
    {
        using var session = CreateSession();
        session.BeginBatch(new(2, Grouping: BatchGrouping.Barcode));
        await ScanAll(session, 4);
        // First, consecutive, and last separators. Non-matching barcode is ordinary content.
        var text = new[] { "ESYUSCAN-SEPARATOR", "ESYUSCAN-SEPARATOR", "invoice-42", "esyuscan-separator", null, null, null, "ESYUSCAN-SEPARATOR" };
        for (var i = 0; i < text.Length; i++) session.RecordBatchBarcode(session.Pages[i].FileName, text[i]);
        session.SetBatchIncludeSeparators(include);
        var ids = session.Pages.Select(page => page.FileName).ToArray();
        var expected = include ? new[] { ids[..1], ids[1..7], ids[7..] } : new[] { ids[2..7] };
        Assert.That(session.BatchDocuments().Select(doc => doc.PageIds), Is.EqualTo(expected));
        // Remove a false positive, then add a missed boundary manually. Both invalidate confirmation.
        session.ConfirmBatchReview(true);
        session.SetBatchPage(ids[1], false, false);
        Assert.That(session.Batch!.Reviewed, Is.False);
        session.SetBatchPage(ids[5], true, false);
        Assert.That(session.BatchDocuments().Single(doc => doc.PageIds.Contains(ids[5])).PageIds[0], Is.EqualTo(ids[5]));
        Assert.That(session.Pages.Count, Is.EqualTo(8), "Exclusion never removes a session page.");
    }

    [Test]
    public async Task AllSeparatorSheetsCanBeExcludedWithoutCreatingEmptyDocuments()
    {
        using var session = CreateSession();
        session.BeginBatch(new(1, Grouping: BatchGrouping.Barcode));
        await ScanAll(session, 3);
        foreach (var page in session.Pages) session.RecordBatchBarcode(page.FileName, "ESYUSCAN-SEPARATOR");
        session.SetBatchIncludeSeparators(false);
        session.ConfirmBatchReview(true);
        Assert.That(session.BatchDocuments(), Is.Empty);
        using var scanner = new Scanner(Root);
        Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ExportBatchDocuments(session, Path.Combine(Root, "empty"), CancellationToken.None));
        session.ExitBatchKeepingAllPages();
        Assert.That(session.Pages.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task BatchGuardsDuplexEditingImportsAndExportUntilReviewIsConfirmed()
    {
        using var session = CreateSession();
        session.BeginBatch(new(1, Grouping: BatchGrouping.Barcode));
        Assert.Throws<InvalidOperationException>(session.BeginManualDuplex);
        Assert.Throws<InvalidOperationException>(() => session.BeginBatch(new(1)));
        Assert.Throws<IOException>(() => Session.Open(session.DirectoryPath).Dispose());
        await ScanAll(session);
        Assert.Throws<InvalidOperationException>(() => session.Rotate(0));
        Assert.Throws<InvalidOperationException>(session.Undo);
        Assert.Throws<InvalidOperationException>(() => session.ConfirmBatchReview(true));
        using var scanner = new Scanner(Root);
        Assert.ThrowsAsync<InvalidOperationException>(() => scanner.Export(session, Path.Combine(Root, "blocked.pdf"), ExportFormat.CombinedPdf, CancellationToken.None));
        session.RecordBatchBarcode(session.Pages[0].FileName, null);
        session.ConfirmBatchReview(true);
        session.ExitBatchKeepingAllPages();
        session.Rotate(0);
        session.Undo();
        Assert.That(session.Pages[0].Rotation, Is.Zero);
        session.BeginManualDuplex();
        Assert.Throws<InvalidOperationException>(() => session.BeginBatch(new(1)));
    }

    [Test]
    public async Task FailedManifestSaveDoesNotApplyBoundaryChanges()
    {
        using var session = CreateSession();
        session.BeginBatch(new(1));
        await ScanAll(session);
        var before = session.Batch;
        var file = Path.Combine(session.DirectoryPath, "session.json");
        File.Move(file, file + ".backup");
        Directory.CreateDirectory(file);
        try
        {
            Assert.Throws<IOException>(() => session.SetBatchPage(session.Pages[0].FileName, false, true));
            Assert.That(session.Batch, Is.SameAs(before));
        }
        finally { Directory.Delete(file); File.Move(file + ".backup", file); }
    }

    [Test]
    public async Task CorruptBatchMembershipIsRejectedAndOldManifestStillLoads()
    {
        string folder;
        using (var session = CreateSession())
        {
            folder = session.DirectoryPath;
            session.BeginBatch(new(1));
            await ScanAll(session);
        }
        var file = Path.Combine(folder, "session.json");
        var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(file))!;
        File.WriteAllText(file, JsonSerializer.Serialize(manifest with
        {
            Batch = manifest.Batch! with
            { Scans = [new(1, BatchScanStatus.Complete, [new("missing.png", true)])] }
        }));
        Assert.Throws<InvalidDataException>(() => Session.Open(folder).Dispose());
        File.WriteAllText(file, JsonSerializer.Serialize(manifest with { Version = 1, Batch = null }));
        using var old = Session.Open(folder);
        Assert.That(old.Pages.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task CaptureUndoRedoRemainsAvailableAfterExplicitExitAndOriginalsNeverChange()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.BeginBatch(new(1));
        await ScanAll(session, 2);
        var raw = session.Pages.ToArray();
        var hashes = raw.Select(page => SHA256.HashData(File.ReadAllBytes(session.OriginalPath(page)))).ToArray();
        session.ExitBatchKeepingAllPages();
        session.Undo();
        Assert.That(session.Pages, Is.EqualTo(raw[..2]));
        session.Redo();
        Assert.That(session.Pages, Is.EqualTo(raw));
        Assert.That(raw.Select(page => SHA256.HashData(File.ReadAllBytes(session.OriginalPath(page)))), Is.EqualTo(hashes));
    }

    [Test]
    public async Task EmptyInvocationsCountButDoNotCreateEmptyDocuments()
    {
        using var session = CreateSession();
        session.BeginBatch(new(3));
        var invocation = 0;
        await session.RunBatch(_ => Capture(session, ++invocation == 2 ? 2 : 0), CancellationToken.None);
        Assert.That(session.Batch!.Scans.Length, Is.EqualTo(3));
        Assert.That(session.BatchDocuments().Single().PageIds.Length, Is.EqualTo(2));
    }

    [Test]
    public async Task ConcurrentRunsAndWorkflowChangesAreRejectedDuringPhysicalInvocation()
    {
        using var session = CreateSession();
        session.BeginBatch(new(1));
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var run = session.RunBatch(async _ => { AddPages(session, 1); entered.SetResult(); await release.Task; }, CancellationToken.None);
        await entered.Task;
        Assert.ThrowsAsync<InvalidOperationException>(() => ScanAll(session));
        Assert.Throws<InvalidOperationException>(session.ExitBatchKeepingAllPages);
        Assert.Throws<InvalidOperationException>(session.ReviewBatchNow);
        release.SetResult();
        await run;
        Assert.That(session.Pages.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task CancelledAnalysisKeepsResultsAndManualCorrectionsAcrossReopenWithoutRescanning()
    {
        var session = CreateSession();
        var folder = session.DirectoryPath;
        session.BeginBatch(new(1, Grouping: BatchGrouping.Barcode));
        await ScanAll(session, 2);
        using var scanner = new Scanner(Root);
        using var cts = new CancellationTokenSource();
        Assert.That(async () => await scanner.DetectBatchBarcodes(session, cts.Token, () => cts.Cancel()),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(session.Batch!.Scans[0].Pages.Select(page => page.DetectionDone), Is.EqualTo(new[] { true, false }));
        session.SetBatchPage(session.Pages[0].FileName, true, true);
        Assert.Throws<InvalidOperationException>(() => session.SetBatchPage(session.Pages[1].FileName, false, false));
        session.Dispose();
        using var recovered = Session.Open(folder);
        await scanner.DetectBatchBarcodes(recovered, CancellationToken.None);
        Assert.That(recovered.Batch!.Scans[0].Pages[0].Separator, Is.True, "Completed manual review must not be overwritten by retry.");
        Assert.That(recovered.Batch.Scans[0].Pages.All(page => page.DetectionDone), Is.True);
        Assert.That(recovered.Pages.Count, Is.EqualTo(2));
    }

    [Test, Category("Integration")]
    public async Task DocumentExclusionsDoNotChangeOrdinaryExportFormats()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.BeginBatch(new(1, Grouping: BatchGrouping.Barcode));
        await ScanAll(session, 3);
        var ids = session.Pages.Select(page => page.FileName).ToArray();
        foreach (var page in session.Pages.Skip(1)) session.RecordBatchBarcode(page.FileName, page.FileName == ids[2] ? "ESYUSCAN-SEPARATOR" : null);
        session.SetBatchIncludeSeparators(false);
        session.ConfirmBatchReview(true);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "grouped");
        await scanner.ExportBatchDocuments(session, target, CancellationToken.None);
        var mapping = JsonSerializer.Deserialize<BatchDocument[]>(File.ReadAllText(Path.Combine(target, "documents.json")))!;
        Assert.That(mapping.SelectMany(doc => doc.PageIds), Is.EqualTo(new[] { ids[1], ids[3] }));
        Assert.That(mapping.Length, Is.EqualTo(2));
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var path = Path.Combine(Root, format.ToString());
            await scanner.Export(session, path, format, CancellationToken.None);
            if (format == ExportFormat.CombinedPdf)
            {
                using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.That(pdf.PageCount, Is.EqualTo(4));
            }
            else if (format == ExportFormat.CombinedTiff)
            {
                using var context = new NAPS2.Scan.ScanningContext(new NAPS2.Images.Gtk.GtkImageContext());
                var count = 0;
                await foreach (var image in new NAPS2.ImportExport.ImageImporter(context).Import(path))
                { image.Dispose(); count++; }
                Assert.That(count, Is.EqualTo(4));
            }
            else Assert.That(Directory.GetFiles(path).Length, Is.EqualTo(4));
        }
        Assert.That(session.Pages.Select(page => page.FileName), Is.EqualTo(ids));
    }

    [Test, Category("Integration")]
    public async Task DestinationRaceKeepsExistingFolderAndDiscardsWholeStagedSet()
    {
        using var session = CreateSession();
        session.BeginBatch(new(2));
        await ScanAll(session);
        session.ConfirmBatchReview(true);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "raced");
        Assert.ThrowsAsync<IOException>(() => scanner.ExportBatchDocuments(session, target, CancellationToken.None,
            progress: update =>
            {
                if (update.Fraction < .5 || Directory.Exists(target)) return;
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "existing.txt"), "keep me");
            }));
        Assert.That(Directory.GetFiles(target).Select(Path.GetFileName), Is.EqualTo(new[] { "existing.txt" }));
        Assert.That(Directory.EnumerateDirectories(Root, ".esyuscan*"), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Category("Integration")]
    public async Task CliDocumentsRequiresPersistedReviewAndExportsOnlyExplicitMembership(bool confirmed)
    {
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            session.BeginBatch(new(2));
            await ScanAll(session);
            if (confirmed) session.ConfirmBatchReview(true);
        }
        var target = Path.Combine(Root, "cli-documents");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(Program).Assembly.Location, "export", Path.Combine(Root, "session"), "--format", "documents", "--output", target, "--json" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(process.ExitCode, Is.EqualTo(confirmed ? 0 : 1), await errors);
        using var json = JsonDocument.Parse(await output);
        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.EqualTo(confirmed));
        Assert.That(Path.Exists(target), Is.EqualTo(confirmed));
        if (confirmed) Assert.That(json.RootElement.GetProperty("pages").GetInt32(), Is.EqualTo(2));
        if (confirmed)
            Assert.That(JsonSerializer.Deserialize<BatchDocument[]>(File.ReadAllText(Path.Combine(target, "documents.json")))!.Length, Is.EqualTo(2));
    }

    private static void BarcodeImage(string path, BarcodeFormat format, string text)
    {
        var matrix = new MultiFormatWriter().encode(text, format, 640, 400,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 16 });
        using var bitmap = new SKBitmap(matrix.Width, matrix.Height);
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++) bitmap.SetPixel(x, y, matrix[x, y] ? SKColors.Black : SKColors.White);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        png.SaveTo(output);
    }

    [TestCase(BarcodeFormat.QR_CODE)]
    [TestCase(BarcodeFormat.CODE_128)]
    public async Task UpstreamDetectionFindsGeneratedBarcodeAndOrdinaryPagesStayIncluded(BarcodeFormat format)
    {
        using var session = CreateSession();
        session.BeginBatch(new(1, Grouping: BatchGrouping.Barcode));
        await session.RunBatch(_ =>
        {
            var path = session.NextOriginalPath();
            BarcodeImage(path, format, "ESYUSCAN-SEPARATOR");
            session.Add(path);
            AddPages(session, 1);
            return Task.CompletedTask;
        }, CancellationToken.None);
        using var scanner = new Scanner(Root);
        await scanner.DetectBatchBarcodes(session, CancellationToken.None);
        var pages = session.Batch!.Scans[0].Pages;
        Assert.That(pages[0].DetectedText, Is.EqualTo("ESYUSCAN-SEPARATOR"));
        Assert.That(pages[0].Separator, Is.True);
        Assert.That(pages[1].DetectionDone, Is.True);
        Assert.That(pages[1].Separator, Is.False);
        session.SetBatchIncludeSeparators(false);
        Assert.That(session.BatchDocuments().Single().PageIds, Is.EqualTo(new[] { session.Pages[1].FileName }));
    }

    [Test, Category("Integration")]
    public async Task DocumentsExportInReviewedOrderAndNeverOverwriteOrPublishPartialSets()
    {
        using var session = CreateSession();
        session.BeginBatch(new(2));
        await session.RunBatch(_ =>
        {
            // Different dimensions make PDF page ordering observable after real SDK export.
            for (var i = 0; i < 2; i++)
            {
                var path = session.NextOriginalPath();
                using var image = new SKBitmap(100 + session.Pages.Count * 20, 200);
                image.Erase(SKColors.White);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                using (var output = File.Create(path)) png.SaveTo(output);
                session.Add(path);
            }
            return Task.CompletedTask;
        }, CancellationToken.None);
        session.ConfirmBatchReview(true);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "documents");
        await scanner.ExportBatchDocuments(session, target, CancellationToken.None);
        using var first = PdfReader.Open(Path.Combine(target, "document-001.pdf"), PdfDocumentOpenMode.Import);
        using var second = PdfReader.Open(Path.Combine(target, "document-002.pdf"), PdfDocumentOpenMode.Import);
        Assert.That((first.PageCount, second.PageCount), Is.EqualTo((2, 2)));
        var widths = first.Pages.Cast<PdfSharpCore.Pdf.PdfPage>().Concat(second.Pages.Cast<PdfSharpCore.Pdf.PdfPage>()).Select(page => page.Width.Point / page.Height.Point).ToArray();
        Assert.That(widths, Is.EqualTo(new[] { .5, .6, .7, .8 }).Within(.01));
        Assert.That(JsonSerializer.Deserialize<BatchDocument[]>(File.ReadAllText(Path.Combine(target, "documents.json")))!
            .SelectMany(doc => doc.PageIds), Is.EqualTo(session.Pages.Select(page => page.FileName)));
        Assert.ThrowsAsync<IOException>(() => scanner.ExportBatchDocuments(session, target, CancellationToken.None));
        var partial = Path.Combine(Root, "cancelled");
        using var cts = new CancellationTokenSource();
        Assert.ThrowsAsync<OperationCanceledException>(() => scanner.ExportBatchDocuments(session, partial, cts.Token,
            progress: update => { if (update.Fraction >= .5) cts.Cancel(); }));
        Assert.That(Path.Exists(partial), Is.False);
        Assert.That(Directory.EnumerateDirectories(Root, ".esyuscan*"), Is.Empty);
        Assert.That(session.Pages.Count, Is.EqualTo(4));
    }

    [AvaloniaTest]
    public async Task BatchReviewKeepsMainScanAndEditingGuardedAndAllThreeExportsAvailableAfterReview()
    {
        using var model = new MainViewModel();
        var session = CreateSession();
        session.BeginBatch(new(1));
        await ScanAll(session);
        model.SetSession(session);
        Assert.That(model.CanScan, Is.False);
        Assert.That(model.CanStartDuplex, Is.False);
        Assert.That(model.CanChangePage, Is.False);
        Assert.That(model.CanExport, Is.False);
        session.ConfirmBatchReview(true);
        model.SyncPages();
        Assert.That(model.CanExport, Is.True);
        Assert.That(model.CanOpenBatch, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task BatchWindowCancelStaysVisibleAndEnabledDuringFakePhysicalScan(bool escape)
    {
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        var entered = new TaskCompletionSource();
        var ended = new TaskCompletionSource();
        var window = new BatchWindow(session, scanner, async (pageAdded, token) =>
        {
            AddPages(session, 1);
            pageAdded();
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, () => ended.TrySetResult())
        { Width = 520, Height = 600 };
        window.Show();
        try
        {
            window.FindControl<Button>("RunBatch")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            window.UpdateLayout();
            var button = window.FindControl<Button>("CancelBatch")!;
            Assert.That(button.IsEffectivelyEnabled && button.IsVisible, Is.True);
            var edge = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
            Assert.That(edge.Y, Is.LessThanOrEqualTo(window.ClientSize.Height));
            if (escape) window.RaiseEvent(new Avalonia.Input.KeyEventArgs
            { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(session.BatchNeedsDecision, Is.True);
            Assert.That(session.Pages.Count, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    public async Task RenderBatchReview()
    {
        using var session = CreateSession();
        session.BeginBatch(new(2));
        await session.RunBatch(_ => Capture(session, 2), CancellationToken.None);
        using var scanner = new Scanner(Root);
        var window = new BatchWindow(session, scanner, null, () => { });
        window.Show();
        window.UpdateLayout();
        try
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(760, 860), new Vector(96, 96));
            bitmap.Render(window);
            var folder = Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR");
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, "batch.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            Assert.That(window.FindControl<ListBox>("BatchPages")!.ItemCount, Is.EqualTo(4));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task NarrowBatchReviewHasScrollableControlsAndCancellationStaysAccessible()
    {
        using var session = CreateSession();
        session.BeginBatch(new(1));
        await ScanAll(session, 3);
        using var scanner = new Scanner(Root);
        var window = new BatchWindow(session, scanner, null, () => { }) { Width = 520, Height = 600 };
        window.Show();
        window.UpdateLayout();
        try
        {
            Assert.That(window.FindControl<ListBox>("BatchPages")!.ItemCount, Is.EqualTo(3));
            var review = window.FindControl<CheckBox>("BatchReviewed")!;
            review.IsChecked = true;
            Assert.That(window.FindControl<Button>("ExportBatch")!.IsEnabled, Is.True);
            var edge = review.TranslatePoint(new Point(review.Bounds.Width, 0), window)!.Value;
            Assert.That(edge.X, Is.LessThanOrEqualTo(window.ClientSize.Width));
            Assert.That(session.Batch!.Reviewed, Is.True);
        }
        finally { window.Close(); }
    }
}
