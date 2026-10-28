using NUnit.Framework;
using PdfSharpCore.Pdf.IO;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class ExportTests : TestFiles
{
    [TestCase(ExportFormat.Images, "page-001.png", "page-020.png")]
    [TestCase(ExportFormat.SeparatePdfs, "page-001.pdf", "page-020.pdf")]
    public async Task TwentyPagesExportToTwentyNumberedFiles(ExportFormat format, string first, string last)
    {
        using var session = CreateSession();
        AddPages(session, 20);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "export");

        await scanner.Export(session, target, format, CancellationToken.None);

        var files = Directory.GetFiles(target).Select(Path.GetFileName).Order().ToArray();
        Assert.That((files.Length, files.First(), files.Last()), Is.EqualTo((20, first, last)));
    }

    [Test]
    public async Task TwentyPagesExportToOneTwentyPagePdf()
    {
        using var session = CreateSession();
        AddPages(session, 20);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "document.pdf");

        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None);

        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(document.PageCount, Is.EqualTo(20));
    }

    [Test]
    public async Task RotatedPageExportsInLandscape()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.Rotate(0);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "document.pdf");

        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None);

        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(document.Pages[0].Width.Point, Is.GreaterThan(document.Pages[0].Height.Point));
    }

    [Test]
    public void ExportRefusesToOverwriteExistingPdf()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var target = Path.Combine(Root, "document.pdf");
        File.WriteAllText(target, "Existing document");
        using var scanner = new Scanner(Root);

        Assert.ThrowsAsync<IOException>(() => scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None));
    }

    [Test]
    public void CancelledExportDoesNotPublishPartialOutput()
    {
        using var session = CreateSession();
        AddPages(session, 2);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "export");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { scanner.Export(session, target, ExportFormat.Images, cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }

        Assert.That(Directory.Exists(target), Is.False);
    }
}
