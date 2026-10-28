using System.Diagnostics;
using System.Security.Cryptography;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Pdf;
using NAPS2.Scan;
using NUnit.Framework;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture]
public sealed class EditTests : TestFiles
{
    [TestCase(-1, 0, 1, 1)]
    [TestCase(0, -1, 1, 1)]
    [TestCase(0, 0, 0, 1)]
    [TestCase(0, 0, 1, 0)]
    [TestCase(61, 0, 2, 1)]
    [TestCase(0, 87, 1, 2)]
    [TestCase(int.MaxValue, 0, int.MaxValue, 1)]
    public void InvalidCropDoesNotChangeManifest(int x, int y, int width, int height)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var before = File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json"));
        Assert.Throws<InvalidDataException>(() => session.SetCrop(0, new PageCrop(x, y, width, height)));
        Assert.That(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(before));
    }

    [Test]
    public void EditsUndoRedoAndReopenWithoutTouchingOriginal()
    {
        SessionPage edited;
        byte[] hash;
        string path;
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            path = session.OriginalPath(session.Pages[0]);
            hash = SHA256.HashData(File.ReadAllBytes(path));
            var original = session.Pages[0];
            session.SetDeskew(0, -4);
            session.SetCrop(0, new PageCrop(2, 3, 38, 60));
            session.Rotate(0);
            edited = session.Pages[0];
            for (var i = 0; i < 3; i++) session.Undo();
            Assert.That(session.Pages[0], Is.EqualTo(original));
            for (var i = 0; i < 3; i++) session.Redo();
            Assert.That(session.Pages[0], Is.EqualTo(edited));
        }
        using var recovered = Session.Open(Path.Combine(Root, "session"));
        Assert.That(recovered.Pages[0], Is.EqualTo(edited));
        Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(hash));
    }

    [Test]
    public void OpensLiteralVersionOneWithNoNewFields()
    {
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            File.WriteAllText(Path.Combine(session.DirectoryPath, "session.json"),
                $$"""{"Version":1,"Name":"Legacy","Pages":[{"FileName":"{{session.Pages[0].FileName}}","Rotation":90}]}""");
        }
        using var recovered = Session.Open(Path.Combine(Root, "session"));
        Assert.That(recovered.Pages[0].DeskewAngle, Is.Zero);
        Assert.That(recovered.Pages[0].Crop, Is.Null);
        using var image = PageRenderer.Render(recovered, recovered.Pages[0]);
        Assert.That((image.Width, image.Height), Is.EqualTo((88, 62)));
    }

    [Test]
    public void InvalidPersistedCropIsRejectedAndReleasesLock()
    {
        string manifest;
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            session.SetCrop(0, new PageCrop(2, 3, 38, 60));
            manifest = Path.Combine(session.DirectoryPath, "session.json");
        }
        var valid = File.ReadAllText(manifest);
        File.WriteAllText(manifest, valid.Replace("\"Width\": 38", "\"Width\": 9999"));
        Assert.Throws<InvalidDataException>(() => Session.Open(Path.GetDirectoryName(manifest)!).Dispose());
        File.WriteAllText(manifest, valid);
        using var recovered = Session.Open(Path.GetDirectoryName(manifest)!);
        Assert.That(recovered.Pages.Count, Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public void DisplayCropMapsToSameContentAcrossQuarterTurns(int rotation)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var crop = new PageCrop(3, 5, 30, 50);
        var displayed = crop;
        var width = 62;
        var height = 88;
        for (var angle = 0; angle < rotation; angle += 90)
        {
            displayed = displayed.RotateClockwise(width, height);
            (width, height) = (height, width);
            session.Rotate(0);
        }
        Assert.That(PageCrop.FromDisplay(displayed, rotation, 62, 88), Is.EqualTo(crop));
        session.SetCrop(0, crop);
        using var rendered = PageRenderer.Render(session, session.Pages[0]);
        Assert.That((rendered.Width, rendered.Height), Is.EqualTo(rotation is 90 or 270 ? (50, 30) : (30, 50)));
    }

    [TestCase(0, 3, 5, 30, 50)]
    [TestCase(90, 33, 3, 50, 30)]
    [TestCase(180, 29, 33, 30, 50)]
    [TestCase(270, 5, 29, 50, 30)]
    public void RotatedCropKeepsExpectedOriginalPixels(int rotation, int x, int y, int width, int height)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        using var original = SKBitmap.Decode(session.OriginalPath(session.Pages[0]));
        var crop = PageCrop.FromDisplay(new PageCrop(x, y, width, height), rotation, 62, 88);
        Assert.That(crop, Is.EqualTo(new PageCrop(3, 5, 30, 50)));
        session.SetCrop(0, crop);
        for (var angle = 0; angle < rotation; angle += 90) session.Rotate(0);
        using var png = PageRenderer.Png(session, session.Pages[0]);
        using var actual = SKBitmap.Decode(png);
        for (var row = 0; row < actual.Height; row++)
            for (var column = 0; column < actual.Width; column++)
            {
                var (sourceX, sourceY) = rotation switch
                {
                    90 => (row, 49 - column),
                    180 => (29 - column, 49 - row),
                    270 => (29 - row, column),
                    _ => (column, row)
                };
                Assert.That(actual.GetPixel(column, row), Is.EqualTo(original.GetPixel(sourceX + 3, sourceY + 5)));
            }
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(21)]
    [TestCase(-21)]
    public void InvalidDeskewDoesNotSave(double angle)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetDeskew(0, angle));
        Assert.That(session.Pages[0].DeskewAngle, Is.Zero);
    }

    [Test]
    public void OnePixelEdgeCropIsValidAndRotationDoesNotChangeStoredCrop()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var crop = new PageCrop(61, 87, 1, 1);
        session.SetCrop(0, crop);
        for (var i = 0; i < 4; i++) session.Rotate(0);
        Assert.That(session.Pages[0].Crop, Is.EqualTo(crop));
        using var image = PageRenderer.Render(session, session.Pages[0]);
        Assert.That((image.Width, image.Height), Is.EqualTo((1, 1)));
    }

    [TestCase(0)]
    [TestCase(5)]
    [TestCase(-7)]
    public void UpstreamDeskewStraightensGeneratedContent(double skew)
    {
        using var session = CreateSession();
        var path = session.NextOriginalPath();
        WriteSkewed(path, skew);
        session.Add(path);
        var angle = PageRenderer.DetectDeskew(session, session.Pages[0], CancellationToken.None);
        Assert.That(angle, Is.EqualTo(-skew).Within(0.6));
        session.SetDeskew(0, angle);
        using var straightened = PageRenderer.Render(session, session.Pages[0]);
        var target = Path.Combine(Root, "straight.png");
        straightened.Save(target, ImageFileFormat.Png);
        session.Add(CopyToOriginal(session, target));
        Assert.That(Math.Abs(PageRenderer.DetectDeskew(session, session.Pages[1], CancellationToken.None)), Is.LessThan(0.6));
    }

    [Test]
    public void CancelledDeskewLeavesSessionUnchanged()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var page = session.Pages[0];
        Assert.Throws<OperationCanceledException>(() => PageRenderer.DetectDeskew(session, page, new CancellationToken(true)));
        Assert.That(session.Pages[0], Is.EqualTo(page));
    }

    [Test]
    public void ScanSettingsMapAutoDeskewToInstalledSdk()
    {
        var settings = new ScanSettings(new ScanDevice(Driver.Escl, "fixture", "Fixture"), PaperSource.Flatbed,
            BitDepth.Color, 300, PageSize.A4);
        Assert.That(settings.ToOptions().AutoDeskew, Is.False);
        Assert.That((settings with { AutoDeskew = true }).ToOptions().AutoDeskew, Is.True);
        Assert.That(settings.ToOptions().MaxQuality, Is.True);
    }

    [TestCase(ExportFormat.Images)]
    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    [Category("Integration")]
    public async Task EveryExportMatchesSharedRenderedPixels(ExportFormat format)
    {
        using var session = CreateSession();
        var original = session.NextOriginalPath();
        WriteSkewed(original, 5);
        session.Add(original);
        session.SetDeskew(0, -5);
        session.SetCrop(0, new PageCrop(100, 120, 390, 500));
        session.Rotate(0);
        session.SetAdjustments(0, AdjustmentTests.Combined);
        using var expected = PageRenderer.Png(session, session.Pages[0]);
        using var expectedBitmap = SKBitmap.Decode(expected);
        using var scanner = new Scanner(Root);
        var destination = Path.Combine(Root, format == ExportFormat.CombinedPdf ? "out.pdf" : "out");
        await scanner.Export(session, destination, format, CancellationToken.None);
        if (format == ExportFormat.Images)
        {
            using var actual = SKBitmap.Decode(Path.Combine(destination, "page-001.png"));
            AssertSamePixels(expectedBitmap, actual);
        }
        else
        {
            using var context = new ScanningContext(new GtkImageContext());
            var file = format == ExportFormat.CombinedPdf ? destination : Path.Combine(destination, "page-001.pdf");
            var count = 0;
            await foreach (var image in new PdfImporter(context).Import(file))
            {
                using (image)
                using (var png = image.SaveToMemoryStream(ImageFileFormat.Png))
                {
                    png.Position = 0;
                    using var actual = SKBitmap.Decode(png);
                    AssertSamePixels(expectedBitmap, actual);
                    count++;
                }
            }
            Assert.That(count, Is.EqualTo(1));
        }
    }

    [Test, Category("Integration")]
    public async Task CliExportUsesPersistedEdits()
    {
        SKColor[] expected;
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            session.SetCrop(0, new PageCrop(2, 3, 38, 60));
            session.Rotate(0);
            session.SetAdjustments(0, AdjustmentTests.Combined);
            using var png = PageRenderer.Png(session, session.Pages[0]);
            using var bitmap = SKBitmap.Decode(png);
            expected = bitmap.Pixels;
        }
        var output = Path.Combine(Root, "cli");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "export", Path.Combine(Root, "session"), "--format", "images", "--output", output, "--json" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.That(process.ExitCode, Is.Zero, await stderr);
        Assert.That(await stdout, Does.Contain("\"ok\":true"));
        using var image = SKBitmap.Decode(Path.Combine(output, "page-001.png"));
        Assert.That((image.Width, image.Height), Is.EqualTo((60, 38)));
        Assert.That(image.Pixels, Is.EqualTo(expected));
    }

    private static string CopyToOriginal(Session session, string path)
    {
        var original = session.NextOriginalPath();
        File.Copy(path, original);
        return original;
    }

    private static void AssertSamePixels(SKBitmap expected, SKBitmap actual)
    {
        Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
        Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
    }

    private static void WriteSkewed(string path, double skew)
    {
        using var bitmap = new SKBitmap(800, 1000);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.RotateDegrees((float)skew, 400, 500);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (var row = 0; row < 22; row++)
            for (var word = 0; word < 8; word++)
                canvas.DrawRect(100 + word * 73, 150 + row * 30, 45 + word % 3 * 4, 9, paint);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        png.SaveTo(stream);
    }
}
