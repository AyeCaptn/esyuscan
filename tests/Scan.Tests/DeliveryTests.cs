using System.Security.Cryptography;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Pdf;
using NAPS2.Scan;
using NUnit.Framework;
using PdfSharpCore.Pdf.Content;
using PdfSharpCore.Pdf.Content.Objects;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Scan.Tests;

public sealed class FakeDesktopCommand : IDesktopCommand
{
    public List<(string Command, string[] Arguments)> Calls { get; } = [];
    public Func<string, string[], CommandResult>? Respond { get; set; }
    public Task<CommandResult> Run(string command, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = arguments.ToArray();
        Calls.Add((command, args));
        return Task.FromResult(Respond?.Invoke(command, args) ?? (command == "lpstat" ? new(0, "Office\n", "") : new(0, "request id is Office-42 (1 file(s))\n", "")));
    }
}

[TestFixture]
public sealed class DeliveryTests : TestFiles
{
    [Test]
    public async Task PrinterDiscoveryAndSubmissionUseArgumentListsAndHoldBeforeRelease()
    {
        var fake = new FakeDesktopCommand { Respond = (command, _) => command == "lpstat" ? new(0, "Office\nOther\nStudio+colour\nOffice\ninvalid queue\n", "") : new(0, "request id is Office-42 (1 file(s))", "") };
        var cups = new CupsPrinting(fake);
        Assert.That(await cups.Printers(default), Is.EqualTo(new[] { "Office", "Other", "Studio+colour" }));
        var title = "Document; $(never-run)";
        Assert.That(await cups.Submit(Path.Combine(Root, "with spaces.pdf"), title, new("Office", 3, PrintSides.ShortEdge, "Letter", false), default), Is.EqualTo("Office-42"));
        Assert.That(fake.Calls[1].Arguments, Is.EqualTo(new[] { "-H", "hold", "-d", "Office", "-n", "3", "-t", title,
            "-o", "collate=true", "-o", "sides=two-sided-short-edge", "-o", "media=Letter", "-o", "fit-to-page=false", Path.Combine(Root, "with spaces.pdf") }));
        Assert.That(fake.Calls[2], Is.EqualTo(("lp", new[] { "-i", "Office-42", "-H", "resume" })));
        await cups.Cancel("Office-42");
        Assert.That(fake.Calls.Last(), Is.EqualTo(("cancel", new[] { "Office-42" })));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CancellationDuringCupsSubmissionOrReleaseCancelsTheExactJob(bool duringSubmit)
    {
        using var cancel = new CancellationTokenSource();
        var fake = new FakeDesktopCommand
        {
            Respond = (command, args) =>
            {
                if (command == "lp" && (duringSubmit ? args.Contains("hold") : args.Contains("resume"))) cancel.Cancel();
                return new(0, "request id is Office-42 (1 file(s))", "");
            }
        };
        Assert.ThrowsAsync<OperationCanceledException>(() => new CupsPrinting(fake).Submit("out.pdf", "Document", new("Office"), cancel.Token));
        Assert.That(fake.Calls.Last(), Is.EqualTo(("cancel", new[] { "Office-42" })));
        Assert.That(fake.Calls.Count(call => call.Arguments.Contains("resume")), Is.EqualTo(duringSubmit ? 0 : 1));
    }

    [TestCase("Office", 0)]
    [TestCase("Office", 1000)]
    [TestCase("-bad", 1)]
    [TestCase("bad\nqueue", 1)]
    public void InvalidJobOptionsNeverLaunchCups(string printer, int copies)
    {
        var fake = new FakeDesktopCommand();
        Assert.ThrowsAsync<ArgumentException>(() => new CupsPrinting(fake).Submit("out.pdf", "Document", new(printer, copies), default));
        Assert.That(fake.Calls, Is.Empty);
    }

    [TestCase("Office-1 extra")]
    [TestCase("-Office-1")]
    [TestCase("Office-1;other")]
    public void InvalidJobIdNeverLaunchesCancel(string id)
    {
        var fake = new FakeDesktopCommand();
        Assert.ThrowsAsync<ArgumentException>(() => new CupsPrinting(fake).Cancel(id));
        Assert.That(fake.Calls, Is.Empty);
    }

    [Test]
    public void UnrecognizedCupsResponseDoesNotReleaseOrResubmit()
    {
        var fake = new FakeDesktopCommand { Respond = (_, _) => new(0, "unrecognized response", "") };
        var error = Assert.ThrowsAsync<IOException>(() => new CupsPrinting(fake).Submit("out.pdf", "Document", new("Office"), default));
        Assert.That(error!.Message, Does.Contain("held job may exist"));
        Assert.That(fake.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public void ReleaseFailureCancelsAndCancellationFailureReportsJobId()
    {
        var fake = new FakeDesktopCommand { Respond = (command, args) => args.Contains("hold") ? new(0, "request id is Office-42", "") : new(1, "", "scheduler unavailable") };
        var error = Assert.ThrowsAsync<IOException>(() => new CupsPrinting(fake).Submit("out.pdf", "Document", new("Office"), default));
        Assert.That(error!.Message, Does.Contain("Office-42").And.Contain("Cancellation also failed").And.Contain("scheduler unavailable"));
        Assert.That(fake.Calls.Last().Command, Is.EqualTo("cancel"));
    }

    [Test]
    public void DiscoveryAndSubmissionFailuresAreVisible()
    {
        var fake = new FakeDesktopCommand { Respond = (_, _) => new(1, "", "CUPS not running") };
        var cups = new CupsPrinting(fake);
        Assert.That(Assert.ThrowsAsync<IOException>(() => cups.Printers(default))!.Message, Does.Contain("CUPS not running"));
        Assert.ThrowsAsync<IOException>(() => cups.Submit("out.pdf", "Document", new("Office"), default));
        Assert.That(fake.Calls.Any(call => call.Arguments.Contains("resume")), Is.False);
    }

    [Test]
    public async Task PrivateAttachmentSurvivesCompositionAndAppLifetimeThenExpiresOnLaterUse()
    {
        var root = Path.Combine(Root, "outgoing");
        string path;
        var fake = new FakeDesktopCommand();
        using (var outgoing = new OutgoingDocument("../../Quarter: 1", root))
        {
            path = outgoing.PdfPath;
            File.WriteAllText(path, "%PDF-fixture");
            outgoing.RetainForMail();
            await new DesktopMail(fake).Compose(path, "Quarter 1", default);
            Assert.That(Path.GetFileName(path), Is.EqualTo("______Quarter_ 1.pdf"));
            if (!OperatingSystem.IsWindows()) Assert.That(File.GetUnixFileMode(outgoing.DirectoryPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            File.SetLastWriteTimeUtc(Path.Combine(outgoing.DirectoryPath, ".lease"), DateTime.UtcNow.AddDays(-8));
            OutgoingDocument.Prune(root, DateTime.UtcNow.AddDays(-7));
            Assert.That(File.Exists(path), Is.True, "Active leases must be retained.");
        }
        Assert.That(File.Exists(path), Is.True);
        Assert.That(fake.Calls.Single(), Is.EqualTo(("xdg-email", new[] { "--utf8", "--subject", "Quarter 1", "--attach", path })));
        OutgoingDocument.Prune(root, DateTime.UtcNow.AddDays(-7));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void PruningIgnoresUnownedFoldersAndSymlinks()
    {
        var unrelated = Path.Combine(Root, "keep");
        Directory.CreateDirectory(unrelated);
        var marker = Path.Combine(unrelated, ".lease");
        File.WriteAllText(marker, "keep");
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-8));
        var alias = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(alias, unrelated);
        OutgoingDocument.Prune(Root, DateTime.UtcNow.AddDays(-7));
        Assert.That(File.Exists(marker), Is.True);
        Directory.Delete(alias);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EmptyOrDuplexSessionCannotReachDesktop(bool duplex)
    {
        using var session = CreateSession();
        if (duplex) { AddPages(session, 1); session.BeginManualDuplex(); }
        var fake = new FakeDesktopCommand();
        var delivery = new DocumentDelivery((_, _, _, _) => throw new AssertionException("Must guard before preparation"), new(fake), new(fake), Root);
        Assert.ThrowsAsync<InvalidOperationException>(() => delivery.Print(session, new("Office"), default));
        Assert.ThrowsAsync<InvalidOperationException>(() => delivery.Email(session, null, default));
        Assert.That(fake.Calls, Is.Empty);
    }

    [Test]
    public void CancelledPreparationCleansPrivateOutputWithoutLaunchingAnything()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var fake = new FakeDesktopCommand();
        var root = Path.Combine(Root, "outgoing");
        var delivery = new DocumentDelivery((_, path, _, _) => { File.WriteAllText(path, "partial"); throw new OperationCanceledException(); }, new(fake), new(fake), root);
        Assert.ThrowsAsync<OperationCanceledException>(() => delivery.Print(session, new("Office"), default));
        Assert.ThrowsAsync<OperationCanceledException>(() => delivery.Email(session, null, default));
        Assert.That(Directory.GetDirectories(root), Is.Empty);
        Assert.That(fake.Calls, Is.Empty);
    }

    [Test]
    public async Task EmailForwardsSearchablePdfOptionsWhilePrintUsesOrdinaryPdf()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var options = new PdfOcrOptions("nld");
        var received = new List<PdfOcrOptions?>();
        var fake = new FakeDesktopCommand();
        var delivery = new DocumentDelivery((_, path, _, ocr) =>
        {
            received.Add(ocr);
            return File.WriteAllTextAsync(path, "fixture");
        }, new(fake), new(fake), Path.Combine(Root, "outgoing"));
        await delivery.Email(session, options, default);
        await delivery.Print(session, new("Office"), default);
        Assert.That(received, Is.EqualTo(new PdfOcrOptions?[] { options, null }));
    }

    [Test]
    public void FailedMailLauncherRetainsAttachmentAndReportsFailure()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var fake = new FakeDesktopCommand { Respond = (_, _) => new(4, "", "No mail client") };
        var delivery = new DocumentDelivery((_, path, _, _) => File.WriteAllTextAsync(path, "fixture"), new(fake), new(fake), Path.Combine(Root, "outgoing"));
        Assert.That(Assert.ThrowsAsync<IOException>(() => delivery.Email(session, null, default))!.Message, Does.Contain("No mail client"));
        Assert.That(File.Exists(fake.Calls.Single().Arguments.Last()), Is.True, "A client may open a draft before the launcher reports an error.");
    }

    [TestCase(false)]
    [TestCase(true)]
    [Category("Integration")]
    public async Task PrintAndEmailUseFinalMixedDocumentPreserveSourcesAndKeepSessionLock(bool email)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.SetDeskew(0, -4);
        session.SetCrop(0, new(2, 3, 38, 60));
        session.Rotate(0);
        var input = Path.Combine(Root, "input.pdf");
        PdfImportTests.WritePdfFile(input, "TEXT");
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, default);
        session.Rotate(1);
        session.Move(1, 0);
        var hashes = Directory.GetFiles(Path.Combine(session.DirectoryPath, "originals")).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var savedPages = session.Pages.ToArray();
        var captured = Path.Combine(Root, "captured.pdf");
        var fake = new FakeDesktopCommand
        {
            Respond = (command, args) =>
            {
                if (command == "xdg-email" || args.Contains("hold"))
                {
                    Assert.That(Path.GetFileName(args.Last()), Is.EqualTo("Example documents.pdf"));
                    File.Copy(args.Last(), captured);
                }
                return new(0, "request id is Office-42", "");
            }
        };
        var outgoingRoot = Path.Combine(Root, "outgoing");
        var delivery = new DocumentDelivery((s, path, token, ocr) => scanner.Export(s, path, ExportFormat.CombinedPdf, token, ocr), new(fake), new(fake), outgoingRoot);
        if (email) await delivery.Email(session, null, default);
        else Assert.That(await delivery.Print(session, new("Office"), default), Is.EqualTo("Office-42"));
        using (var pdf = PdfReader.Open(captured, PdfDocumentOpenMode.Import))
        {
            Assert.That(pdf.PageCount, Is.EqualTo(2));
            var operators = ContentReader.ReadContent(pdf.Pages[0]).OfType<COperator>().ToArray();
            Assert.That(string.Concat(operators.Where(o => o.OpCode.Name == "Tj").SelectMany(o => o.Operands.OfType<CString>()).Select(s => s.Value)), Is.EqualTo("TEXT"));
            Assert.That(operators.Any(o => o.OpCode.Name == "re"), Is.True);
            Assert.That(pdf.Pages[0].Rotate, Is.EqualTo(90));
        }
        using var expectedPng = PageRenderer.Png(session, session.Pages[1]);
        using var expected = SKBitmap.Decode(expectedPng);
        using var context = new ScanningContext(new GtkImageContext());
        var count = 0;
        await foreach (var image in new PdfImporter(context).Import(captured))
        {
            using (image)
            {
                if (count++ != 1) continue;
                using var png = image.SaveToMemoryStream(ImageFileFormat.Png);
                png.Position = 0;
                using var actual = SKBitmap.Decode(png);
                Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
                Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
            }
        }
        Assert.That(count, Is.EqualTo(2));
        Assert.That(session.Pages, Is.EqualTo(savedPages));
        foreach (var (path, hash) in hashes) Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(hash));
        Assert.Throws<IOException>(() => Session.Open(session.DirectoryPath).Dispose());
        Assert.That(Directory.GetDirectories(outgoingRoot).Length, Is.EqualTo(email ? 1 : 0));
    }

    [Test]
    public async Task DesktopCommandPassesArgumentsLiterallyAndReportsExitCode()
    {
        var result = await new DesktopCommand().Run("/bin/sh", ["-c", "printf '%s' \"$1\"; printf '%s' failure >&2; exit 7", "fixture", "a; $(not executed)"], default);
        Assert.That(result, Is.EqualTo(new CommandResult(7, "a; $(not executed)", "failure")));
    }

    [Test]
    public void MissingDesktopExecutableHasAnActionableError()
    {
        Assert.That(Assert.ThrowsAsync<IOException>(() => new DesktopCommand().Run("esyuscan-does-not-exist", [], default))!.Message, Does.Contain("Install/configure"));
    }
}
