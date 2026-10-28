using System.Runtime.CompilerServices;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.ImportExport;
using NAPS2.Scan;
using NUnit.Framework;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class FakeScanIntegrationTests : TestFiles
{
    [Test]
    public async Task AreaSelectionSavesOnlySelectedPixelsUsingImageResolutionAndOldSessionFormat()
    {
        var input = Path.Combine(Root, "input.png");
        WriteImage(input, 4);
        using var context = new ScanningContext(new GtkImageContext());
        var fake = new FakeScannerBackend { Images = token => Images(context, input, token) };
        using var scanner = new Scanner(Root, backend: fake);
        var path = Path.Combine(Root, "session");
        using (var session = Session.Create(path, "Fake scan"))
        {
            var settings = new ScanSettings(ScanTestData.Device(), PaperSource.Flatbed, BitDepth.Color, 300, PageSize.A4,
                Area: new(2.54m, 5.08m, 7.62m, 10.16m));
            var added = 0;
            await scanner.Scan(session, settings, () => added++, _ => { }, CancellationToken.None);
            Assert.That(added, Is.EqualTo(1));
            using var actual = SKBitmap.Decode(session.OriginalPath(session.Pages.Single()));
            using var original = SKBitmap.Decode(input);
            Assert.That(actual.Width, Is.EqualTo(6));
            Assert.That(actual.Height, Is.EqualTo(8));
            // Fixture reports 20 DPI (different from requested 300): physical origin is (2,4).
            for (var y = 0; y < actual.Height; y++)
                for (var x = 0; x < actual.Width; x++) Assert.That(actual.GetPixel(x, y), Is.EqualTo(original.GetPixel(x + 2, y + 4)));
            Assert.That(session.Pages.Single().Crop, Is.Null, "Acquisition selection is baked into a new immutable original");
            Assert.That(File.ReadAllText(Path.Combine(path, "session.json")), Does.Contain("\"Version\": 1"));
        }
        using var reopened = Session.Open(path);
        Assert.That(reopened.Pages.Count, Is.EqualTo(1));
    }

    [Test]
    public void CancelledFakeScanKeepsCompletedPagesAndReleasesTemporaryFiles()
    {
        var input = Path.Combine(Root, "input.png");
        WriteImage(input, 1);
        using var context = new ScanningContext(new GtkImageContext());
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeScannerBackend { Images = token => Images(context, input, token, cancellation) };
        using var scanner = new Scanner(Root, backend: fake);
        using var session = CreateSession();
        var settings = new ScanSettings(ScanTestData.Device(), PaperSource.Flatbed, BitDepth.Color, 300, PageSize.A4);
        Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Scan(session, settings, () => { }, _ => { }, cancellation.Token));
        Assert.That(session.Pages.Count, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(Path.Combine(session.DirectoryPath, "originals")).Length, Is.EqualTo(1));
    }

    [Test]
    public void ResponseTooSmallForAreaFailsWithoutPublishingAnOriginal()
    {
        var input = Path.Combine(Root, "input.png");
        WriteImage(input, 1);
        using var context = new ScanningContext(new GtkImageContext());
        var fake = new FakeScannerBackend { Images = token => Images(context, input, token) };
        using var scanner = new Scanner(Root, backend: fake);
        using var session = CreateSession();
        var settings = new ScanSettings(ScanTestData.Device(), PaperSource.Flatbed, BitDepth.Color, 300, PageSize.A4,
            Area: new(100, 100, 100, 100));
        Assert.ThrowsAsync<InvalidDataException>(() => scanner.Scan(session, settings, () => { }, _ => { }, CancellationToken.None));
        Assert.That(session.Pages, Is.Empty);
        Assert.That(Directory.GetFiles(Path.Combine(session.DirectoryPath, "originals")), Is.Empty);
    }

    private static async IAsyncEnumerable<ProcessedImage> Images(ScanningContext context, string file,
        [EnumeratorCancellation] CancellationToken token, CancellationTokenSource? cancelAfterPage = null)
    {
        // In-memory fixture through the real SDK importer and raster pipeline; no scanner driver is invoked.
        await foreach (var image in new ImageImporter(context).Import(file, progress: token))
        {
            using (image)
            using (var memory = image.Render())
            {
                memory.SetResolution(20, 20);
                using var png = memory.SaveToMemoryStream(ImageFileFormat.Png);
                await foreach (var fixture in new ImageImporter(context).Import(png, progress: token)) yield return fixture;
            }
        }
        if (cancelAfterPage is not null) { cancelAfterPage.Cancel(); token.ThrowIfCancellationRequested(); }
    }
}
