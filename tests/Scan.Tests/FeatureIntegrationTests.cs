using System.Text.Json;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.ImportExport;
using NAPS2.Pdf;
using NAPS2.Scan;
using NUnit.Framework;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class FeatureIntegrationTests : TestFiles
{
    [Test]
    public async Task SelectedAdjustedPageCanExportAsPdfAWithoutRasterizingExcludedPdfSources()
    {
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        var input = Path.Combine(Root, "excluded.pdf");
        PdfImportTests.WritePdfFile(input, "EXCLUDED");
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        AddPages(session, 1, detailed: true);
        session.SetAdjustments(1, new(Brightness: 100, Contrast: 50));
        var original = File.ReadAllBytes(session.OriginalPath(session.Pages[1]));
        var target = Path.Combine(Root, "selected.pdf");

        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { Compatibility = PdfCompat.PdfA2B, Metadata = new() { Title = "Selected adjustment" } },
            selectedPages: [session.Pages[1].Identity]);

        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(document.PageCount, Is.EqualTo(1));
        Assert.That(document.Info.Title, Is.EqualTo("Selected adjustment"));
        Assert.That(File.ReadAllBytes(session.OriginalPath(session.Pages[1])), Is.EqualTo(original));
        Assert.That(session.Pages[0].PreservePdf, Is.True);
    }

    [Test]
    public async Task SelectedMultipageTiffUsesCurrentOrderAndFinalAdjustmentPixels()
    {
        using var session = CreateSession();
        AddPages(session, 3);
        session.SetCrop(0, new(2, 3, 40, 60));
        session.SetAdjustments(0, new(Brightness: 100, FlipHorizontal: true));
        session.Rotate(2);
        var selected = new[] { session.Pages[0].Identity, session.Pages[2].Identity };
        session.MovePages([selected[1]], 0);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "selected.tiff");
        await scanner.Export(session, target, ExportFormat.CombinedTiff, CancellationToken.None,
            options: new() { TiffCompression = TiffCompressionType.Lzw }, selectedPages: selected);

        using var context = new ScanningContext(new GtkImageContext());
        var expectedPages = session.Pages.Where(page => selected.Contains(page.Identity)).ToArray();
        var index = 0;
        await foreach (var image in new ImageImporter(context).Import(target))
        {
            using (image)
            using (var expectedPng = PageRenderer.Png(session, expectedPages[index]))
            using (var expected = SKBitmap.Decode(expectedPng))
            using (var png = image.SaveToMemoryStream(ImageFileFormat.Png))
            {
                png.Position = 0;
                using var actual = SKBitmap.Decode(png);
                Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
                Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
            }
            index++;
        }
        Assert.That(index, Is.EqualTo(2));
    }

    [Test]
    public async Task BatchRecoveryKeepsExistingAdjustmentAndSharedOriginalsWhileExportingProtectedDocuments()
    {
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            session.SetAdjustments(0, new(Brightness: 50));
            session.Add(session.OriginalPath(session.Pages[0]));
            session.BeginBatch(new(2));
            await session.RunBatch(_ => { AddPages(session, 1); return Task.CompletedTask; }, CancellationToken.None);
            session.ConfirmBatchReview(true);
        }
        using var reopened = Session.Open(Path.Combine(Root, "session"));
        Assert.That(reopened.Pages[0].Adjustments!.Brightness, Is.EqualTo(50));
        Assert.That(reopened.Pages[0].Identity, Is.Not.EqualTo(reopened.Pages[1].Identity));
        var command = ExportCommandOptions.Parse(["export", "session", "--format", "documents", "--output", "out", "--pdf-compression", "jpeg", "--quality", "60"]);
        Assert.That(command.Documents, Is.True);
        Assert.That(command.Options.EffectiveQuality, Is.EqualTo(60));
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "documents");
        var owner = Guid.NewGuid().ToString("N");
        await scanner.ExportBatchDocuments(reopened, target, CancellationToken.None, options: new()
        {
            Metadata = new() { Title = "Reviewed batch" },
            Passwords = new() { Owner = owner },
            Permissions = new() { Copying = false }
        });

        var files = Directory.GetFiles(target, "*.pdf").Order().ToArray();
        Assert.That(files, Has.Length.EqualTo(2));
        foreach (var file in files)
        {
            using var document = PdfReader.Open(file, owner, PdfDocumentOpenMode.Import);
            Assert.That(document.PageCount, Is.EqualTo(1));
            Assert.That(document.Info.Title, Is.EqualTo("Reviewed batch"));
            Assert.That(document.SecuritySettings.PermitExtractContent, Is.False);
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(reopened.DirectoryPath, "session.json")));
        Assert.That(manifest.RootElement.GetProperty("Version").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public void ExactImportedPresetKeepsGeometryAndNeverSubstitutesAnotherScanner()
    {
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        var geometry = new ScanGeometry(101.6m, 152.4m, HorizontalAlign.Center, new(10, 20, 50, 70), "in");
        var settings = ScanTestData.Settings() with { Size = "Custom", Geometry = geometry, RequireExactDevice = true };
        var origin = new Naps2ProfileOrigin("Imported", [], []);
        store.Save(new(1, settings, [new("imported", "Imported", settings, origin)]));
        using var model = new MainViewModel(store);
        model.SetDevices([ScanTestData.Device("unrelated")]);
        Assert.That(model.SelectedDevice, Is.Null);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        var restored = model.CurrentScanSettings();
        Assert.That(restored.Size.WidthInMm, Is.EqualTo(101.6m));
        Assert.That(restored.Area, Is.EqualTo(geometry.Area));
        Assert.That(restored.Alignment, Is.EqualTo(HorizontalAlign.Center));
        model.RememberCurrentSettings();
        Assert.That(store.Load().Preferences.LastScan!.RequireExactDevice, Is.True);
        Assert.That(store.Load().Preferences.LastScan!.Geometry, Is.EqualTo(geometry));
    }

    [Test]
    public void BatchPreventsStartingManualDuplex()
    {
        using var model = new MainViewModel();
        model.SetSession(CreateSession());
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SelectedSource = model.Sources.Single(source => source.Value == PaperSource.Feeder);
        model.Session!.BeginBatch(new(1));
        model.SyncPages();
        Assert.That(model.CanStartDuplex, Is.False);
    }
}
