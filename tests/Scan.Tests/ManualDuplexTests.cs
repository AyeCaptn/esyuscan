using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class ManualDuplexTests : TestFiles
{
    private static Task Capture(Session session, int count)
    {
        AddPages(session, count);
        return Task.CompletedTask;
    }

    private static async Task CaptureBoth(Session session, int fronts, int backs)
    {
        session.BeginManualDuplex();
        await session.ScanDuplexPass(false, _ => Capture(session, fronts), CancellationToken.None);
        await session.ScanDuplexPass(true, _ => Capture(session, backs), CancellationToken.None);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InterleavesOnlyCapturedPagesAndApplyIsOneUndoableAction(bool reverse)
    {
        using var session = CreateSession();
        AddPages(session, 2);
        session.Rotate(0);
        var existing = session.Pages.ToArray();
        await CaptureBoth(session, 3, 3);
        var raw = session.Pages.ToArray();
        var hashes = raw.Select(page => SHA256.HashData(File.ReadAllBytes(session.OriginalPath(page)))).ToArray();
        session.SetDuplexOptions(reverse, 180);
        var fronts = raw.Skip(2).Take(3).ToArray();
        var backs = raw.Skip(5).ToArray();
        if (reverse) Array.Reverse(backs);
        var expected = existing.Concat(fronts.Zip(backs).SelectMany(pair =>
            new[] { pair.First, pair.Second with { Rotation = 180 } })).ToArray();

        Assert.That(session.PreviewDuplex(), Is.EqualTo(expected));
        Assert.That(session.Pages, Is.EqualTo(raw), "Review must not edit saved order or rotations.");
        session.ApplyManualDuplex();
        Assert.That(session.Pages, Is.EqualTo(expected));
        Assert.That(session.ManualDuplex, Is.Null);

        session.Undo();
        Assert.That(session.Pages, Is.EqualTo(raw), "One undo restores every captured side in scan order.");
        Assert.That(session.ManualDuplex, Is.Null, "Undo is an ordinary order edit, not an unfinished scanner job.");
        session.Redo();
        Assert.That(session.Pages, Is.EqualTo(expected));
        Assert.That(raw.Select(page => SHA256.HashData(File.ReadAllBytes(session.OriginalPath(page)))), Is.EqualTo(hashes));
    }

    [TestCase(3, 2)]
    [TestCase(2, 3)]
    [TestCase(2, 0)]
    public async Task MismatchCannotApplyAndKeepsEveryPageInScanOrder(int fronts, int backs)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        await CaptureBoth(session, fronts, backs);
        var raw = session.Pages.ToArray();
        session.SetDuplexOptions(true, 270);

        Assert.That(session.PreviewDuplex().Select(page => page.FileName), Is.EquivalentTo(raw.Select(page => page.FileName)));
        Assert.That(session.CanApplyDuplex, Is.False);
        Assert.Throws<InvalidOperationException>(session.ApplyManualDuplex);
        Assert.Throws<InvalidOperationException>(() => session.Rotate(0));
        Assert.Throws<InvalidOperationException>(session.Undo);
        session.KeepDuplexScanOrder();
        Assert.That(session.Pages, Is.EqualTo(raw));
        Assert.That(session.ManualDuplex, Is.Null);
    }

    [Test]
    public async Task EmptyFrontPassCannotScanBacksOrApply()
    {
        using var session = CreateSession();
        session.BeginManualDuplex();
        await session.ScanDuplexPass(false, _ => Task.CompletedTask, CancellationToken.None);

        Assert.That(session.CanApplyDuplex, Is.False);
        Assert.ThrowsAsync<InvalidOperationException>(() => session.ScanDuplexPass(true,
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.That(session.ManualDuplex!.Phase, Is.EqualTo(DuplexPhase.Reload));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task InterruptedPassPreservesCompletedSidesAndCanResume(bool backs, bool cancel)
    {
        var session = CreateSession();
        var folder = session.DirectoryPath;
        AddPages(session, 1);
        session.BeginManualDuplex();
        if (backs) await session.ScanDuplexPass(false, _ => Capture(session, 3), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        async Task Fail(CancellationToken token)
        {
            await Capture(session, 1);
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("Simulated feeder jam");
        }
        if (cancel) Assert.ThrowsAsync<OperationCanceledException>(() => session.ScanDuplexPass(backs, Fail, cancellation.Token));
        else Assert.ThrowsAsync<IOException>(() => session.ScanDuplexPass(backs, Fail, cancellation.Token));
        var raw = session.Pages.ToArray();
        session.Dispose();

        using var recovered = Session.Open(folder);
        Assert.That(recovered.Pages, Is.EqualTo(raw));
        Assert.That(recovered.Pages.All(page => File.Exists(recovered.OriginalPath(page))), Is.True);
        Assert.That(recovered.ManualDuplex!.Phase, Is.EqualTo(backs ? DuplexPhase.Review : DuplexPhase.Reload));
        await recovered.ScanDuplexPass(backs, _ => Capture(recovered, 2), CancellationToken.None);
        if (!backs) await recovered.ScanDuplexPass(true, _ => Capture(recovered, 3), CancellationToken.None);
        Assert.That(recovered.CanApplyDuplex, Is.True);
        recovered.ApplyManualDuplex();
        Assert.That(recovered.Pages.Count, Is.EqualTo(7));
        Assert.That(recovered.Pages.Select(page => page.FileName), Does.Contain(raw[1].FileName));
    }

    [Test]
    public void CancellationBeforeScanDoesNotInvokeTheScanner()
    {
        using var session = CreateSession();
        session.BeginManualDuplex();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;

        Assert.ThrowsAsync<OperationCanceledException>(() => session.ScanDuplexPass(false,
            _ => { invoked = true; return Task.CompletedTask; }, cancellation.Token));

        Assert.That(invoked, Is.False);
        Assert.That(session.Pages, Is.Empty);
        Assert.That(session.ManualDuplex!.Phase, Is.EqualTo(DuplexPhase.Reload));
    }

    [TestCase(DuplexPhase.Fronts)]
    [TestCase(DuplexPhase.Backs)]
    public async Task ReopeningAnAbruptlyInterruptedPassRecoversToAnExplicitPause(DuplexPhase interruptedPhase)
    {
        string folder;
        SessionPage[] raw;
        using (var session = CreateSession())
        {
            folder = session.DirectoryPath;
            session.BeginManualDuplex();
            if (interruptedPhase == DuplexPhase.Fronts) AddPages(session, 2);
            else
            {
                await session.ScanDuplexPass(false, _ => Capture(session, 2), CancellationToken.None);
                await session.ScanDuplexPass(true, _ => Capture(session, 2), CancellationToken.None);
            }
            raw = session.Pages.ToArray();
        }
        // Simulate process termination while the SDK owns a pass, bypassing finally.
        var manifestPath = Path.Combine(folder, "session.json");
        var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifestPath))!;
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with
        {
            ManualDuplex = manifest.ManualDuplex! with { Phase = interruptedPhase }
        }));

        using var recovered = Session.Open(folder);
        Assert.That(recovered.Pages, Is.EqualTo(raw));
        Assert.That(recovered.ManualDuplex!.Phase, Is.EqualTo(interruptedPhase == DuplexPhase.Fronts ? DuplexPhase.Reload : DuplexPhase.Review));
        Assert.That(recovered.ManualDuplex.Fronts.Length, Is.EqualTo(2));
        Assert.That(recovered.ManualDuplex.Backs.Length, Is.EqualTo(interruptedPhase == DuplexPhase.Backs ? 2 : 0));
        Assert.That(recovered.CanApplyDuplex, Is.EqualTo(interruptedPhase == DuplexPhase.Backs));
    }

    [Test]
    public async Task ReviewOptionsPersistWithoutApplyingAndFinalOrderSurvivesReopen()
    {
        string folder;
        SessionPage[] raw;
        SessionPage[] expected;
        using (var session = CreateSession())
        {
            folder = session.DirectoryPath;
            await CaptureBoth(session, 2, 2);
            raw = session.Pages.ToArray();
            session.SetDuplexOptions(true, 90);
            expected = session.PreviewDuplex().ToArray();
        }
        using (var recovered = Session.Open(folder))
        {
            Assert.That(recovered.Pages, Is.EqualTo(raw));
            Assert.That(recovered.ManualDuplex!.ReverseBacks, Is.True);
            Assert.That(recovered.ManualDuplex.BackRotation, Is.EqualTo(90));
            Assert.That(recovered.PreviewDuplex(), Is.EqualTo(expected));
            recovered.ApplyManualDuplex();
        }
        using var final = Session.Open(folder);
        Assert.That(final.Pages, Is.EqualTo(expected));
        Assert.That(final.ManualDuplex, Is.Null);
    }

    [Test]
    public async Task FailedApplyLeavesReviewAndRawOrderIntact()
    {
        using var session = CreateSession();
        await CaptureBoth(session, 2, 2);
        var raw = session.Pages.ToArray();
        var state = session.ManualDuplex;
        var manifest = Path.Combine(session.DirectoryPath, "session.json");
        File.Move(manifest, manifest + ".backup");
        Directory.CreateDirectory(manifest);
        try
        {
            Assert.Throws<IOException>(session.ApplyManualDuplex);
            Assert.That(session.Pages, Is.EqualTo(raw));
            Assert.That(session.ManualDuplex, Is.EqualTo(state));
        }
        finally
        {
            Directory.Delete(manifest);
            File.Move(manifest + ".backup", manifest);
        }
        session.ApplyManualDuplex();
        session.Undo();
        Assert.That(session.Pages, Is.EqualTo(raw));
    }

    [Test]
    public async Task ExportRequiresFinishingReview()
    {
        using var session = CreateSession();
        await CaptureBoth(session, 1, 1);
        using var scanner = new Scanner(Root);
        Assert.ThrowsAsync<InvalidOperationException>(() => scanner.Export(session,
            Path.Combine(Root, "blocked.pdf"), ExportFormat.CombinedPdf, CancellationToken.None));
        Assert.That(File.Exists(Path.Combine(Root, "blocked.pdf")), Is.False);
    }

    [Test]
    public void InvalidRecoveryReferencesAreRejected()
    {
        string folder;
        using (var session = CreateSession())
        {
            folder = session.DirectoryPath;
            session.BeginManualDuplex();
        }
        var manifest = Path.Combine(folder, "session.json");
        File.WriteAllText(manifest, """
            {"Version":1,"Name":"Invalid","Pages":[],"ManualDuplex":{"Phase":1,"Fronts":["missing.png"],"Backs":[]}}
            """);
        Assert.Throws<InvalidDataException>(() => Session.Open(folder).Dispose());
    }
}
