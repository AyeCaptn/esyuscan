using System.Security.Cryptography;
using NUnit.Framework;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture]
public sealed class AutomaticProcessingTests : TestFiles
{
    [TestCase(PageCorrection.Document)]
    [TestCase(PageCorrection.Photo)]
    public void ProcessingTouchesOnlySelectedPages(PageCorrection correction)
    {
        using var session = CreateSession();
        AddPages(session, 3);
        var before = session.Pages.ToArray();

        session.AutoProcessPages([before[0].Identity, before[2].Identity], correction, CancellationToken.None);

        Assert.That(session.Pages, Is.EqualTo(new[]
        {
            before[0] with { Adjustments = new PageAdjustments(Correction: correction) },
            before[1],
            before[2] with { Adjustments = new PageAdjustments(Correction: correction) }
        }));
    }

    [Test]
    public void ProcessingPreservesManualEditsAndReplacesOnlyCleanupAndDeskew()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.SetEdits(0, session.Pages[0] with
        {
            Crop = new PageCrop(2, 3, 30, 50),
            Rotation = 90,
            DeskewAngle = 4,
            Adjustments = AdjustmentTests.Combined
        });
        var before = session.Pages[0];

        session.AutoProcessPages([before.Identity], PageCorrection.Photo, CancellationToken.None);

        Assert.That(session.Pages[0], Is.EqualTo(before with
        {
            DeskewAngle = 0,
            Adjustments = AdjustmentTests.Combined with { Correction = PageCorrection.Photo }
        }));
    }

    [Test]
    public void OneUndoRestoresEveryProcessedPage()
    {
        using var session = CreateSession();
        AddPages(session, 3);
        var before = session.Pages.ToArray();
        session.AutoProcessPages(before.Select(page => page.Identity), PageCorrection.Document, CancellationToken.None);

        session.Undo();

        Assert.That(session.Pages, Is.EqualTo(before));
    }

    [Test]
    public void RedoRestoresTheWholeProcessedSelection()
    {
        using var session = CreateSession();
        AddPages(session, 3);
        session.AutoProcessPages(session.Pages.Select(page => page.Identity), PageCorrection.Photo, CancellationToken.None);
        var processed = session.Pages.ToArray();
        session.Undo();

        session.Redo();

        Assert.That(session.Pages, Is.EqualTo(processed));
    }

    [Test]
    public void ProcessingPersistsAcrossReopen()
    {
        using (var session = CreateSession())
        {
            AddPages(session, 2);
            session.AutoProcessPages(session.Pages.Select(page => page.Identity), PageCorrection.Photo, CancellationToken.None);
        }

        using var reopened = Session.Open(Path.Combine(Root, "session"));

        Assert.That(reopened.Pages.Select(page => page.Adjustments?.Correction),
            Is.EqualTo(new[] { PageCorrection.Photo, PageCorrection.Photo }));
    }

    [TestCase(1)]
    [TestCase(3)]
    public void CancellingAfterTheFirstPageLeavesTheManifestUnchanged(int count)
    {
        using var session = CreateSession();
        AddPages(session, count);
        var manifest = Path.Combine(session.DirectoryPath, "session.json");
        var before = File.ReadAllText(manifest);
        using var cancellation = new CancellationTokenSource();

        try
        {
            session.AutoProcessPages(session.Pages.Select(page => page.Identity), PageCorrection.Document,
                cancellation.Token, (_, _) => cancellation.Cancel());
        }
        catch (OperationCanceledException) { }

        Assert.That(File.ReadAllText(manifest), Is.EqualTo(before));
    }

    [Test]
    public void FailureOnALaterPageDoesNotApplyEarlierDrafts()
    {
        using var session = CreateSession();
        AddPages(session, 2);
        var before = session.Pages.ToArray();
        var path = session.OriginalPath(before[1]);
        File.Move(path, path + ".backup");
        try
        {
            try { session.AutoProcessPages(before.Select(page => page.Identity), PageCorrection.Document, CancellationToken.None); }
            catch (FileNotFoundException) { }

            Assert.That(session.Pages, Is.EqualTo(before));
        }
        finally { File.Move(path + ".backup", path); }
    }

    [Test]
    public void ProcessingNeverOverwritesOriginalImages()
    {
        using var session = CreateSession();
        AddPages(session, 2);
        var paths = session.Pages.Select(session.OriginalPath).ToArray();
        var hashes = paths.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();

        session.AutoProcessPages(session.Pages.Select(page => page.Identity), PageCorrection.Document, CancellationToken.None);

        Assert.That(paths.Select(path => SHA256.HashData(File.ReadAllBytes(path))), Is.EqualTo(hashes));
    }

    [Test]
    public void ReapplyingTheSamePresetDoesNotAddAnotherUndoStep()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var before = session.Pages.ToArray();
        session.AutoProcessPages([before[0].Identity], PageCorrection.Document, CancellationToken.None);
        session.AutoProcessPages([before[0].Identity], PageCorrection.Document, CancellationToken.None);

        session.Undo();

        Assert.That(session.Pages, Is.EqualTo(before));
    }

    [Test]
    public void ProcessingSharedOriginalsUsesPageIdentityNotFilename()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.Add(session.OriginalPath(session.Pages[0]));
        var before = session.Pages.ToArray();

        session.AutoProcessPages([before[1].Identity], PageCorrection.Document, CancellationToken.None);

        Assert.That(session.Pages, Is.EqualTo(new[]
        {
            before[0], before[1] with { Adjustments = new PageAdjustments(Correction: PageCorrection.Document) }
        }));
    }

    [Test]
    public void DeskewDetectsTheOriginalAngleRatherThanAccumulatingPreviousEdits()
    {
        using var session = CreateSession();
        var path = session.NextOriginalPath();
        using (var bitmap = new SKBitmap(800, 1000))
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black })
        {
            canvas.Clear(SKColors.White);
            canvas.RotateDegrees(5, 400, 500);
            for (var row = 0; row < 22; row++)
                for (var word = 0; word < 8; word++)
                    canvas.DrawRect(100 + word * 73, 150 + row * 30, 45, 9, paint);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            png.SaveTo(file);
        }
        session.Add(path);
        session.SetDeskew(0, 3);

        session.AutoProcessPages([session.Pages[0].Identity], PageCorrection.Document, CancellationToken.None);

        Assert.That(session.Pages[0].DeskewAngle, Is.EqualTo(-5).Within(0.6));
    }
}
