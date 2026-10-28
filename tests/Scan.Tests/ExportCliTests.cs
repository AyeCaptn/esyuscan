using System.Diagnostics;
using System.Text.Json;
using NAPS2.Images.Gtk;
using NUnit.Framework;
using PdfSharpCore.Pdf.IO;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class ExportCliTests : TestFiles
{
    [TestCase("images", "jpeg")]
    [TestCase("images", "tiff")]
    [TestCase("tiff", null)]
    public async Task CliExportsActualRequestedImageFormats(string format, string? imageFormat)
    {
        string folder;
        using (var session = CreateSession()) { AddPages(session, 2); folder = session.DirectoryPath; }
        var target = Path.Combine(Root, "output");
        var args = new List<string> { "export", folder, "--format", format, "--output", target, "--json" };
        if (imageFormat is not null) args.AddRange(["--image-format", imageFormat]);
        if (imageFormat == "jpeg") args.AddRange(["--quality", "75"]);
        else args.AddRange(["--tiff-compression", "lzw"]);
        var result = await Cli([], args.ToArray());
        Assert.That(result.ExitCode, Is.Zero, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.True);
        if (format == "tiff")
        {
            var count = 0;
            await foreach (var frame in new GtkImageContext().LoadFrames(target)) { frame.Dispose(); count++; }
            Assert.That(count, Is.EqualTo(2));
        }
        else
        {
            var files = Directory.GetFiles(target);
            Assert.That(files, Has.Length.EqualTo(2));
            Assert.That(files.Select(Path.GetExtension), Is.All.EqualTo(imageFormat == "jpeg" ? ".jpg" : ".tiff"));
        }
    }

    [Test]
    public async Task CliEncryptsUsingEnvironmentPasswordsWithoutLoggingThem()
    {
        string folder;
        using (var session = CreateSession()) { AddPages(session, 1); folder = session.DirectoryPath; }
        var target = Path.Combine(Root, "protected.pdf");
        var owner = Guid.NewGuid().ToString("N");
        var user = Guid.NewGuid().ToString("N");
        var result = await Cli(new() { ["TEST_ESYUSCAN_OWNER"] = owner, ["TEST_ESYUSCAN_OPEN"] = user }, "export", folder, "--format", "pdf", "--output", target,
            "--pdf-owner-password-env", "TEST_ESYUSCAN_OWNER", "--pdf-open-password-env", "TEST_ESYUSCAN_OPEN", "--deny", "copy,print", "--title", "Invoice", "--json");
        Assert.That(result.ExitCode, Is.Zero, result.Error);
        Assert.That(result.Output + result.Error, Does.Not.Contain(owner).And.Not.Contain(user));
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(target, PdfDocumentOpenMode.Import));
        using var document = PdfReader.Open(target, user, PdfDocumentOpenMode.Import);
        Assert.That(document.Info.Title, Is.EqualTo("Invoice"));
        Assert.That(document.SecuritySettings.PermitPrint, Is.False);
        Assert.That(document.SecuritySettings.PermitExtractContent, Is.False);
        Assert.That(File.ReadAllText(Path.Combine(folder, "session.json")), Does.Not.Contain(owner).And.Not.Contain(user));
    }

    [TestCase("--pdfa", "2b")]
    [TestCase("--pdf-compression", "lossless")]
    public async Task CliConflictsReturnFailureJsonAndDoNotPublishOrExposePasswords(string flag, string value)
    {
        string folder;
        using (var session = CreateSession()) { AddPages(session, 1); folder = session.DirectoryPath; }
        var target = Path.Combine(Root, "invalid.pdf");
        var secret = Guid.NewGuid().ToString("N");
        var args = new List<string> { "export", folder, "--format", "pdf", "--output", target,
            "--pdf-owner-password-env", "TEST_ESYUSCAN_OWNER", flag, value, "--quality", "85", "--json" };
        if (flag == "--pdfa") args.AddRange(["--pdf-compression", "jpeg"]);
        var result = await Cli(new() { ["TEST_ESYUSCAN_OWNER"] = secret }, args.ToArray());
        Assert.That(result.ExitCode, Is.EqualTo(1));
        using var json = JsonDocument.Parse(result.Output);
        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(result.Output + result.Error, Does.Contain(flag == "--pdfa" ? "PDF/A cannot be encrypted" : "JPEG quality requires").And.Not.Contain(secret));
        Assert.That(Path.Exists(target), Is.False);
    }

    private static async Task<(int ExitCode, string Output, string Error)> Cli(Dictionary<string, string> environment, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await errors);
    }
}
