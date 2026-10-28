using NUnit.Framework;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Content;
using PdfSharpCore.Pdf.Content.Objects;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class SelectedExportTests : TestFiles
{
    [TestCase(ExportFormat.Images)]
    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    public async Task SelectionExportsOnlyChosenPagesInCurrentOrderAndPreservesSources(ExportFormat format)
    {
        var input = Path.Combine(Root, "source.pdf");
        PdfImportTests.WritePdfFile(input, "FIRST", "EXCLUDED", "THIRD");
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        var original = session.Pages.ToArray();
        session.Rotate(2);
        session.MovePages([original[2].FileName], 0);
        var saved = File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json"));
        var source = File.ReadAllBytes(session.PdfSourcePath(original[0].PdfSource!));
        var target = Path.Combine(Root, "selected");
        // Deliberately pass reverse order and a duplicate: identities are a set, order is the document's.
        var selection = new[] { original[0].FileName, original[2].FileName, original[0].FileName };
        var progress = new List<ExportProgress>();
        await scanner.Export(session, target, format, CancellationToken.None, progress: progress.Add, selectedPages: selection);
        Assert.That(File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(saved));
        Assert.That(File.ReadAllBytes(session.PdfSourcePath(original[0].PdfSource!)), Is.EqualTo(source));
        Assert.That(progress.Last().Fraction, Is.EqualTo(1));
        if (format == ExportFormat.Images)
        {
            var files = Directory.GetFiles(target).Order().ToArray();
            Assert.That(files.Select(Path.GetFileName), Is.EqualTo(new[] { "page-001.png", "page-002.png" }));
            for (var i = 0; i < 2; i++)
            {
                using var expectedStream = PageRenderer.Png(session, session.Pages[i]);
                using var expected = SKBitmap.Decode(expectedStream);
                using var actual = SKBitmap.Decode(files[i]);
                Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
            }
        }
        else if (format == ExportFormat.CombinedPdf)
        {
            using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            Assert.That(document.PageCount, Is.EqualTo(2));
            AssertNativePage(document.Pages[0], "THIRD", 90);
            AssertNativePage(document.Pages[1], "FIRST", 0);
        }
        else
        {
            var files = Directory.GetFiles(target).Order().ToArray();
            Assert.That(files.Select(Path.GetFileName), Is.EqualTo(new[] { "page-001.pdf", "page-002.pdf" }));
            using var first = PdfReader.Open(files[0], PdfDocumentOpenMode.Import);
            using var second = PdfReader.Open(files[1], PdfDocumentOpenMode.Import);
            Assert.That((first.PageCount, second.PageCount), Is.EqualTo((1, 1)));
            AssertNativePage(first.Pages[0], "THIRD", 90);
            AssertNativePage(second.Pages[0], "FIRST", 0);
        }
        Assert.ThrowsAsync<IOException>(() => scanner.Export(session, target, format, CancellationToken.None, selectedPages: selection));
    }

    [TestCase(ExportFormat.Images)]
    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    public void EmptyUnknownAndCancelledSelectionsDoNotPublish(ExportFormat format)
    {
        using var session = CreateSession();
        AddPages(session, 2);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "selected");
        Assert.ThrowsAsync<InvalidOperationException>(() => scanner.Export(session, target, format, CancellationToken.None, selectedPages: []));
        Assert.ThrowsAsync<ArgumentException>(() => scanner.Export(session, target, format, CancellationToken.None, selectedPages: ["missing.png"]));
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Export(session, target, format, cancellation.Token,
            progress: _ => cancellation.Cancel(), selectedPages: [session.Pages[1].FileName]));
        Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [TestCase(ExportFormat.Images)]
    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    public void DuplexReviewAlsoBlocksSelectedExports(ExportFormat format)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.BeginManualDuplex();
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "selected");
        Assert.ThrowsAsync<InvalidOperationException>(() => scanner.Export(session, target, format, CancellationToken.None, selectedPages: [session.Pages[0].FileName]));
        Assert.That(Path.Exists(target), Is.False);
    }

    [Test]
    public async Task SharedOriginalSelectionExportsOnlyTheChosenPageInstance()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.Add(session.OriginalPath(session.Pages[0]));
        session.Rotate(1);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "shared-original.pdf");
        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None,
            selectedPages: [session.Pages[1].Identity]);
        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(document.PageCount, Is.EqualTo(1));
        Assert.That(document.Pages[0].Width.Point, Is.GreaterThan(document.Pages[0].Height.Point));
    }

    private static void AssertNativePage(PdfPage page, string label, int rotation)
    {
        var operators = ContentReader.ReadContent(page).OfType<COperator>().ToArray();
        var text = string.Concat(operators.Where(op => op.OpCode.Name == "Tj").SelectMany(op => op.Operands.OfType<CString>()).Select(text => text.Value));
        Assert.That(text, Is.EqualTo(label));
        Assert.That(operators.Any(op => op.OpCode.Name == "re"), Is.True);
        Assert.That(operators.Any(op => op.OpCode.Name == "Do"), Is.False);
        Assert.That(page.Rotate, Is.EqualTo(rotation));
    }
}
