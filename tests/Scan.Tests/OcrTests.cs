using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture]
public sealed class OcrDetectionTests : TestFiles
{
    [Test]
    public async Task DetectionKeepsCustomLanguagesButExcludesOrientationAndEquationModules()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("The probe fixture requires a POSIX shell."); return; }
        var executable = Path.Combine(Root, "fixture-tesseract");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\ncase \"$1\" in\n--version) echo 'tesseract 5.5.0';;\n--list-langs) printf 'List of available languages (5):\\neng\\nara\\nprivate_model\\nosd\\nequ\\n';;\nesac\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var available = await new OcrRuntime(executable).Detect();

        Assert.That(available.Languages, Is.EqualTo(new[] { "ara", "eng", "private_model" }));
    }

    [Test]
    public async Task MissingEngineReportsActionableSetupAndNoLanguages()
    {
        var available = await new OcrRuntime(Path.Combine(Root, "missing-tesseract")).Detect();
        Assert.Multiple(() =>
        {
            Assert.That(available.EngineAvailable, Is.False);
            Assert.That(available.Languages, Is.Empty);
            Assert.That(available.Message, Does.Contain("ESYUSCAN_TESSERACT").And.Contain("tesseract-data-eng"));
        });
    }
}

[TestFixture, Category("Integration")]
public sealed class OcrExportTests : TestFiles
{
    private async Task<OcrRuntime> InstalledEnglish()
    {
        var runtime = new OcrRuntime();
        var available = await runtime.Detect();
        if (!available.EngineAvailable || !available.Languages.Contains("eng"))
            Assert.Ignore("Real OCR integration requires user-provisioned Tesseract 4/5 and English data. " + available.Message);
        return runtime;
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task SearchableExportPreservesNativeTextAndVectorsAtEveryQuarterTurn(int rotation)
    {
        var runtime = await InstalledEnglish();
        using var scanner = new Scanner(Root, runtime);
        var input = Path.Combine(Root, "native-rotation.pdf");
        PdfImportTests.WritePdfFile(input, "PRESERVED");
        using var session = CreateSession();
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        for (var angle = 0; angle < rotation; angle += 90) session.Rotate(0);
        var target = Path.Combine(Root, "native-searchable.pdf");

        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None, new("eng"));

        Assert.That(await ExtractText(target), Does.Contain("PRESERVED"));
        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var page = document.Pages[0];
        Assert.That(page.Width.Point, Is.EqualTo(rotation is 90 or 270 ? 180 : 120).Within(0.01));
        Assert.That(page.Height.Point, Is.EqualTo(rotation is 90 or 270 ? 120 : 180).Within(0.01));
        Assert.That(page.Rotate, Is.Zero);
        var operators = PdfSharpCore.Pdf.Content.ContentReader.ReadContent(page).OfType<PdfSharpCore.Pdf.Content.Objects.COperator>();
        Assert.That(operators.Any(op => op.OpCode.Name == "re"), Is.True);
        Assert.That(operators.Any(op => op.OpCode.Name == "Do"), Is.False);
    }

    [TestCase(ExportFormat.CombinedPdf)]
    [TestCase(ExportFormat.SeparatePdfs)]
    public async Task MixedOcrPreservesExistingPdfTextAndRecognizesRotatedPdfAndCroppedScan(ExportFormat format)
    {
        var runtime = await InstalledEnglish();
        using var scanner = new Scanner(Root, runtime);
        var imagePdf = Path.Combine(Root, "image.pdf");
        using (var input = Session.Create(Path.Combine(Root, "input-session"), "Sideways input"))
        {
            AddLegiblePage(input, sideways: true);
            await scanner.Export(input, imagePdf, ExportFormat.CombinedPdf, CancellationToken.None);
        }
        // Keep the image page on upstream PDF passthrough rather than its single-image extraction path.
        using (var document = PdfReader.Open(imagePdf, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes("0 0 0 rg 1 1 2 2 re f\n"));
            document.Save(imagePdf);
        }
        var nativePdf = Path.Combine(Root, "native.pdf");
        PdfImportTests.WritePdfFile(nativePdf, "PRESERVED");
        using var session = CreateSession();
        await scanner.Import(session, [nativePdf, imagePdf], () => { }, CancellationToken.None);
        session.Rotate(1);
        AddLegiblePage(session, sideways: false);
        session.SetCrop(2, new PageCrop(40, 80, 1150, 500));
        session.SetDeskew(2, 0.5);
        var target = Path.Combine(Root, format == ExportFormat.CombinedPdf ? "mixed.pdf" : "mixed");

        await scanner.Export(session, target, format, CancellationToken.None, new("eng"));

        var files = format == ExportFormat.CombinedPdf ? new[] { target } : Directory.GetFiles(target).Order().ToArray();
        var text = new List<string>();
        foreach (var file in files) text.AddRange((await ExtractText(file)).Split('\f', StringSplitOptions.RemoveEmptyEntries).Where(value => !string.IsNullOrWhiteSpace(value)));
        Assert.That(text, Has.Count.EqualTo(3), string.Join(" | ", text));
        Assert.That(text[0], Does.Contain("PRESERVED"));
        Assert.That(text[1], Does.Contain("ESYUSCAN SEARCHABLE DOCUMENT").And.Contain("48273"));
        Assert.That(text[2], Does.Contain("ESYUSCAN SEARCHABLE DOCUMENT").And.Contain("48273"));
        using var exported = PdfReader.Open(files[0], PdfDocumentOpenMode.Import);
        var operators = PdfSharpCore.Pdf.Content.ContentReader.ReadContent(exported.Pages[0]).OfType<PdfSharpCore.Pdf.Content.Objects.COperator>();
        Assert.That(operators.Any(op => op.OpCode.Name == "re"), Is.True, "The original vector path must survive OCR export.");
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [TestCase(ExportFormat.CombinedPdf)]
    [TestCase(ExportFormat.SeparatePdfs)]
    public async Task SearchablePdfsContainRecognizedTextFromFinalOrientation(ExportFormat format)
    {
        var runtime = await InstalledEnglish();
        using var session = CreateSession();
        // Store a sideways original, then rotate it upright using the existing session edit.
        AddLegiblePage(session, sideways: true);
        session.Rotate(0);
        AddLegiblePage(session, sideways: false);
        var original = session.OriginalPath(session.Pages[0]);
        var hash = SHA256.HashData(File.ReadAllBytes(original));
        var manifest = File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json"));
        using var scanner = new Scanner(Root, runtime);
        var target = Path.Combine(Root, format == ExportFormat.CombinedPdf ? "searchable.pdf" : "pdfs");
        var progress = new List<ExportProgress>();

        await scanner.Export(session, target, format, CancellationToken.None, new("eng"),
            value => { lock (progress) progress.Add(value); });

        var files = format == ExportFormat.CombinedPdf ? [target] : Directory.GetFiles(target).Order().ToArray();
        var textPages = new List<string>();
        foreach (var file in files) textPages.AddRange((await ExtractText(file)).Split('\f', StringSplitOptions.RemoveEmptyEntries).Where(text => !string.IsNullOrWhiteSpace(text)));
        Assert.That(textPages, Has.Count.EqualTo(2));
        foreach (var text in textPages)
            Assert.That(text, Does.Contain("ESYUSCAN SEARCHABLE DOCUMENT").And.Contain("48273").And.Contain("The quick brown fox"));
        using var document = PdfReader.Open(files[0], PdfDocumentOpenMode.Import);
        Assert.Multiple(() =>
        {
            Assert.That(document.Pages[0].Height.Point, Is.GreaterThan(document.Pages[0].Width.Point));
            Assert.That(SHA256.HashData(File.ReadAllBytes(original)), Is.EqualTo(hash));
            Assert.That(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(manifest));
            Assert.That(session.CanUndo, Is.True);
            Assert.That(progress.Any(value => value.Message.Contains("Recognizing")), Is.True);
            Assert.That(progress.Last().Fraction, Is.EqualTo(1));
            Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
        });
    }

    [TestCase(ExportFormat.Images)]
    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    public async Task OrdinaryExportsWorkWithoutOcrEngine(ExportFormat format)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        using var scanner = new Scanner(Root, new OcrRuntime(Path.Combine(Root, "absent")));
        var target = Path.Combine(Root, "ordinary");
        await scanner.Export(session, target, format, CancellationToken.None);
        Assert.That(Path.Exists(target), Is.True);
    }

    [TestCase("missing-engine")]
    [TestCase("missing-language")]
    [TestCase("failed-engine")]
    [TestCase("invalid-language")]
    public void UnavailableOcrNeverPublishesAnImageOnlyPdf(string scenario)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var executable = FakeEngine("exit 1\n");
        var runtime = new OcrRuntime(scenario == "missing-engine" ? Path.Combine(Root, "absent") : executable);
        using var scanner = new Scanner(Root, runtime);
        var target = Path.Combine(Root, "searchable.pdf");
        var language = scenario switch { "missing-language" => "nld", "invalid-language" => "eng --bad", _ => "eng" };

        var error = Assert.CatchAsync(() => scanner.Export(session, target, ExportFormat.CombinedPdf,
            CancellationToken.None, new(language)));

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Message, Does.Contain(scenario == "invalid-language" ? "language code" : "Tesseract").IgnoreCase);
            Assert.That(File.Exists(target), Is.False);
            Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
        });
    }

    [Test]
    public async Task EmptyLanguageDirectoryIsDetected()
    {
        await InstalledEnglish();
        var empty = Path.Combine(Root, "empty-tessdata");
        Directory.CreateDirectory(empty);
        // Use the same installation but deliberately override its language search directory.
        var available = await new OcrRuntime(dataDirectory: empty).Detect();
        Assert.That(available.EngineAvailable, Is.True);
        Assert.That(available.Languages, Is.Empty);
        Assert.That(available.Message, Does.Contain("No OCR languages"));
    }

    [Test]
    public async Task CorruptTrainedDataFailsRatherThanPublishingImageOnlyPdf()
    {
        await InstalledEnglish();
        using var session = CreateSession();
        AddLegiblePage(session, sideways: false);
        var data = Path.Combine(Root, "bad-tessdata");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "eng.traineddata"), "Not trained data");
        using var scanner = new Scanner(Root, new OcrRuntime(dataDirectory: data));
        var target = Path.Combine(Root, "broken.pdf");
        Assert.ThrowsAsync<InvalidOperationException>(() => scanner.Export(session, target, ExportFormat.CombinedPdf,
            CancellationToken.None, new("eng")));
        Assert.That(File.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [TestCase(ExportFormat.CombinedPdf)]
    [TestCase(ExportFormat.SeparatePdfs)]
    public async Task CancellingDuringOcrStopsTheEngineAndRemovesStagedOutput(ExportFormat format)
    {
        using var session = CreateSession();
        AddLegiblePage(session, sideways: false);
        var marker = Path.Combine(Root, "engine-started");
        var executable = FakeEngine($"printf '%s' \"$$\" > '{marker}'\nexec sleep 30\n");
        using var scanner = new Scanner(Root, new OcrRuntime(executable));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var target = Path.Combine(Root, "cancelled");
        var export = scanner.Export(session, target, format, cancellation.Token, new("eng"));
        while (!File.Exists(marker) && !export.IsCompleted && !cancellation.IsCancellationRequested)
            await Task.Delay(20);
        Assert.That(File.Exists(marker), Is.True, "Test must cancel while the OCR process is running.");
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await export.WaitAsync(TimeSpan.FromSeconds(5)));
        var pid = int.Parse(File.ReadAllText(marker));
        Assert.That(Directory.Exists($"/proc/{pid}"), Is.False, "The cancelled OCR process must have exited.");
        Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [TestCase(ExportFormat.CombinedPdf)]
    [TestCase(ExportFormat.SeparatePdfs)]
    public async Task CancellationAtPublicationBoundaryDiscardsCompletedSearchablePdfs(ExportFormat format)
    {
        var runtime = await InstalledEnglish();
        using var session = CreateSession();
        AddLegiblePage(session, sideways: false);
        using var scanner = new Scanner(Root, runtime);
        using var cancellation = new CancellationTokenSource();
        var target = Path.Combine(Root, "cancelled");

        Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Export(session, target, format, cancellation.Token,
            new("eng"), value => { if (value.Message.StartsWith("Publishing")) cancellation.Cancel(); }));
        Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [Test]
    public async Task DestinationCreatedDuringOcrIsNeverOverwritten()
    {
        var runtime = await InstalledEnglish();
        using var session = CreateSession();
        AddLegiblePage(session, sideways: false);
        using var scanner = new Scanner(Root, runtime);
        var target = Path.Combine(Root, "existing.pdf");
        Assert.ThrowsAsync<IOException>(() => scanner.Export(session, target, ExportFormat.CombinedPdf,
            CancellationToken.None, new("eng"), value =>
            {
                if (value.Message.StartsWith("Publishing")) File.WriteAllText(target, "Keep this file");
            }));
        Assert.That(File.ReadAllText(target), Is.EqualTo("Keep this file"));
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [Test]
    public void ExplicitOcrForImagesIsRejected()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        using var scanner = new Scanner(Root);
        Assert.ThrowsAsync<ArgumentException>(() => scanner.Export(session, Path.Combine(Root, "images"), ExportFormat.Images,
            CancellationToken.None, new("eng")));
    }

    [TestCase("pdf")]
    [TestCase("pdfs")]
    public async Task CliSearchableExportProducesExtractableTextAndJson(string format)
    {
        await InstalledEnglish();
        string sessionPath;
        using (var session = CreateSession())
        {
            AddLegiblePage(session, sideways: false);
            sessionPath = session.DirectoryPath;
        }
        var target = Path.Combine(Root, "cli-export");
        var result = await Cli(null, "export", sessionPath, "--format", format, "--output", target,
            "--searchable", "--ocr-language", "eng", "--json");
        Assert.That(result.ExitCode, Is.Zero, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.That(json.RootElement.GetProperty("searchablePdf").GetBoolean(), Is.True);
        Assert.That(result.Error, Does.Contain("Recognizing"));
        var pdf = format == "pdf" ? target : Path.Combine(target, "page-001.pdf");
        Assert.That(await ExtractText(pdf), Does.Contain("ESYUSCAN SEARCHABLE DOCUMENT"));
    }

    [Test]
    public async Task CliMissingEngineReturnsFailureJsonAndPublishesNothing()
    {
        string sessionPath;
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            sessionPath = session.DirectoryPath;
        }
        var target = Path.Combine(Root, "cli-export.pdf");
        var result = await Cli(Path.Combine(Root, "absent"), "export", sessionPath, "--format", "pdf", "--output", target,
            "--searchable", "--json");
        Assert.That(result.ExitCode, Is.EqualTo(1));
        using var json = JsonDocument.Parse(result.Output);
        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(json.RootElement.GetProperty("error").GetString(), Does.Contain("OCR engine unavailable"));
        Assert.That(File.Exists(target), Is.False);
    }

    [TestCase(ExportFormat.CombinedPdf)]
    [TestCase(ExportFormat.SeparatePdfs)]
    public async Task SelectedSearchableExportsPreserveNativeTextAndRecognizeOnlyIncludedEditedPages(ExportFormat format)
    {
        var runtime = await InstalledEnglish();
        using var scanner = new Scanner(Root, runtime);
        var input = Path.Combine(Root, "native-selection.pdf");
        PdfImportTests.WritePdfFile(input, "PRESERVED", "EXCLUDED");
        using var session = CreateSession();
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        AddLegiblePage(session, sideways: false);
        session.SetCrop(2, new PageCrop(40, 80, 1150, 500));
        var selected = new[] { session.Pages[0].FileName, session.Pages[2].FileName };
        session.MovePages([selected[1]], 0);
        var target = Path.Combine(Root, "selected-ocr");
        await scanner.Export(session, target, format, CancellationToken.None, new("eng"), selectedPages: selected);
        var files = format == ExportFormat.CombinedPdf ? new[] { target } : Directory.GetFiles(target).Order().ToArray();
        var texts = new List<string>();
        foreach (var file in files) texts.AddRange((await ExtractText(file)).Split('\f', StringSplitOptions.RemoveEmptyEntries).Where(text => !string.IsNullOrWhiteSpace(text)));
        Assert.That(texts.Count, Is.EqualTo(2));
        Assert.That(texts[0], Does.Contain("ESYUSCAN SEARCHABLE DOCUMENT"));
        Assert.That(texts[1], Does.Contain("PRESERVED").And.Not.Contain("EXCLUDED"));
    }

    [TestCase(ExportFormat.CombinedPdf)]
    [TestCase(ExportFormat.SeparatePdfs)]
    public async Task OcrReceivesIdenticalFinalPixelsIncludingAllAdjustments(ExportFormat format)
    {
        await InstalledEnglish();
        using var session = CreateSession();
        AddLegiblePage(session, sideways: true);
        session.Rotate(0);
        session.SetDeskew(0, 0.2);
        session.SetCrop(0, new(80, 30, 600, 1150));
        // Two flips + 180 degrees leave text upright while exercising every transform.
        session.SetAdjustments(0, new(30, 100, 100, 180, true, true, PageCorrection.Document));
        using var expectedPng = PageRenderer.Png(session, session.Pages[0]);
        using var expected = SKBitmap.Decode(expectedPng);
        var captured = Path.Combine(Root, "ocr-input");
        var executable = FakeEngine($"cp \"$1\" '{captured}'\nexec tesseract \"$@\"\n");
        using var scanner = new Scanner(Root, new OcrRuntime(executable));
        var destination = Path.Combine(Root, "adjusted-export");
        await scanner.Export(session, destination, format, CancellationToken.None, new("eng"));
        using var actual = SKBitmap.Decode(captured);
        Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
        Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels), "The SDK OCR input must contain exactly the preview/export pixels.");
        var pdf = format == ExportFormat.CombinedPdf ? destination : Path.Combine(destination, "page-001.pdf");
        Assert.That(await ExtractText(pdf), Does.Contain("ESYUSCAN SEARCHABLE DOCUMENT").And.Contain("48273"));
    }

    private static async Task<(int ExitCode, string Output, string Error)> Cli(string? executable, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (executable is not null) start.Environment["ESYUSCAN_TESSERACT"] = executable;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await errors);
    }

    private static async Task<string> ExtractText(string path)
    {
        var start = new ProcessStartInfo("pdftotext") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-");
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { Assert.Ignore("Text extraction integration requires Poppler's pdftotext on PATH."); throw; }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.That(process.ExitCode, Is.Zero, await errors);
            return await output;
        }
    }

    private string FakeEngine(string body)
    {
        var path = Path.Combine(Root, "fake-tesseract");
        File.WriteAllText(path, "#!/bin/sh\ncase \"$1\" in\n--version) echo 'tesseract 5.5.0'; exit 0;;\n"
            + "--list-langs) printf 'List of available languages (1):\\neng\\n'; exit 0;;\nesac\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void AddLegiblePage(Session session, bool sideways)
    {
        using var upright = new SKBitmap(1240, 1754);
        using (var canvas = new SKCanvas(upright))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var font = new SKFont(SKTypeface.FromFamilyName("DejaVu Sans"), 46);
            canvas.DrawText("ESYUSCAN SEARCHABLE DOCUMENT", 70, 180, SKTextAlign.Left, font, paint);
            canvas.DrawText("Invoice number 48273", 70, 290, SKTextAlign.Left, font, paint);
            canvas.DrawText("The quick brown fox jumps over the lazy dog.", 70, 400, SKTextAlign.Left, font, paint);
        }
        using var image = sideways ? new SKBitmap(upright.Height, upright.Width) : upright.Copy();
        if (sideways)
        {
            using var canvas = new SKCanvas(image);
            canvas.Translate(0, upright.Width);
            canvas.RotateDegrees(-90);
            canvas.DrawBitmap(upright, 0, 0);
        }
        var path = session.NextOriginalPath();
        using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(path)) png.SaveTo(stream);
        session.Add(path);
    }
}
