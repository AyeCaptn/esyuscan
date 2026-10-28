using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Pdf;
using NAPS2.Scan;
using NUnit.Framework;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class PdfiumTests : TestFiles
{
    [Test]
    public async Task RasterizesPdfPagesWithBundledNativeLibrary()
    {
        // AirScan can return PDF even when the user is scanning to images.
        // A vector page exercises PDFium rendering, rather than image extraction.
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = 72;
        page.Height = 144;
        using (var graphics = XGraphics.FromPdfPage(page))
            graphics.DrawRectangle(XBrushes.Black, 0, 0, 72, 144);
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        stream.Position = 0;
        using var context = new ScanningContext(new GtkImageContext());
        var importer = new PdfImporter(context);
        var count = 0;

        await foreach (var image in importer.Import(stream))
        {
            using (image)
            {
                var path = Path.Combine(Root, "page.png");
                image.Save(path);
                using var bitmap = SKBitmap.Decode(path);
                Assert.That(bitmap.Height, Is.EqualTo(bitmap.Width * 2));
                Assert.That(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2), Is.EqualTo(SKColors.Black));
                count++;
            }
        }

        Assert.That(count, Is.EqualTo(1));
    }
}
