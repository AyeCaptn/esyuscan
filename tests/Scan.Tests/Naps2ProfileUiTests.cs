using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class Naps2ProfileUiTests : TestFiles
{
    private string WriteProfiles(string xml = Naps2TestData.FourProfiles)
    {
        var path = Path.Combine(Root, "profiles.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private static void Click(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static CheckBox[] Choices(Window window) => window.FindControl<StackPanel>("Naps2ProfileList")!
        .GetVisualDescendants().OfType<CheckBox>().ToArray();

    [AvaloniaTest]
    public void MainWindowOpensImportAndBusyStateDisablesEntry()
    {
        var model = new MainViewModel();
        var owner = new MainWindow(model, discover: false);
        owner.Show();
        try
        {
            var settings = owner.FindControl<Button>("SettingsButton")!;
            settings.Flyout!.ShowAt(settings);
            owner.FindControl<Expander>("PresetEditor")!.IsExpanded = true;
            model.Busy = true;
            Dispatcher.UIThread.RunJobs();
            Assert.That(owner.FindControl<Button>("ImportNaps2ProfilesButton")!.IsEffectivelyEnabled, Is.False);
            model.Busy = false;
            Click(owner, "ImportNaps2ProfilesButton");
            var dialog = owner.OwnedWindows.OfType<Naps2ProfileImportWindow>().Single();
            Assert.That(dialog.FindControl<Button>("BrowseNaps2Profiles")!.IsEffectivelyEnabled, Is.True);
            Click(dialog, "CancelNaps2Import");
            Assert.That(model.Presets, Is.Empty);
        }
        finally { model.Busy = false; owner.Close(); }
    }

    [AvaloniaTest]
    public void ApplyingMissingImportedPresetLeavesScannerPickerEmptyWithoutQueryingHardware()
    {
        var model = new MainViewModel();
        model.SetSession(CreateSession());
        model.SetDevices([ScanTestData.Device("unrelated")]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.ImportProfiles(Naps2TestData.Read().Take(1), ProfileDuplicatePolicy.Skip);
        model.SelectedPreset = model.Presets.Single();
        var owner = new MainWindow(model, discover: false);
        owner.Show();
        try
        {
            Click(owner, "ApplyPresetButton");
            Assert.That(model.SelectedDevice, Is.Null);
            Assert.That(owner.FindControl<ComboBox>("DevicePicker")!.SelectedItem, Is.Null);
            Assert.That(model.CanScan, Is.False);
            Assert.That(model.SettingsExplanation, Does.Contain("unavailable"));
            Assert.That(model.Status, Does.Contain("explicitly"));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task FourProfilePreviewImportsOnlyAfterReviewWithoutApplyingSettings()
    {
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        store.Save(new(1, ScanTestData.Settings(), []));
        var model = new MainViewModel(store);
        var owner = new MainWindow(model, discover: false);
        owner.Show();
        var path = WriteProfiles();
        var dialog = new Naps2ProfileImportWindow(model, [path]);
        var result = dialog.ShowDialog<int>(owner);
        try
        {
            Assert.That(dialog.FindControl<ComboBox>("Naps2Locations")!.SelectedItem, Is.EqualTo(path));
            Assert.That(dialog.FindControl<Button>("LoadNaps2Profiles")!.IsEnabled, Is.True);
            await dialog.LoadFileAsync(path);
            Dispatcher.UIThread.RunJobs();
            Assert.That(Choices(dialog), Has.Length.EqualTo(4));
            Assert.That(Choices(dialog).All(c => c.IsChecked == true), Is.True);
            var text = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("600 DPI").And.Contain("Device ID").And.Contain("Old USB scanner"));
            Assert.That(model.Presets, Is.Empty);
            Assert.That(dialog.FindControl<Button>("ImportSelectedNaps2Profiles")!.IsEnabled, Is.False);
            dialog.FindControl<CheckBox>("Naps2ImportReviewed")!.IsChecked = true;
            Assert.That(dialog.FindControl<Button>("ImportSelectedNaps2Profiles")!.IsEnabled, Is.True);
            Click(dialog, "ImportSelectedNaps2Profiles");
            Assert.That(await result, Is.EqualTo(4));
            Assert.That(model.Presets, Has.Count.EqualTo(4));
            Assert.That(model.SelectedPreset, Is.Null);
            Assert.That(store.Load().Preferences.LastScan, Is.EqualTo(ScanTestData.Settings()));
            Assert.That(File.ReadAllText(path), Is.EqualTo(Naps2TestData.FourProfiles));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task SelectionAndDuplicatePolicyUpdatePreviewAndCancelDoesNotWrite()
    {
        using var model = new MainViewModel();
        model.ImportProfiles(Naps2TestData.Read().Take(1), ProfileDuplicatePolicy.Skip);
        var dialog = new Naps2ProfileImportWindow(model, []);
        dialog.Show();
        try
        {
            await dialog.LoadFileAsync(WriteProfiles());
            Dispatcher.UIThread.RunJobs();
            var choices = Choices(dialog);
            foreach (var choice in choices.Skip(1)) choice.IsChecked = false;
            dialog.FindControl<CheckBox>("Naps2ImportReviewed")!.IsChecked = true;
            Assert.That(dialog.FindControl<Button>("ImportSelectedNaps2Profiles")!.IsEnabled, Is.False);
            dialog.FindControl<ComboBox>("Naps2DuplicatePolicy")!.SelectedIndex = 1;
            Assert.That(dialog.FindControl<Button>("ImportSelectedNaps2Profiles")!.IsEnabled, Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Letters (2)") == true), Is.True);
            dialog.FindControl<ComboBox>("Naps2DuplicatePolicy")!.SelectedIndex = 2;
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Replace ‘Letters’") == true), Is.True);
            Click(dialog, "CancelNaps2Import");
            Assert.That(model.Presets.Single().Name, Is.EqualTo("Letters"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BadFileClearsPreviousPreviewAndBadProfileIsDisabled()
    {
        using var model = new MainViewModel();
        var dialog = new Naps2ProfileImportWindow(model, []);
        dialog.Show();
        try
        {
            var path = WriteProfiles(Naps2TestData.FourProfiles.Replace("<Resolution>Dpi600</Resolution>", "<Resolution>bad</Resolution>"));
            await dialog.LoadFileAsync(path);
            Dispatcher.UIThread.RunJobs();
            Assert.That(Choices(dialog)[0].IsEnabled, Is.False);
            Assert.That(Choices(dialog)[0].IsChecked, Is.False);
            Assert.That(Choices(dialog).Skip(1).All(c => c.IsEnabled), Is.True);
            dialog.FindControl<CheckBox>("Naps2ImportReviewed")!.IsChecked = true;
            File.WriteAllText(path, "<broken");
            await dialog.LoadFileAsync(path);
            Assert.That(dialog.FindControl<StackPanel>("Naps2ProfileList")!.Children, Is.Empty);
            Assert.That(dialog.FindControl<Button>("ImportSelectedNaps2Profiles")!.IsEnabled, Is.False);
            Assert.That(dialog.FindControl<CheckBox>("Naps2ImportReviewed")!.IsChecked, Is.False);
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Could not preview") == true), Is.True);
            Assert.That(model.Presets, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task PersistenceFailureKeepsDialogOpenAndPresetsUnchanged()
    {
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        using var model = new MainViewModel(store);
        var dialog = new Naps2ProfileImportWindow(model, []);
        dialog.Show();
        try
        {
            await dialog.LoadFileAsync(WriteProfiles());
            dialog.FindControl<CheckBox>("Naps2ImportReviewed")!.IsChecked = true;
            Directory.CreateDirectory(store.FilePath);
            Click(dialog, "ImportSelectedNaps2Profiles");
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(model.Presets, Is.Empty);
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Presets were not imported") == true), Is.True);
            Assert.That(Directory.GetFiles(Root, ".preferences-*.tmp"), Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task NarrowImportDialogKeepsActionsVisibleAndRendersPreview()
    {
        using var model = new MainViewModel();
        var dialog = new Naps2ProfileImportWindow(model, []) { Width = 520, Height = 680 };
        dialog.Show();
        try
        {
            await dialog.LoadFileAsync(WriteProfiles());
            dialog.FindControl<CheckBox>("Naps2ImportReviewed")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            foreach (var name in new[] { "BrowseNaps2Profiles", "ImportSelectedNaps2Profiles", "CancelNaps2Import" })
            {
                var control = dialog.FindControl<Button>(name)!;
                var bounds = control.Bounds;
                var position = control.TranslatePoint(default, dialog)!.Value;
                Assert.That(position.X, Is.GreaterThanOrEqualTo(0), name);
                Assert.That(position.Y + bounds.Height, Is.LessThanOrEqualTo(dialog.ClientSize.Height), name);
                Assert.That(position.X + bounds.Width, Is.LessThanOrEqualTo(dialog.ClientSize.Width), name);
            }
            using var bitmap = new RenderTargetBitmap(new PixelSize(520, 680), new Vector(96, 96));
            bitmap.Render(dialog);
            if (Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, "naps2-import.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { dialog.Close(); }
    }
}
