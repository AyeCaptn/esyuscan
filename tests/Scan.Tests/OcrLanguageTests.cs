using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class OcrLanguageTests
{
    [AvaloniaTest]
    public void ChoosingAModelFromExportSetupKeepsItsCodeForThePdfEngine()
    {
        var window = new ExportWindow(ExportFormat.CombinedPdf, false, true, ["eng", "ara"], "eng");
        window.Show();
        try
        {
            window.FindControl<Button>("OcrLanguageSetup")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.OwnedWindows.OfType<OcrLanguagesWindow>().Single().FindControl<Button>("ChooseOcrLanguage_ara")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("AcceptExportOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(window.TakeResult()!.Ocr!.Language, Is.EqualTo("ara"));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void InstalledChoicesSortByReadableNameWithoutAddingMissingModels()
    {
        using var model = new MainViewModel();

        model.SetOcrAvailability(new(true, ["eng", "deu", "sqi", "jpn"], "Fixture availability"));

        Assert.That(model.OcrLanguages, Is.EqualTo(new[] { "sqi", "eng", "deu", "jpn" }));
    }

    [AvaloniaTest]
    public void RefreshPreservesTheExactSelectedScriptVariant()
    {
        using var model = new MainViewModel();
        model.SetOcrAvailability(new(true, ["eng", "srp", "srp_latn"], "Fixture availability"));
        model.OcrLanguage = "srp_latn";

        model.SetOcrAvailability(new(true, ["eng", "srp", "srp_latn", "fra"], "Updated fixture availability"));

        Assert.That(model.OcrLanguage, Is.EqualTo("srp_latn"));
    }

    [AvaloniaTest]
    public void MainPickerDisplaysTheLanguageNameAndRetainsTheModelCode()
    {
        var model = new MainViewModel();
        model.SetOcrAvailability(new(true, ["eng", "srp_latn"], "Fixture availability"));
        model.OcrLanguage = "srp_latn";
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var picker = window.FindControl<ComboBox>("OcrLanguageSelector")!;

            Assert.That(picker.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text), Does.Contain("Serbian (Latin) (srp_latn)"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase("sqi")]
    [TestCase("fra")]
    [TestCase("deu_latf")]
    [TestCase("srp")]
    [TestCase("srp_latn")]
    [TestCase("ukr")]
    [TestCase("jpn")]
    [TestCase("ara")]
    [TestCase("chi_sim_vert")]
    [TestCase("hin")]
    [TestCase("private_model")]
    public void ExportPassesTheInstalledModelCodeInsteadOfItsDisplayName(string code)
    {
        var window = new ExportWindow(ExportFormat.CombinedPdf, false, true, [code], code);
        window.Show();
        try
        {
            window.FindControl<Button>("AcceptExportOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(window.TakeResult()!.Ocr!.Language, Is.EqualTo(code));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void MissingModelsCannotBeSelectedFromTheCatalogue()
    {
        var window = new OcrLanguagesWindow(["eng"], "eng");
        window.Show();
        try
        {
            window.FindControl<Button>("ChooseOcrLanguage_fra")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(window.IsVisible, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task ChoosingAnInstalledModelReturnsOnlyThatCode()
    {
        var owner = new Window();
        owner.Show();
        var window = new OcrLanguagesWindow(["eng", "fra", "srp_latn"], "eng");
        var result = window.ShowDialog<string?>(owner);
        try
        {
            window.FindControl<Button>("ChooseOcrLanguage_srp_latn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(await result, Is.EqualTo("srp_latn"));
        }
        finally { window.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public void SearchFindsScriptVariantsByTheirReadableNames()
    {
        var window = new OcrLanguagesWindow(["eng"], "eng");
        window.Show();
        try
        {
            window.FindControl<TextBox>("OcrLanguageSearch")!.Text = "Serbian";
            Dispatcher.UIThread.RunJobs();
            var matches = window.FindControl<StackPanel>("OcrLanguageList")!.Children.Where(row => row.IsVisible)
                .SelectMany(row => row.GetVisualDescendants().OfType<Button>()).Select(button => button.Name);

            Assert.That(matches, Is.EqualTo(new[] { "ChooseOcrLanguage_srp", "ChooseOcrLanguage_srp_latn" }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void SearchFindsNonEuropeanLanguagesByTheirReadableNames()
    {
        var window = new OcrLanguagesWindow(["eng"]);
        window.Show();
        try
        {
            window.FindControl<TextBox>("OcrLanguageSearch")!.Text = "Chinese";
            Dispatcher.UIThread.RunJobs();
            var matches = window.FindControl<StackPanel>("OcrLanguageList")!.Children.Where(row => row.IsVisible)
                .SelectMany(row => row.GetVisualDescendants().OfType<Button>()).Select(button => button.Name);

            Assert.That(matches, Is.EquivalentTo(new[] { "ChooseOcrLanguage_chi_sim", "ChooseOcrLanguage_chi_sim_vert", "ChooseOcrLanguage_chi_tra", "ChooseOcrLanguage_chi_tra_vert" }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void UncataloguedInstalledLanguagesAreStillSelectable()
    {
        var window = new OcrLanguagesWindow(["eng", "jpn", "private_model"]);
        window.Show();
        try
        {
            Assert.That(window.FindControl<Button>("ChooseOcrLanguage_private_model")!.IsEnabled, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void TheCatalogueShortcutCannotBypassAnActiveOperation()
    {
        var model = new MainViewModel { Busy = true };
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.L, RawInputModifiers.Control | RawInputModifiers.Alt);

            Assert.That(window.OwnedWindows, Is.Empty);
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public void TheCatalogueShortcutAppliesAnInstalledLanguageToTheMainPicker()
    {
        var model = new MainViewModel();
        model.SetOcrAvailability(new(true, ["eng", "fra"], "Fixture availability"));
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.L, RawInputModifiers.Control | RawInputModifiers.Alt);
            window.OwnedWindows.OfType<OcrLanguagesWindow>().Single().FindControl<Button>("ChooseOcrLanguage_fra")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(model.OcrLanguage, Is.EqualTo("fra"));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    [TestCase(620, 680)]
    [TestCase(420, 500)]
    public void CatalogueKeepsItsCloseActionVisibleAndRendersInstalledAndMissingModels(int width, int height)
    {
        OmarchyTheme.Apply(ThemePalette.Default);
        var window = new OcrLanguagesWindow(["eng", "fra", "nld"], "eng") { Width = width, Height = height };
        window.Show();
        try
        {
            window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
            bitmap.Render(window);
            if (Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"ocr-languages-{width}.png"), new PngBitmapEncoderOptions());
            }
            var close = window.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel);
            var edge = close.TranslatePoint(new Point(close.Bounds.Width, close.Bounds.Height), window)!.Value;

            Assert.That(new Rect(window.ClientSize).Contains(edge), Is.True);
        }
        finally { window.Close(); }
    }
}
