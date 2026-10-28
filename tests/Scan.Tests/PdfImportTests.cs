using System.Security.Cryptography;
using System.Text;
using NAPS2.Images;
using NUnit.Framework;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Content;
using PdfSharpCore.Pdf.Content.Objects;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class PdfImportTests : TestFiles
{
    [TestCase(ExportFormat.Images)]
    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    public async Task RasterEditsToPdfSurviveReopenAndMatchPreviewWithoutDoubleRotation(ExportFormat format)
    {
        var input = WritePdf("edited-source.pdf", "ORIGINAL", "UNCHANGED");
        string sourcePath;
        byte[] sourceHash;
        using (var session = CreateSession())
        {
            using var scanner = new Scanner(Root);
            await scanner.Import(session, [input], () => { }, CancellationToken.None);
            sourcePath = session.PdfSourcePath(session.Pages[0].PdfSource!);
            sourceHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
            session.SetDeskew(0, -2);
            session.SetCrop(0, new PageCrop(10, 15, 150, 210));
            session.Rotate(0);
            Assert.That(session.Pages[0].PreservePdf, Is.False);
            session.Undo(); session.Undo(); session.Undo();
            Assert.That(session.Pages[0].PreservePdf, Is.True);
            session.Redo(); session.Redo(); session.Redo();
            session.BeginManualDuplex();
        }
        using var reopened = Session.Open(Path.Combine(Root, "session"));
        Assert.That(reopened.ManualDuplex, Is.Not.Null);
        reopened.KeepDuplexScanOrder();
        Assert.That(reopened.Pages[0].PdfSource, Is.Not.Null);
        using var expectedPng = PageRenderer.Png(reopened, reopened.Pages[0]);
        using var expected = SKBitmap.Decode(expectedPng);
        Assert.That((expected.Width, expected.Height), Is.EqualTo((210, 150)));
        using var exporter = new Scanner(Root);
        var target = Path.Combine(Root, format == ExportFormat.CombinedPdf ? "edited.pdf" : "edited");
        await exporter.Export(reopened, target, format, CancellationToken.None);
        if (format == ExportFormat.Images)
        {
            using var actual = SKBitmap.Decode(Path.Combine(target, "page-001.png"));
            Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
        }
        else
        {
            var file = format == ExportFormat.CombinedPdf ? target : Path.Combine(target, "page-001.pdf");
            using var document = PdfReader.Open(file, PdfDocumentOpenMode.Import);
            Assert.That(document.Pages[0].Rotate, Is.Zero);
            if (format == ExportFormat.CombinedPdf) AssertPdfContent(document.Pages[1], "UNCHANGED");
            using var context = new NAPS2.Scan.ScanningContext(new NAPS2.Images.Gtk.GtkImageContext());
            await foreach (var image in new NAPS2.Pdf.PdfImporter(context).Import(file,
                new NAPS2.ImportExport.ImportParams { Slice = NAPS2.ImportExport.Slice.Item(0) }))
            {
                using (image)
                using (var png = image.SaveToMemoryStream(NAPS2.Images.ImageFileFormat.Png))
                {
                    png.Position = 0;
                    using var actual = SKBitmap.Decode(png);
                    Assert.That((actual.Width, actual.Height), Is.EqualTo((210, 150)));
                    Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
                }
            }
        }
        Assert.That(SHA256.HashData(File.ReadAllBytes(sourcePath)), Is.EqualTo(sourceHash));
    }

    private static IEnumerable<PageAdjustments> RasterAdjustments()
    {
        yield return new(Brightness: 100);
        yield return new(Contrast: 200);
        yield return new(Sharpness: 500);
        yield return new(Angle: 12.3);
        yield return new(FlipHorizontal: true);
        yield return new(FlipVertical: true);
        yield return new(Correction: PageCorrection.Document);
    }

    [TestCaseSource(nameof(RasterAdjustments))]
    public async Task AdjustmentAloneRasterizesPdfAndUndoRestoresNativeObjects(PageAdjustments adjustments)
    {
        var input = WritePdf("adjusted-source.pdf", "RECOVERABLE");
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        var original = session.Pages[0];
        session.SetAdjustments(0, adjustments);
        Assert.That(session.Pages[0].PreservePdf, Is.False);
        var target = Path.Combine(Root, "adjusted.pdf");
        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None);
        using (var document = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            Assert.That(Text(document.Pages[0]), Is.Empty);
            Assert.That(Operators(document.Pages[0]).Any(op => op.OpCode.Name == "Do"), Is.True);
        }
        using var expectedPng = PageRenderer.Png(session, session.Pages[0]);
        using var expected = SKBitmap.Decode(expectedPng);
        using var context = new NAPS2.Scan.ScanningContext(new NAPS2.Images.Gtk.GtkImageContext());
        await foreach (var image in new NAPS2.Pdf.PdfImporter(context).Import(target))
        {
            using (image)
            using (var png = image.SaveToMemoryStream(ImageFileFormat.Png))
            {
                png.Position = 0;
                using var actual = SKBitmap.Decode(png);
                Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
                Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
            }
        }
        session.Undo();
        Assert.That(session.Pages[0], Is.EqualTo(original));
        var recovered = Path.Combine(Root, "native.pdf");
        await scanner.Export(session, recovered, ExportFormat.CombinedPdf, CancellationToken.None);
        using var native = PdfReader.Open(recovered, PdfDocumentOpenMode.Import);
        AssertPdfContent(native.Pages[0], "RECOVERABLE");
    }

    [Test]
    public async Task MixedPagesSurviveEditsReopenAndDeletedInputsInAllExportModes()
    {
        var pdfPath = WritePdf("input.PDF", "ALPHA", "BETA", "GAMMA");
        var pdfBytes = SHA256.HashData(File.ReadAllBytes(pdfPath));
        var imagePath = Path.Combine(Root, "image.png");
        WriteImage(imagePath, 1);
        string ownedSource;
        using (var session = CreateSession())
        {
            using var scanner = new Scanner(Root);
            var added = 0;
            await scanner.Import(session, [imagePath, pdfPath, imagePath], () => added++, CancellationToken.None);
            Assert.That(added, Is.EqualTo(5));
            Assert.That(session.Pages.Select(page => page.PdfSource?.PageIndex), Is.EqualTo(new int?[] { null, 0, 1, 2, null }));
            ownedSource = session.PdfSourcePath(session.Pages[1].PdfSource!);
            Assert.That(SHA256.HashData(File.ReadAllBytes(ownedSource)), Is.EqualTo(pdfBytes));
            session.Rotate(2); // BETA
            session.Move(2, 0);
            session.Remove(3); // GAMMA, still recoverable from the source
            session.Undo();
            session.Redo();
            Assert.That(session.Pages.Count, Is.EqualTo(4));
        }
        File.Delete(pdfPath);
        File.Delete(imagePath);

        using var reopened = Session.Open(Path.Combine(Root, "session"));
        using var exporter = new Scanner(Root);
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var target = Path.Combine(Root, format == ExportFormat.CombinedPdf ? "combined.pdf" : format.ToString());
            await exporter.Export(reopened, target, format, CancellationToken.None);
            if (format == ExportFormat.Images)
            {
                var files = Directory.GetFiles(target).Order().ToArray();
                Assert.That(files.Length, Is.EqualTo(4));
                using var beta = SKBitmap.Decode(files[0]);
                Assert.That(beta.Width, Is.GreaterThan(beta.Height));
                using var alpha = SKBitmap.Decode(files[2]);
                Assert.That(alpha.Height, Is.GreaterThan(alpha.Width));
            }
            else if (format == ExportFormat.CombinedPdf)
            {
                using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
                Assert.That(document.PageCount, Is.EqualTo(4));
                AssertPdfContent(document.Pages[0], "BETA", rotation: 90);
                AssertPdfContent(document.Pages[2], "ALPHA");
                Assert.That(Text(document.Pages[1]), Is.Empty);
                Assert.That(Text(document.Pages[3]), Is.Empty);
            }
            else if (format == ExportFormat.CombinedTiff)
            {
                var frames = new List<NAPS2.Images.IMemoryImage>();
                try
                {
                    await foreach (var frame in new NAPS2.Images.Gtk.GtkImageContext().LoadFrames(target)) frames.Add(frame);
                    Assert.That(frames, Has.Count.EqualTo(4));
                    for (var i = 0; i < frames.Count; i++)
                    {
                        using var expected = PageRenderer.Render(reopened, reopened.Pages[i]);
                        Assert.That((frames[i].Width, frames[i].Height), Is.EqualTo((expected.Width, expected.Height)));
                        using var png = frames[i].SaveToMemoryStream(NAPS2.Images.ImageFileFormat.Png);
                        using var actual = SKBitmap.Decode(png.ToArray());
                        using var expectedPng = PageRenderer.Png(reopened, reopened.Pages[i]);
                        using var expectedBitmap = SKBitmap.Decode(expectedPng.ToArray());
                        Assert.That(actual.Pixels, Is.EqualTo(expectedBitmap.Pixels));
                    }
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
            else
            {
                var files = Directory.GetFiles(target).Order().ToArray();
                Assert.That(files.Length, Is.EqualTo(4));
                for (var i = 0; i < files.Length; i++)
                {
                    using var document = PdfReader.Open(files[i], PdfDocumentOpenMode.Import);
                    Assert.That(document.PageCount, Is.EqualTo(1));
                    if (i == 0) AssertPdfContent(document.Pages[0], "BETA", rotation: 90);
                    if (i == 2) AssertPdfContent(document.Pages[0], "ALPHA");
                }
            }
        }
        Assert.That(SHA256.HashData(File.ReadAllBytes(ownedSource)), Is.EqualTo(pdfBytes));
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task QuarterTurnsPreserveTextAndVectorsIncludingExistingPageRotation(int rotation)
    {
        var input = WritePdf("rotated.pdf", "ROTATED");
        using (var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Rotate = 90;
            document.Save(input);
        }
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        for (var angle = 0; angle < rotation; angle += 90) session.Rotate(0);
        var target = Path.Combine(Root, "rotated-output.pdf");

        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None);

        using var exported = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        AssertPdfContent(exported.Pages[0], "ROTATED", (rotation + 90) % 360);
        Assert.That(exported.Pages[0].MediaBox.Width, Is.EqualTo(120).Within(0.01));
        Assert.That(exported.Pages[0].MediaBox.Height, Is.EqualTo(180).Within(0.01));
    }

    [Test]
    public async Task CancellationKeepsCompletedPagesAndTheirSourceAfterReopen()
    {
        var input = WritePdf("cancel.pdf", "FIRST", "SECOND", "THIRD");
        using (var session = CreateSession())
        {
            using var scanner = new Scanner(Root);
            using var cancellation = new CancellationTokenSource();
            Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Import(session, [input], cancellation.Cancel, cancellation.Token));
            Assert.That(session.Pages.Count, Is.EqualTo(1));
        }
        File.Delete(input);
        using var reopened = Session.Open(Path.Combine(Root, "session"));
        using var exporter = new Scanner(Root);
        var target = Path.Combine(Root, "saved.pdf");
        await exporter.Export(reopened, target, ExportFormat.CombinedPdf, CancellationToken.None);
        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(document.PageCount, Is.EqualTo(1));
        AssertPdfContent(document.Pages[0], "FIRST");
    }

    [Test]
    public void CancellationBeforeImportDoesNotCopySourcesOrAddPages()
    {
        var input = WritePdf("cancel.pdf", "FIRST");
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Import(session, [input], () => { }, cancellation.Token));

        Assert.That(session.Pages, Is.Empty);
        Assert.That(Directory.GetFiles(Path.Combine(session.DirectoryPath, "originals")), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BadPdfReportsFileAndReasonWithoutLosingEarlierPages(bool encrypted)
    {
        var valid = WritePdf("valid.pdf", "SAVED");
        var bad = WritePdf("bad.pdf", "LOCKED");
        if (encrypted)
        {
            using var document = PdfReader.Open(bad, PdfDocumentOpenMode.Modify);
            document.SecuritySettings.UserPassword = "test-password";
            document.SecuritySettings.OwnerPassword = "test-owner";
            document.Save(bad);
        }
        else File.WriteAllText(bad, "%PDF-1.7\ninvalid PDF contents");
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        var error = Assert.ThrowsAsync<IOException>(() => scanner.Import(session, [valid, bad], () => { }, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("bad.pdf"));
        Assert.That(error.Message, encrypted ? Does.Contain("requires a password") : Does.Contain("damaged"));
        Assert.That(session.Pages.Count, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(Path.Combine(session.DirectoryPath, "originals"), "*.pdf").Length, Is.EqualTo(1));
        var target = Path.Combine(Root, "saved.pdf");
        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None);
        using var output = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        AssertPdfContent(output.Pages[0], "SAVED");
    }

    [Test]
    public async Task PdfWithNonPdfExtensionRetainsItsSource()
    {
        var input = WritePdf("document.bin", "DETECTED");
        using var session = CreateSession();
        using var scanner = new Scanner(Root);

        await scanner.Import(session, [input], () => { }, CancellationToken.None);

        Assert.That(session.Pages[0].PdfSource, Is.Not.Null);
    }

    [Test]
    public async Task MultiplePdfFilesKeepPickerOrderAroundImagePages()
    {
        var first = WritePdf("first.pdf", "FIRST");
        var second = WritePdf("second.pdf", "SECOND", "THIRD");
        var image = Path.Combine(Root, "image.png");
        WriteImage(image, 1);
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [second, image, first], () => { }, CancellationToken.None);
        var target = Path.Combine(Root, "ordered.pdf");

        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None);

        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(document.PageCount, Is.EqualTo(4));
        Assert.That(document.Pages.Cast<PdfPage>().Select(Text), Is.EqualTo(new[] { "SECOND", "THIRD", "", "FIRST" }));
    }

    [Test]
    public async Task ImagePdfWithHiddenTextKeepsItsSearchableLayer()
    {
        var input = Path.Combine(Root, "searchable.pdf");
        using var scanner = new Scanner(Root);
        using (var raster = Session.Create(Path.Combine(Root, "raster-session"), "Raster"))
        {
            AddPages(raster, 1);
            await scanner.Export(raster, input, ExportFormat.CombinedPdf, CancellationToken.None);
        }
        using (var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var page = document.Pages[0];
            AddStandardFont(document, page);
            // A page-filling image plus invisible searchable text must bypass single-image extraction.
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(
                "BT /F1 12 Tf 3 Tr 1 0 0 1 12 100 Tm (HIDDEN) Tj ET\n"));
            document.Save(input);
        }
        using var session = CreateSession();
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        session.Rotate(0);
        var target = Path.Combine(Root, "searchable-output.pdf");

        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None);

        using var exported = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(Text(exported.Pages[0]), Is.EqualTo("HIDDEN"));
        Assert.That(Operators(exported.Pages[0]).Any(op => op.OpCode.Name == "Do"), Is.True);
        Assert.That(exported.Pages[0].Rotate, Is.EqualTo(90));
    }

    [Test]
    public async Task OutOfRangeSourcePageAbortsExportWithoutPublishingPreview()
    {
        var input = WritePdf("source.pdf", "FIRST");
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        var original = session.Pages[0];
        session.Add(session.OriginalPath(original), original.PdfSource! with { PageIndex = 100 });
        var target = Path.Combine(Root, "invalid.pdf");

        Assert.ThrowsAsync<InvalidDataException>(() => scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None));

        Assert.That(File.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [TestCase(ExportFormat.Images)]
    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    public async Task PdfExportsKeepNoOverwriteAndCancellationGuarantees(ExportFormat format)
    {
        var input = WritePdf("document.pdf", "SAFE");
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        var target = Path.Combine(Root, "output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Export(session, target, format, cancellation.Token));
        Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
        await scanner.Export(session, target, format, CancellationToken.None);
        Assert.ThrowsAsync<IOException>(() => scanner.Export(session, target, format, CancellationToken.None));
    }

    internal string WritePdf(string name, params string[] labels)
    {
        var path = Path.Combine(Root, name);
        WritePdfFile(path, labels);
        return path;
    }

    internal static void WritePdfFile(string path, params string[] labels)
    {
        // Use upstream PdfSharp to generate standard-font text and vector paths; no font downloads.
        using var document = new PdfDocument();
        foreach (var label in labels)
        {
            var page = document.AddPage();
            page.Width = 120;
            page.Height = 180;
            AddStandardFont(document, page);
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(
                $"BT /F1 12 Tf 1 0 0 1 12 100 Tm ({label}) Tj ET\n0 0 0 rg 10 10 25 30 re f\n"));
        }
        document.Save(path);
    }

    private static void AddStandardFont(PdfDocument document, PdfPage page)
    {
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = font;
        page.Resources.Elements["/Font"] = fonts;
    }

    private static string Text(PdfPage page) => string.Concat(Operators(page)
        .Where(op => op.OpCode.Name == "Tj").SelectMany(op => op.Operands.OfType<CString>()).Select(s => s.Value));

    private static IEnumerable<COperator> Operators(PdfPage page) => ContentReader.ReadContent(page).OfType<COperator>();

    private static void AssertPdfContent(PdfPage page, string text, int rotation = 0)
    {
        Assert.That(Text(page), Is.EqualTo(text), "Text must remain text, rather than an image of text.");
        Assert.That(Operators(page).Any(op => op.OpCode.Name == "re"), Is.True, "The vector rectangle must remain a path.");
        Assert.That(Operators(page).Any(op => op.OpCode.Name == "Do"), Is.False, "These generated vector pages contain no raster objects.");
        Assert.That(page.Rotate, Is.EqualTo(rotation));
    }
}
