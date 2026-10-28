using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NAPS2.Images;
using NAPS2.Scan;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using NUnit.Framework;

[assembly: AvaloniaTestApplication(typeof(Scan.Tests.TestAppBuilder))]

namespace Scan.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia().UseHarfBuzz();
}

[TestFixture]
public sealed class UiTests : TestFiles
{
    [AvaloniaTest]
    [TestCase("ExportSelectedCheckBox", 760)]
    [TestCase("SearchablePdfCheckBox", 760)]
    [TestCase("ExportSelectedCheckBox", 1280)]
    [TestCase("SearchablePdfCheckBox", 1280)]
    public void FooterCheckboxLabelsHaveSpaceAfterTheSquare(string name, int width)
    {
        var window = new MainWindow(new MainViewModel(), discover: false) { Width = width, Height = 680 };

        window.Show();

        try
        {
            var checkbox = window.FindControl<CheckBox>(name)!;
            var square = checkbox.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "border");
            var label = checkbox.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single(presenter => presenter.Name == "PART_ContentPresenter");
            var squareRight = square.TranslatePoint(new Point(square.Bounds.Width, 0), checkbox)!.Value.X;
            var labelLeft = label.TranslatePoint(default, checkbox)!.Value.X;

            Assert.That(labelLeft - squareRight, Is.EqualTo(8));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(760, 680)]
    [TestCase(845, 900)]
    [TestCase(1280, 900)]
    public void CompactWorkspaceLeavesMostOfTheHeightForThePreview(int width, int height)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 10, detailed: true);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false) { Width = width, Height = height };
        window.Show();
        try
        {
            Assert.That(window.FindControl<ScrollViewer>("PreviewScroller")!.Viewport.Height, Is.GreaterThan(height * 0.6));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void SidebarSaveOpensPresetEditorAndPersistsTheCurrentSettings()
    {
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        var model = new MainViewModel(store);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.Dpi = 600;
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<Button>("ManagePresetsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<TextBox>("PresetNameBox")!.Text = "Receipts";

            window.FindControl<Button>("SavePresetButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var saved = store.Load().Preferences.Presets.Single();
            Assert.That((saved.Name, saved.Settings.Dpi), Is.EqualTo(("Receipts", 600)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void FooterProgressUsesACompactTrackInsteadOfTheDefaultLargeBar()
    {
        var model = new MainViewModel { Busy = true, ExportProgress = true, Progress = 0.45 };
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var progress = window.FindControl<ProgressBar>("OperationProgress")!;

            Assert.That(progress.Bounds.Size, Is.EqualTo(new Size(112, 4)));
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(760)]
    [TestCase(1280)]
    public void FooterPrintAndExportActionsShareTheSameBaseline(int width)
    {
        var window = new MainWindow(new MainViewModel(), discover: false) { Width = width, Height = 680 };
        window.Show();
        try
        {
            var print = window.FindControl<Button>("PrintButton")!;
            var export = window.FindControl<Button>("CombinedExportButton")!;
            var printBottom = print.TranslatePoint(new Point(0, print.Bounds.Height), window)!.Value.Y;
            var exportBottom = export.TranslatePoint(new Point(0, export.Bounds.Height), window)!.Value.Y;

            Assert.That(printBottom, Is.EqualTo(exportBottom).Within(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(760)]
    [TestCase(1280)]
    public void BusyFooterKeepsProgressBesideTheOperationStatus(int width)
    {
        var model = new MainViewModel { Busy = true, ExportProgress = true, Progress = 0.45, Status = "Exporting document…" };
        var window = new MainWindow(model, discover: false) { Width = width, Height = 680 };
        window.Show();
        try
        {
            var progress = window.FindControl<ProgressBar>("OperationProgress")!;
            var status = window.FindControl<TextBlock>("OperationStatus")!;
            var progressCenter = progress.TranslatePoint(new Point(0, progress.Bounds.Height / 2), window)!.Value.Y;
            var statusCenter = status.TranslatePoint(new Point(0, status.Bounds.Height / 2), window)!.Value.Y;

            Assert.That(progressCenter, Is.EqualTo(statusCenter).Within(1));
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(1280)]
    [TestCase(845)]
    public void StandardHeightShowsPageSizeWithoutScrollingSidebar(int width)
    {
        var model = new MainViewModel();
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        var window = new MainWindow(model, discover: false) { Width = width, Height = 900 };
        window.Show();
        try
        {
            var size = window.FindControl<ComboBox>("SizePicker")!;
            var scroller = window.FindControl<ScrollViewer>("ScanSettingsScroller")!;
            var bottom = size.TranslatePoint(new Point(0, size.Bounds.Height), scroller)!.Value.Y;

            Assert.That(bottom, Is.LessThanOrEqualTo(scroller.Bounds.Height));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void SettingsButtonOpensMenuInNarrowWindow()
    {
        var window = new MainWindow(new MainViewModel(), discover: false) { Width = 760, Height = 680 };
        window.Show();
        try
        {
            var settings = window.FindControl<Button>("SettingsButton")!;
            var center = settings.TranslatePoint(new Point(settings.Bounds.Width / 2, settings.Bounds.Height / 2), window)!.Value;

            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);

            Assert.That(settings.Flyout!.IsOpen, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void AutoDeskewChangedInSettingsAppliesToSubsequentScanAfterDismissal()
    {
        var model = new MainViewModel();
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var settings = window.FindControl<Button>("SettingsButton")!;
            settings.Flyout!.ShowAt(settings);

            window.FindControl<CheckBox>("AutoDeskewCheckBox")!.IsChecked = true;
            settings.Flyout.Hide();

            Assert.That(model.CurrentScanSettings().AutoDeskew, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ScannerGeometryControlsValidateInSettingsAndRespectBusyState()
    {
        var model = new MainViewModel();
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SetSession(CreateSession());
        var window = new MainWindow(model, discover: false) { Width = 760, Height = 680 };
        window.Show();
        try
        {
            var settings = window.FindControl<Button>("SettingsButton")!;
            settings.Flyout!.ShowAt(settings);
            window.FindControl<Expander>("ScanGeometryEditor")!.IsExpanded = true;
            window.FindControl<Expander>("ManualDeviceEditor")!.IsExpanded = true;
            model.SelectedSize = model.Sizes.Single(s => s.Label == "Custom");
            window.FindControl<TextBox>("PaperWidthBox")!.Text = "100";
            window.FindControl<CheckBox>("SelectScanAreaCheckBox")!.IsChecked = true;
            window.FindControl<TextBox>("AreaWidthBox")!.Text = "50";
            window.FindControl<TextBox>("AreaXBox")!.Text = "60";
            Assert.That(window.FindControl<Button>("ScanButton")!.IsEffectivelyEnabled, Is.False);
            Assert.That(window.FindControl<TextBlock>("GeometryError")!.IsVisible, Is.True);
            window.FindControl<TextBox>("AreaXBox")!.Text = "10";
            Assert.That(model.CurrentScanSettings().Area!.X, Is.EqualTo(10));
            Assert.That(window.FindControl<Button>("ScanButton")!.IsEffectivelyEnabled, Is.True);
            window.UpdateLayout();
            foreach (var name in new[] { "PaperWidthBox", "AreaXBox", "AlignmentPicker", "AddManualDeviceButton", "RemoveManualDeviceButton", "ScanGeometryPreview" })
            {
                var control = window.FindControl<Control>(name)!;
                var menu = window.FindControl<ScrollViewer>("SettingsMenu")!;
                var left = control.TranslatePoint(new Point(0, 0), menu)!.Value.X;
                var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), menu)!.Value.X;
                Assert.That(left, Is.GreaterThanOrEqualTo(0), name);
                Assert.That(right, Is.LessThanOrEqualTo(menu.Bounds.Width), name);
                Assert.That(control.Bounds.Width, Is.GreaterThan(30), name);
            }
            model.Busy = true;
            Assert.That(window.FindControl<TextBox>("AreaXBox")!.IsEffectivelyEnabled, Is.False);
            Assert.That(window.FindControl<Button>("AddManualDeviceButton")!.IsEffectivelyEnabled, Is.False);
            Assert.That(window.FindControl<Button>("CancelButton")!.IsEffectivelyEnabled, Is.True);
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    public void RenderScannerGeometry()
    {
        var model = new MainViewModel();
        model.SetDevices([ScanTestData.Device("genesys:libusb:001:004", "USB scanner fixture", Driver.Sane)]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SelectedSize = model.Sizes.Single(s => s.Label == "Custom");
        model.PaperWidth = "100"; model.PaperHeight = "150";
        model.SelectedAlignment = MainViewModel.AlignmentChoices[1];
        model.SelectScanArea = true;
        model.AreaWidth = "80"; model.AreaHeight = "120"; model.AreaX = "10"; model.AreaY = "15";
        var window = new MainWindow(model, discover: false) { Width = 760, Height = 680 };
        window.Show();
        var settings = window.FindControl<Button>("SettingsButton")!;
        settings.Flyout!.ShowAt(settings);
        window.FindControl<Expander>("ScanGeometryEditor")!.IsExpanded = true;
        window.UpdateLayout();
        window.FindControl<Control>("ScanGeometryPreview")!.BringIntoView();
        window.UpdateLayout();
        try
        {
            var menu = window.FindControl<ScrollViewer>("SettingsMenu")!;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)menu.Bounds.Width, (int)menu.Bounds.Height), new Vector(96, 96));
            bitmap.Render(menu);
            var folder = Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR");
            if (folder is not null) { Directory.CreateDirectory(folder); bitmap.Save(Path.Combine(folder, "scanner-geometry.png"), new PngBitmapEncoderOptions()); }
            Assert.That(model.GeometryDiagram!.Area, Is.EqualTo(new ScanRegion(10, 15, 80, 120)));
            Assert.That(model.HasValidScanSettings, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void SessionWithPagesEnablesExport()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 2);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);

        window.Show();

        try { Assert.That(model.CanExport, Is.True); }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void BusySessionDisablesPhysicalScanning()
    {
        using var model = new MainViewModel { Busy = true };

        Assert.That(model.CanScan, Is.False);
    }

    [AvaloniaTest]
    public void CancelButtonStaysEnabledDuringAnOperation()
    {
        var model = new MainViewModel { Busy = true };
        var window = new MainWindow(model, discover: false);
        window.Show();

        try { Assert.That(window.FindControl<Button>("CancelButton")!.IsEffectivelyEnabled, Is.True); }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public void SearchablePdfIsOptionalAndLanguageControlsFollowBusyState()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var checkbox = window.FindControl<CheckBox>("SearchablePdfCheckBox")!;
            var languages = window.FindControl<ComboBox>("OcrLanguageSelector")!;
            Assert.That(checkbox.IsChecked, Is.False);
            Assert.That(languages.IsEffectivelyEnabled, Is.False);
            model.SetOcrAvailability(new(true, ["eng", "fra", "nld"], "Ready"));
            checkbox.IsChecked = true;
            Assert.That(model.SearchablePdf, Is.True);
            Assert.That(languages.IsEffectivelyEnabled, Is.True);
            Assert.That(languages.SelectedItem, Is.EqualTo("eng"));
            languages.SelectedItem = "nld";
            Assert.That(model.OcrLanguage, Is.EqualTo("nld"));
            model.Busy = true;
            model.ExportProgress = true;
            Assert.That(checkbox.IsEffectivelyEnabled, Is.False);
            Assert.That(languages.IsEffectivelyEnabled, Is.False);
            Assert.That(model.IndeterminateProgress, Is.False);
            Assert.That(window.FindControl<Button>("CancelButton")!.IsEffectivelyEnabled, Is.True);
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public void OcrLanguageRefreshPreservesSelectionAndClearsUnavailableLanguage()
    {
        using var model = new MainViewModel();
        model.SetOcrAvailability(new(true, ["eng", "nld"], "Ready"));
        model.OcrLanguage = "nld";
        model.SetOcrAvailability(new(true, ["eng", "nld", "fra"], "Ready"));
        Assert.That(model.OcrLanguage, Is.EqualTo("nld"));
        model.SetOcrAvailability(new(false, [], "Install Tesseract"));
        Assert.That(model.OcrLanguage, Is.Null);
        Assert.That(model.OcrStatus, Is.EqualTo("Install Tesseract"));
    }

    [AvaloniaTest]
    public void RotateShortcutSavesTheSelectedPageRotation()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();

        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);

        try { Assert.That(session.Pages[0].Rotation, Is.EqualTo(90)); }
        finally { window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    public async Task ImportedPdfHasThumbnailPreviewAndUsesExistingEditShortcuts()
    {
        var input = Path.Combine(Root, "document.pdf");
        PdfImportTests.WritePdfFile(input, "FIRST", "SECOND");
        var session = CreateSession();
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        var model = new MainViewModel();
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            Assert.That(model.Preview, Is.Not.Null);
            Assert.That(model.Pages[0].Thumbnail.PixelSize.Height, Is.GreaterThan(0));
            Assert.That(model.PreviewCaption, Does.Contain("PDF source preserved"));
            var original = session.Pages[0];
            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
            Assert.That(session.Pages[0].Rotation, Is.EqualTo(90));
            Assert.That(model.Preview!.PixelSize.Width, Is.GreaterThan(model.Preview.PixelSize.Height));
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
            Assert.That(session.Pages[1].PdfSource, Is.EqualTo(original.PdfSource));
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Assert.That(model.Pages.Count, Is.EqualTo(1));
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(model.Pages.Count, Is.EqualTo(2));
            Assert.That(session.Pages[1].PdfSource, Is.EqualTo(original.PdfSource));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void TiledWindowKeepsExportControlsWithinItsBounds()
    {
        foreach (var width in new[] { 845, 760 })
        {
            var window = new MainWindow(new MainViewModel(), discover: false) { Width = width, Height = 680 };
            window.Show();
            var button = window.FindControl<Button>("CombinedExportButton")!;

            var rightEdge = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;

            var languages = window.FindControl<ComboBox>("OcrLanguageSelector")!;
            var languageBottom = languages.TranslatePoint(new Point(0, languages.Bounds.Height), window)!.Value.Y;
            try
            {
                Assert.That(rightEdge, Is.LessThanOrEqualTo(window.ClientSize.Width));
                Assert.That(languageBottom, Is.LessThanOrEqualTo(window.ClientSize.Height));
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task DuplexReviewUsesPreviewOrderAndRequiresFreshConfirmation()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        session.BeginManualDuplex();
        await session.ScanDuplexPass(false, _ => { AddPages(session, 2); return Task.CompletedTask; }, CancellationToken.None);
        await session.ScanDuplexPass(true, _ => { AddPages(session, 2); return Task.CompletedTask; }, CancellationToken.None);
        var raw = session.Pages.ToArray();
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            Assert.That(model.CanScan, Is.False);
            Assert.That(model.CanExport, Is.False);
            Assert.That(model.CanUndo, Is.False);
            Assert.That(window.FindControl<Button>("ApplyDuplexButton")!.IsEffectivelyEnabled, Is.False);
            Assert.That(model.Pages.Select(page => page.Caption), Is.EqualTo(new[] { "Page 1", "Front 1", "Back 1", "Front 2", "Back 2" }));
            model.DuplexReviewed = true;
            Assert.That(model.CanApplyDuplex, Is.True);
            model.ReverseBacks = true;
            model.BackRotation = 180;
            Assert.That(model.DuplexReviewed, Is.False, "Changing a review option invalidates confirmation.");
            Assert.That(session.OriginalPath(model.Pages[2].Page), Is.EqualTo(session.OriginalPath(raw[4])));
            Assert.That(model.Pages[2].Page.Rotation, Is.EqualTo(180));
            model.SelectedPage = model.Pages[2];
            using (var expected = PageRenderer.Png(session, model.Pages[2].Page))
            using (var actual = new MemoryStream())
            {
                model.Preview!.Save(actual, new PngBitmapEncoderOptions());
                actual.Position = 0;
                using var expectedBitmap = SkiaSharp.SKBitmap.Decode(expected);
                using var actualBitmap = SkiaSharp.SKBitmap.Decode(actual);
                Assert.That(actualBitmap.Pixels, Is.EqualTo(expectedBitmap.Pixels));
            }
            Assert.That(session.Pages, Is.EqualTo(raw));
            window.FindControl<CheckBox>("DuplexReviewedCheck")!.IsChecked = true;
            Assert.That(model.CanApplyDuplex, Is.True);
            window.FindControl<Button>("ApplyDuplexButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(session.ManualDuplex, Is.Null);
            Assert.That(session.Pages[2], Is.EqualTo(raw[4] with { Rotation = 180 }));
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(session.Pages, Is.EqualTo(raw));
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.That(session.Pages[2], Is.EqualTo(raw[4] with { Rotation = 180 }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task MismatchedDuplexReviewBlocksApplyEvenWhenConfirmed()
    {
        using var model = new MainViewModel();
        var session = CreateSession();
        session.BeginManualDuplex();
        await session.ScanDuplexPass(false, _ => { AddPages(session, 2); return Task.CompletedTask; }, CancellationToken.None);
        await session.ScanDuplexPass(true, _ => { AddPages(session, 1); return Task.CompletedTask; }, CancellationToken.None);
        model.SetSession(session);
        model.DuplexReviewed = true;
        Assert.That(model.CanApplyDuplex, Is.False);
        Assert.That(model.DuplexCounts, Does.Contain("Apply blocked"));
        model.ApplyDuplex();
        Assert.That(session.ManualDuplex, Is.Not.Null);
        model.KeepDuplexScanOrder();
        Assert.That(model.Pages.Count, Is.EqualTo(3));
        Assert.That(model.CanExport, Is.True);
    }

    [AvaloniaTest]
    public async Task FailedReviewOptionSaveClearsConfirmationAndKeepsSavedOptions()
    {
        using var model = new MainViewModel();
        var session = CreateSession();
        session.BeginManualDuplex();
        await session.ScanDuplexPass(false, _ => { AddPages(session, 1); return Task.CompletedTask; }, CancellationToken.None);
        await session.ScanDuplexPass(true, _ => { AddPages(session, 1); return Task.CompletedTask; }, CancellationToken.None);
        model.SetSession(session);
        model.DuplexReviewed = true;
        var manifest = Path.Combine(session.DirectoryPath, "session.json");
        File.Move(manifest, manifest + ".backup");
        Directory.CreateDirectory(manifest);
        try
        {
            model.ReverseBacks = true;
            Assert.That(model.HasError, Is.True);
            Assert.That(model.ReverseBacks, Is.False);
            Assert.That(model.DuplexReviewed, Is.False);
            Assert.That(model.CanApplyDuplex, Is.False);
        }
        finally
        {
            Directory.Delete(manifest);
            File.Move(manifest + ".backup", manifest);
        }
    }

    [AvaloniaTest]
    public void ManualDuplexIsOfferedOnlyForSingleSidedFeeder()
    {
        using var model = new MainViewModel();
        model.SetSession(CreateSession());
        model.SetDevices([ScanTestData.Device()]);
        Assert.That(model.CanStartDuplex, Is.False, "Capability discovery must finish before scanning.");
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SelectedSource = model.Sources.Single(source => source.Value == PaperSource.Flatbed);
        Assert.That(model.CanStartDuplex, Is.False);
        model.SelectedSource = model.Sources.Single(source => source.Value == PaperSource.Duplex);
        Assert.That(model.CanStartDuplex, Is.False);
        model.SelectedSource = model.Sources.Single(source => source.Value == PaperSource.Feeder);
        Assert.That(model.CanStartDuplex, Is.True);
        model.BeginDuplex();
        model.Busy = true;
        Assert.That(model.CanScanFronts, Is.False);
        Assert.That(model.CanScanBacks, Is.False);
    }

    [AvaloniaTest, Category("Integration")]
    [TestCase(1280, 900)]
    [TestCase(845, 900)]
    public async Task RenderManualDuplexReview(int width, int height)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1, detailed: true);
        session.BeginManualDuplex();
        await session.ScanDuplexPass(false, _ => { AddPages(session, 2, detailed: true); return Task.CompletedTask; }, CancellationToken.None);
        await session.ScanDuplexPass(true, _ => { AddPages(session, 2, detailed: true); return Task.CompletedTask; }, CancellationToken.None);
        model.SetSession(session);
        model.ReverseBacks = true;
        model.BackRotation = 180;
        model.SelectedPage = model.Pages[2];
        var window = new MainWindow(model, discover: false) { Width = width, Height = height };
        window.Show();
        window.UpdateLayout();
        try
        {
            var apply = window.FindControl<Button>("ApplyDuplexButton")!;
            var edge = apply.TranslatePoint(new Point(apply.Bounds.Width, apply.Bounds.Height), window)!.Value;
            Assert.That(edge.X, Is.LessThanOrEqualTo(window.ClientSize.Width));
            Assert.That(edge.Y, Is.LessThan(window.ClientSize.Height - 104));
            Assert.That(window.FindControl<ListBox>("PageTray")!.IsEffectivelyEnabled, Is.True);
            Assert.That(window.FindControl<Image>("SelectedPageImage")!.Bounds.Height, Is.GreaterThan(200),
                "Review controls must leave room to inspect the selected side.");
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            var folder = Environment.GetEnvironmentVariable("ESYUSCAN_DUPLEX_SCREENSHOT_DIR");
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"manual-duplex-{width}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task ThemeDirectoryReplacementUpdatesTheRunningApp()
    {
        var themePath = Path.Combine(Root, ".local/state/omarchy/current/theme");
        Directory.CreateDirectory(themePath);
        var path = Path.Combine(themePath, "colors.toml");
        await File.WriteAllTextAsync(path, "background = \"#111111\"\nforeground = \"#eeeeee\"\naccent = \"#55aa88\"");
        using var theme = new OmarchyTheme(Root);
        await theme.Refresh();
        Directory.Delete(themePath, recursive: true);
        Directory.CreateDirectory(themePath);
        await File.WriteAllTextAsync(path, "background = \"#ffffff\"\nforeground = \"#111111\"\naccent = \"#2255bb\"");

        await theme.Refresh();

        Assert.That(((SolidColorBrush)Application.Current!.Resources["AccentBrush"]!).Color, Is.EqualTo(Color.Parse("#2255bb")));
    }

    [AvaloniaTest]
    public void ScannerPickersPreserveRestoredSettingsAndPersistUserChanges()
    {
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        store.Save(new(1, ScanTestData.Settings(), []));
        var model = new MainViewModel(store);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            model.SetDevices([ScanTestData.Device()]);
            Assert.That(window.FindControl<ComboBox>("SourcePicker")!.IsEffectivelyEnabled, Is.False);
            model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
            Dispatcher.UIThread.RunJobs();
            var color = window.FindControl<ComboBox>("ColorPicker")!;
            var dpi = window.FindControl<ComboBox>("DpiPicker")!;
            Assert.Multiple(() =>
            {
                Assert.That(((ScanChoice<BitDepth>)color.SelectedItem!).Value, Is.EqualTo(BitDepth.Color));
                Assert.That(dpi.SelectedItem, Is.EqualTo(600));
                Assert.That(((ScanChoice<PageSize>)window.FindControl<ComboBox>("SizePicker")!.SelectedItem!).Label, Is.EqualTo("Letter"));
            });
            window.FindControl<ComboBox>("SourcePicker")!.SelectedItem = model.Sources.Single(s => s.Value == PaperSource.Duplex);
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(model.SelectedColor!.Value, Is.EqualTo(BitDepth.Color));
                Assert.That(model.Dpi, Is.EqualTo(600));
            });
            dpi.SelectedItem = 150;
            Dispatcher.UIThread.RunJobs();
            model.SetDevices([ScanTestData.Device()]);
            model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(dpi.SelectedItem, Is.EqualTo(150));
                Assert.That(StoreLast().Source, Is.EqualTo("Duplex"));
                Assert.That(StoreLast().Dpi, Is.EqualTo(150));
            });
            RememberedScanSettings StoreLast() => store.Load().Preferences.LastScan!;
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void PresetControlsSaveRenameUpdateDeleteAndDisableDuringWork()
    {
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        var model = new MainViewModel(store);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var settings = window.FindControl<Button>("SettingsButton")!;
            settings.Flyout!.ShowAt(settings);
            window.FindControl<Expander>("PresetEditor")!.IsExpanded = true;
            var name = window.FindControl<TextBox>("PresetNameBox")!;
            name.Text = "Receipts";
            Click("SavePresetButton");
            Assert.That(model.Presets.Single().Name, Is.EqualTo("Receipts"));
            name.Text = "Archive";
            Click("RenamePresetButton");
            Assert.That(model.SelectedPreset!.Name, Is.EqualTo("Archive"));
            window.FindControl<ComboBox>("DpiPicker")!.SelectedItem = 600;
            Click("UpdatePresetButton");
            Assert.That(store.Load().Preferences.Presets.Single().Settings.Dpi, Is.EqualTo(600));
            model.Busy = true;
            Dispatcher.UIThread.RunJobs();
            foreach (var control in new[] { "SavePresetButton", "RenamePresetButton", "UpdatePresetButton", "DeletePresetButton", "ApplyPresetButton" })
                Assert.That(window.FindControl<Button>(control)!.IsEffectivelyEnabled, Is.False, control);
            model.Busy = false;
            Click("DeletePresetButton");
            Assert.Multiple(() =>
            {
                Assert.That(store.Load().Preferences.Presets, Is.Empty);
                Assert.That(window.FindControl<Button>("ApplyPresetButton")!.IsEffectivelyEnabled, Is.False);
            });
            void Click(string control)
            {
                Dispatcher.UIThread.RunJobs();
                window.FindControl<Button>(control)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public void SmallWindowKeepsScanAndCancelVisibleWithSettingsOpen()
    {
        var model = new MainViewModel { Busy = true };
        var window = new MainWindow(model, discover: false) { Width = 845, Height = 680 };
        window.Show();
        try
        {
            var settings = window.FindControl<Button>("SettingsButton")!;
            settings.Flyout!.ShowAt(settings);
            window.FindControl<Expander>("PresetEditor")!.IsExpanded = true;
            window.UpdateLayout();
            foreach (var control in new[] { "ScanButton", "CancelButton" })
            {
                var button = window.FindControl<Button>(control)!;
                var top = button.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), window)!.Value.Y;
                Assert.That(top, Is.GreaterThan(84), control);
                Assert.That(bottom, Is.LessThanOrEqualTo(window.ClientSize.Height - 104), control);
            }
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public void PreferenceFallbackIsVisibleAndScanStaysDisabledUntilCapabilitiesLoad()
    {
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        store.Save(new(1, ScanTestData.Settings(), []));
        var model = new MainViewModel(store);
        model.SetSession(CreateSession());
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            model.SetDevices([ScanTestData.Device("missing-replacement")]);
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(window.FindControl<TextBlock>("SettingsExplanation")!.IsVisible, Is.True);
                Assert.That(window.FindControl<TextBlock>("SettingsExplanation")!.Text, Does.Contain("unavailable"));
                Assert.That(window.FindControl<Button>("ScanButton")!.IsEffectivelyEnabled, Is.False);
            });
            model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.FindControl<Button>("ScanButton")!.IsEffectivelyEnabled, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task CropDialogCancelApplyAndUndoPreserveOriginal()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        session.Rotate(0);
        session.SetAdjustments(0, AdjustmentTests.Combined);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var original = session.Pages[0];
            window.FindControl<Button>("CropButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var editor = window.OwnedWindows.OfType<CropWindow>().Single();
            editor.FindControl<NumericUpDown>("CropLeft")!.Value = 5;
            editor.FindControl<NumericUpDown>("CropTop")!.Value = 6;
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = 40;
            editor.FindControl<NumericUpDown>("CropHeight")!.Value = 30;
            Assert.That(editor.Result, Is.EqualTo(new PageCrop(6, 43, 30, 40)));
            using (var expectedPng = PageRenderer.Png(session, original with { Crop = editor.Result }))
            using (var actualPng = new MemoryStream())
            {
                ((Bitmap)editor.FindControl<Image>("CropResultPreview")!.Source!).Save(actualPng, new PngBitmapEncoderOptions());
                actualPng.Position = 0;
                using var expectedBitmap = SkiaSharp.SKBitmap.Decode(expectedPng);
                using var actualBitmap = SkiaSharp.SKBitmap.Decode(actualPng);
                Assert.That(actualBitmap.Pixels, Is.EqualTo(expectedBitmap.Pixels));
            }
            editor.FindControl<Button>("CancelCrop")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.That(session.Pages[0], Is.EqualTo(original));

            window.FindControl<Button>("CropButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            editor = window.OwnedWindows.OfType<CropWindow>().Single();
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = 0;
            Assert.That(editor.FindControl<Button>("ApplyCrop")!.IsEnabled, Is.False);
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = 40;
            editor.FindControl<NumericUpDown>("CropHeight")!.Value = 30;
            var expected = editor.Result;
            editor.FindControl<Button>("ApplyCrop")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.That(session.Pages[0].Crop, Is.EqualTo(expected));
            Assert.That(model.Preview!.PixelSize, Is.EqualTo(new PixelSize(40, 30)));
            session.Undo();
            model.SyncPages();
            Assert.That(session.Pages[0], Is.EqualTo(original));
            Assert.That(model.Preview!.PixelSize, Is.EqualTo(new PixelSize(88, 62)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void PreviewPixelsAndThumbnailAspectMatchSharedRenderer()
    {
        using var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        session.SetDeskew(0, -3);
        session.SetCrop(0, new PageCrop(2, 4, 30, 70));
        session.SetAdjustments(0, AdjustmentTests.Combined);
        session.Rotate(0);
        model.SetSession(session);
        using var expected = PageRenderer.Png(session, session.Pages[0]);
        using var actual = new MemoryStream();
        model.Preview!.Save(actual, new PngBitmapEncoderOptions());
        actual.Position = 0;
        using var expectedBitmap = SkiaSharp.SKBitmap.Decode(expected);
        using var actualBitmap = SkiaSharp.SKBitmap.Decode(actual);
        Assert.That(actualBitmap.Pixels, Is.EqualTo(expectedBitmap.Pixels));
        var thumbnail = model.Pages[0].Thumbnail;
        Assert.That((double)thumbnail.PixelSize.Width / thumbnail.PixelSize.Height,
            Is.EqualTo(70.0 / 30).Within(0.02));
    }

    [AvaloniaTest]
    public void CropSelectionDragUsesImagePixelsAndClampsAtPageEdges()
    {
        using var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new CropWindow(session, session.Pages[0]);
        window.Show();
        window.UpdateLayout();
        try
        {
            var selection = window.FindControl<CropSelection>("CropSelection")!;
            var center = selection.TranslatePoint(new Point(selection.Bounds.Width / 2, selection.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(new Point(window.ClientSize.Width, window.ClientSize.Height));
            window.MouseUp(new Point(window.ClientSize.Width, window.ClientSize.Height), MouseButton.Left);
            Assert.That(window.Result, Is.EqualTo(new PageCrop(31, 44, 31, 44)));
            window.FindControl<Button>("ResetCrop")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(window.Result, Is.Null);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void NarrowWindowKeepsPageToolbarInsidePreview()
    {
        var window = new MainWindow(new MainViewModel(), discover: false) { Width = 760, Height = 900 };
        window.Show();
        window.UpdateLayout();
        try
        {
            var button = window.FindControl<Button>("RemoveButton")!;
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
            Assert.That(right, Is.LessThanOrEqualTo(window.ClientSize.Width - 28));
            var toolbar = window.FindControl<Panel>("PageToolbar")!;
            Assert.That(toolbar.TranslatePoint(new Point(0, 0), window)!.Value.X, Is.GreaterThanOrEqualTo(268));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    public void RenderCropEditor()
    {
        using var session = CreateSession();
        AddPages(session, 1, detailed: true);
        session.SetCrop(0, new PageCrop(40, 50, 540, 760));
        var window = new CropWindow(session, session.Pages[0]);
        window.Show();
        window.UpdateLayout();
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(980, 760), new Vector(96, 96));
            bitmap.Render(window);
            var folder = Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR");
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, "crop.png"), new PngBitmapEncoderOptions());
            }
            var apply = window.FindControl<Button>("ApplyCrop")!;
            Assert.That(apply.IsEffectivelyEnabled, Is.True);
            var bottom = apply.TranslatePoint(new Point(0, apply.Bounds.Height), window)!.Value.Y;
            Assert.That(bottom, Is.LessThanOrEqualTo(window.ClientSize.Height));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    [TestCase(1280, 900, false)]
    [TestCase(1280, 900, true)]
    [TestCase(760, 680, false)]
    [TestCase(760, 680, true)]
    public void RenderDocumentWorkspace(int width, int height, bool busy)
    {
        OmarchyTheme.Apply(ThemePalette.Default with { Accent = "#a78bda" });
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 8, detailed: true);
        model.SetSession(session);
        model.SetDevices([ScanTestData.Device("demo", "AirScan document scanner")]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.PresetName = "Office documents";
        model.SavePreset();
        model.Status = busy ? "Exporting document… 4 of 8 pages" : "8 pages saved. Choose separate files or one document.";
        model.SetOcrAvailability(new(true, ["eng"], "OCR ready · 1 language: eng"));
        model.SearchablePdf = true;
        model.Busy = busy;
        model.ExportProgress = true;
        model.Progress = 0.45;
        var window = new MainWindow(model, discover: false) { Width = width, Height = height };
        window.Show();
        window.UpdateLayout();
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            var folder = Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR");
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"esyuscan-{width}-{(busy ? "busy" : "ready")}.png"), new PngBitmapEncoderOptions());
            }
            Assert.That(window.FindControl<ListBox>("PageTray")!.ItemCount, Is.EqualTo(8));
        }
        finally { model.Busy = false; window.Close(); }
    }
}
