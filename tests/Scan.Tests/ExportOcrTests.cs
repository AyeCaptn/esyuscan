using System.Diagnostics;
using System.Text;
using NAPS2.Pdf;
using NUnit.Framework;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class ExportOcrTests : TestFiles
{
    [TestCase("jpeg", ExportFormat.CombinedPdf)]
    [TestCase("jpeg", ExportFormat.SeparatePdfs)]
    [TestCase("pdfa", ExportFormat.CombinedPdf)]
    [TestCase("pdfa", ExportFormat.SeparatePdfs)]
    [TestCase("encrypted", ExportFormat.CombinedPdf)]
    [TestCase("encrypted", ExportFormat.SeparatePdfs)]
    public async Task OptionsWorkWithOcrMixedPdfSourcesAndRasterEdits(string mode, ExportFormat format)
    {
        var runtime = new OcrRuntime();
        var available = await runtime.Detect();
        if (!available.EngineAvailable || !available.Languages.Contains("eng"))
            Assert.Ignore("Requires provisioned Tesseract and English data: " + available.Message);
        using var session = CreateSession();
        using var scanner = new Scanner(Root, runtime);
        var input = Path.Combine(Root, "native.pdf");
        PdfImportTests.WritePdfFile(input, "PRESERVED");
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        session.Rotate(0);
        using (var image = new SKBitmap(1240, 1754))
        {
            using var canvas = new SKCanvas(image);
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var font = new SKFont(SKTypeface.FromFamilyName("DejaVu Sans"), 48);
            canvas.DrawText("ESYUSCAN EXPORT OPTIONS", 70, 180, SKTextAlign.Left, font, paint);
            canvas.DrawText("Invoice 48273", 70, 280, SKTextAlign.Left, font, paint);
            var path = session.NextOriginalPath();
            using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Create(path)) png.SaveTo(stream);
            session.Add(path);
        }
        session.SetCrop(1, new(40, 80, 1150, 400));
        session.SetDeskew(1, 0.5);
        var owner = Guid.NewGuid().ToString("N");
        var options = new ExportOptions
        {
            Compatibility = mode == "pdfa" ? PdfCompat.PdfA3U : PdfCompat.Default,
            RasterizePdfSources = mode == "pdfa",
            PdfCompression = PdfImageCompression.Jpeg,
            JpegQuality = 90,
            Passwords = mode == "encrypted" ? new() { Owner = owner } : null,
            Permissions = new() { Copying = mode != "encrypted" },
            Metadata = new() { Title = "Searchable invoice", Creator = "esyuscan" }
        };
        var target = Path.Combine(Root, "searchable");
        await scanner.Export(session, target, format, CancellationToken.None, new("eng"), options: options);
        var files = format == ExportFormat.CombinedPdf ? [target] : Directory.GetFiles(target).Order().ToArray();
        var texts = new List<string>();
        foreach (var file in files)
        {
            // Decrypt through the bundled upstream reader in-memory, so credentials never enter process arguments.
            var readable = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".pdf");
            using var document = PdfReader.Open(file, mode == "encrypted" ? owner : null, PdfDocumentOpenMode.Modify);
            Assert.That(document.Info.Title, Is.EqualTo("Searchable invoice"));
            if (mode == "encrypted")
            {
                Assert.That(document.SecurityHandler.ValidatePassword(owner), Is.EqualTo(PasswordValidity.OwnerPassword));
                document.SecuritySettings.DocumentSecurityLevel = PdfSharpCore.Pdf.Security.PdfDocumentSecurityLevel.None;
            }
            document.Save(readable);
            texts.Add(await ExtractText(readable));
        }
        var combined = string.Join("\n", texts);
        Assert.That(combined, Does.Contain("ESYUSCAN EXPORT OPTIONS").And.Contain("48273"));
        if (mode != "pdfa")
        {
            Assert.That(combined, Does.Contain("PRESERVED"));
            using var document = PdfReader.Open(files[0], mode == "encrypted" ? owner : null, PdfDocumentOpenMode.Import);
            var operators = PdfSharpCore.Pdf.Content.ContentReader.ReadContent(document.Pages[0]).OfType<PdfSharpCore.Pdf.Content.Objects.COperator>();
            Assert.That(operators.Any(op => op.OpCode.Name == "re"), Is.True);
        }
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    private static async Task<string> ExtractText(string path)
    {
        var start = new ProcessStartInfo("pdftotext") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-");
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { Assert.Ignore("Requires Poppler pdftotext."); throw; }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(process.ExitCode, Is.Zero, await errors);
            return await output;
        }
    }
}
