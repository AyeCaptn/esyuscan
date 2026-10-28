using System.Security.Cryptography;
using System.Text.Json;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Images.Transforms;
using NUnit.Framework;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture]
public sealed class AdjustmentTests : TestFiles
{
    public static readonly PageAdjustments Combined = new(75, 100, 350, 12.3, true, true, PageCorrection.Document);

    [Test]
    public void AdjustmentsPersistUndoRedoAndResetWithoutChangingRecoverySources()
    {
        SessionPage edited;
        byte[] originalHash;
        byte[] pdfHash;
        string originalPath;
        string pdfPath;
        using (var session = CreateSession())
        {
            AddPages(session, 1, detailed: true);
            originalPath = session.OriginalPath(session.Pages[0]);
            session.Remove(0);
            pdfPath = session.NextPdfSourcePath();
            PdfImportTests.WritePdfFile(pdfPath, "Recovery");
            session.Add(originalPath, new(Path.GetFileName(pdfPath), 0));
            var original = session.Pages[0];
            originalHash = SHA256.HashData(File.ReadAllBytes(originalPath));
            pdfHash = SHA256.HashData(File.ReadAllBytes(pdfPath));
            session.SetEdits(0, original with { Crop = new(20, 30, 500, 700), DeskewAngle = -2, Rotation = 90, Adjustments = Combined });
            edited = session.Pages[0];
            Assert.That(edited.PreservePdf, Is.False);
            session.Undo();
            Assert.That(session.Pages[0], Is.EqualTo(original));
            Assert.That(session.Pages[0].PreservePdf, Is.True);
            session.Redo();
            Assert.That(session.Pages[0], Is.EqualTo(edited));
            session.ResetEdits(0);
            Assert.That(session.Pages[0], Is.EqualTo(original));
            Assert.That(session.Pages[0].PreservePdf, Is.True);
            session.Undo();
        }
        using var recovered = Session.Open(Path.Combine(Root, "session"));
        Assert.That(recovered.Pages[0], Is.EqualTo(edited));
        Assert.That(SHA256.HashData(File.ReadAllBytes(originalPath)), Is.EqualTo(originalHash));
        Assert.That(SHA256.HashData(File.ReadAllBytes(pdfPath)), Is.EqualTo(pdfHash));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(recovered.DirectoryPath, "session.json")));
        Assert.That(json.RootElement.GetProperty("Version").GetInt32(), Is.EqualTo(3));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void LiteralOldSessionsHaveNeutralAdjustments(int version)
    {
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            File.WriteAllText(Path.Combine(session.DirectoryPath, "session.json"),
                $$$"""{"Version":{{{version}}},"Name":"Legacy","Pages":[{"FileName":"{{{session.Pages[0].FileName}}}","Rotation":90,"DeskewAngle":-2,"Crop":{"X":2,"Y":3,"Width":38,"Height":60}}]}""");
        }
        using var reopened = Session.Open(Path.Combine(Root, "session"));
        Assert.That(reopened.Pages[0].Adjustments, Is.Null);
        using var image = PageRenderer.Render(reopened, reopened.Pages[0]);
        Assert.That((image.Width, image.Height), Is.EqualTo((60, 38)));
    }

    [Test]
    public void NeutralAdjustmentsAreCanonicalAndDoNotCreateHistoryOrUpgradeFormat()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.Undo();
        session.Redo();
        var before = File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json"));
        session.SetAdjustments(0, new());
        Assert.That(session.Pages[0].Adjustments, Is.Null);
        Assert.That(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(before));
        session.Undo();
        Assert.That(session.Pages, Is.Empty, "Neutral edits must not add a redundant undo step.");
    }

    private static IEnumerable<PageAdjustments> InvalidAdjustments()
    {
        yield return new(Brightness: 1001);
        yield return new(Brightness: -1001);
        yield return new(Contrast: -1001);
        yield return new(Contrast: 1001);
        yield return new(Sharpness: -1);
        yield return new(Sharpness: 1001);
        yield return new(Angle: 180.01);
        yield return new(Angle: -180.01);
        yield return new(Angle: double.NaN);
        yield return new(Angle: double.PositiveInfinity);
        yield return new(Correction: (PageCorrection)99);
    }

    [TestCaseSource(nameof(InvalidAdjustments))]
    public void InvalidAdjustmentsDoNotChangeSession(PageAdjustments adjustments)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var before = session.Pages[0];
        var saved = File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json"));
        Assert.Throws<InvalidDataException>(() => session.SetAdjustments(0, adjustments));
        Assert.That(session.Pages[0], Is.EqualTo(before));
        Assert.That(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(saved));
    }

    [TestCase("\"Angle\":181")]
    [TestCase("\"Sharpness\":-1")]
    [TestCase("\"Correction\":99")]
    public void InvalidPersistedAdjustmentsAreRejected(string property)
    {
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            File.WriteAllText(Path.Combine(session.DirectoryPath, "session.json"),
                $$$"""{"Version":3,"Name":"Invalid","Pages":[{"FileName":"{{{session.Pages[0].FileName}}}","Adjustments":{ {{{property}}} }}]}""");
        }
        Assert.Throws<InvalidDataException>(() => Session.Open(Path.Combine(Root, "session")).Dispose());
    }

    [Test]
    public void FailedAdjustmentSaveKeepsHistoryAndManifestAndCleansTemporaryFiles()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.SetAdjustments(0, Combined);
        session.Undo();
        var before = session.Pages[0];
        var manifest = Path.Combine(session.DirectoryPath, "session.json");
        File.Move(manifest, manifest + ".backup");
        Directory.CreateDirectory(manifest);
        try
        {
            Assert.Catch<IOException>(() => session.SetAdjustments(0, Combined));
            Assert.That(session.Pages[0], Is.EqualTo(before));
            Assert.That(session.CanRedo, Is.True);
            Assert.That(Directory.GetFiles(session.DirectoryPath, "*.tmp"), Is.Empty);
        }
        finally { Directory.Delete(manifest); File.Move(manifest + ".backup", manifest); }
        session.Redo();
        Assert.That(session.Pages[0].Adjustments, Is.EqualTo(Combined));
    }

    [Test]
    public void EditDraftCannotReplaceImmutableSources()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        Assert.Throws<ArgumentException>(() => session.SetEdits(0, session.Pages[0] with { FileName = "other.png" }));
        Assert.Throws<ArgumentException>(() => session.SetEdits(0, session.Pages[0] with { PdfSource = new("other.pdf", 0) }));
    }

    [TestCase(0, true, false)]
    [TestCase(90, false, true)]
    [TestCase(180, true, true)]
    [TestCase(270, true, false)]
    public void FlipsUseDisplayedAxesAfterCropAndQuarterTurn(int rotation, bool horizontal, bool vertical)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var page = session.Pages[0] with { Crop = new(2, 3, 38, 60), Rotation = rotation, DeskewAngle = -2 };
        using var before = PageRenderer.Png(session, page);
        using var expected = SKBitmap.Decode(before);
        using var after = PageRenderer.Png(session, page with { Adjustments = new(FlipHorizontal: horizontal, FlipVertical: vertical) });
        using var actual = SKBitmap.Decode(after);
        Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
        for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
                Assert.That(actual.GetPixel(x, y), Is.EqualTo(expected.GetPixel(horizontal ? expected.Width - 1 - x : x, vertical ? expected.Height - 1 - y : y)));
    }

    [TestCase(0, -180)]
    [TestCase(90, -46)]
    [TestCase(180, 12.3)]
    [TestCase(270, 90)]
    [TestCase(0, 180)]
    public void CombinedPipelineMatchesOrderedUpstreamTransforms(int rotation, double angle)
    {
        using var session = CreateSession();
        AddPages(session, 1, detailed: true);
        var page = session.Pages[0] with { DeskewAngle = -3, Crop = new(30, 50, 450, 700), Rotation = rotation, Adjustments = Combined with { Angle = angle } };
        using var sdk = new GtkImageContext().Load(session.OriginalPath(page))
            .PerformTransform(new RotationTransform(-3))
            .PerformTransform(new CropTransform(30, 140, 50, 127))
            .PerformTransform(new RotationTransform(rotation))
            .PerformTransform(new RotationTransform(angle))
            // Two flips are exactly a 180-degree quarter-turn, independently verifying their position.
            .PerformTransform(new RotationTransform(180))
            .PerformTransform(new CorrectionTransform(CorrectionMode.Document))
            .PerformTransform(new BrightnessTransform(75))
            .PerformTransform(new TrueContrastTransform(100))
            .PerformTransform(new SharpenTransform(350));
        using var expectedPng = sdk.SaveToMemoryStream(ImageFileFormat.Png);
        using var expected = SKBitmap.Decode(expectedPng);
        using var actualPng = PageRenderer.Png(session, page);
        using var actual = SKBitmap.Decode(actualPng);
        Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
        Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
    }

    [TestCase(PageCorrection.Document)]
    [TestCase(PageCorrection.Photo)]
    public void CorrectionMatchesSdkAndChangesGeneratedPaperTones(PageCorrection correction)
    {
        using var session = CreateSession();
        var path = session.NextOriginalPath();
        using (var source = new SKBitmap(120, 160))
        {
            using var canvas = new SKCanvas(source);
            canvas.Clear(new SKColor(190, 185, 180));
            using var paint = new SKPaint { Color = new SKColor(65, 60, 55) };
            canvas.DrawRect(20, 20, 50, 80, paint);
            using var data = source.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }
        session.Add(path);
        using var sdk = new GtkImageContext().Load(path).PerformTransform(new CorrectionTransform(correction == PageCorrection.Document ? CorrectionMode.Document : CorrectionMode.Photo));
        using var expectedPng = sdk.SaveToMemoryStream(ImageFileFormat.Png);
        using var expected = SKBitmap.Decode(expectedPng);
        using var actualPng = PageRenderer.Png(session, session.Pages[0] with { Adjustments = new(Correction: correction) });
        using var actual = SKBitmap.Decode(actualPng);
        using var original = SKBitmap.Decode(path);
        Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
        Assert.That(actual.Pixels, Is.Not.EqualTo(original.Pixels));
    }

    [Test]
    public void CancelledRenderingKeepsOriginalAndManifestAndCanRenderAgain()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.SetAdjustments(0, Combined);
        var manifest = File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json"));
        Assert.Throws<OperationCanceledException>(() => PageRenderer.Render(session, session.Pages[0], new CancellationToken(true)).Dispose());
        Assert.Throws<OperationCanceledException>(() => PageRenderer.Png(session, session.Pages[0], new CancellationToken(true)).Dispose());
        Assert.That(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(manifest));
        using var rendered = PageRenderer.Render(session, session.Pages[0]);
        Assert.That(rendered.Width, Is.Positive);
    }

    [Test]
    public void CropCachedCanvasPreviewMatchesFullPipelineWithAllAdjustments()
    {
        using var session = CreateSession();
        AddPages(session, 1, detailed: true);
        var page = session.Pages[0] with { DeskewAngle = 3, Crop = new(20, 30, 500, 700), Rotation = 90, Adjustments = Combined };
        using var canvas = PageRenderer.Render(session, page with { Crop = null, Rotation = 0, Adjustments = null });
        using var cachedPng = PageRenderer.Png(canvas, page.Crop, page.Rotation, page.Adjustments);
        using var fullPng = PageRenderer.Png(session, page);
        using var cached = SKBitmap.Decode(cachedPng);
        using var full = SKBitmap.Decode(fullPng);
        Assert.That(cached.Pixels, Is.EqualTo(full.Pixels));
    }
}
