using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class KeyboardShortcutTests : TestFiles
{
    [AvaloniaTest]
    public void ExportFromASettingsTextFieldDismissesThePopupBeforeOpeningItsDialog()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Alt);
            var root = TopLevel.GetTopLevel(window.FindControl<TextBox>("PresetNameBox")!)!;

            root.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);

            Assert.That(window.FindControl<Button>("SettingsButton")!.Flyout!.IsOpen, Is.False);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void CtrlPOpensPrintOptionsWithoutSubmittingAPrintJob()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var desktop = new FakeDesktopCommand();
        var window = new MainWindow(model, discover: false, desktopCommand: desktop);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);

            Assert.That(desktop.Calls.Select(call => call.Command), Is.EqualTo(new[] { "lpstat" }));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void CtrlDBeginsTheDuplexGuideWithoutStartingAPhysicalScan()
    {
        var model = new MainViewModel();
        model.SetSession(CreateSession());
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SelectedSource = model.Sources.Single(source => source.Value == NAPS2.Scan.PaperSource.Feeder);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);

            Assert.That(model.Session!.ManualDuplex!.Fronts, Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void CtrlEnterInBatchStartsOnlyTheInjectedFakeScanner()
    {
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        var invocations = 0;
        var window = new BatchWindow(session, scanner, (_, _) => { invocations++; return Task.CompletedTask; }, () => { });
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);

            Assert.That(invocations, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void QuestionMarkInATextFieldDoesNotOpenShortcutHelp()
    {
        var window = new MainWindow(new MainViewModel(), discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Alt);
            var root = TopLevel.GetTopLevel(window.FindControl<TextBox>("PresetNameBox")!)!;

            root.KeyPressQwerty(PhysicalKey.Slash, RawInputModifiers.Shift);
            root.KeyTextInput("?");

            Assert.That(window.OwnedWindows, Is.Empty);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void CtrlAInATextFieldDoesNotSelectDocumentPages()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 3);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Alt);
            var root = TopLevel.GetTopLevel(window.FindControl<TextBox>("PresetNameBox")!)!;

            root.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);

            Assert.That(model.Selection.Selected.Count, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void SpaceOnAFocusedButtonActivatesThatButtonInsteadOfTheScanShortcut()
    {
        var window = new MainWindow(new MainViewModel(), discover: false);
        window.Show();
        try
        {
            window.FindControl<Button>("NewSessionButton")!.Focus();

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.That(window.OwnedWindows.Select(dialog => dialog.Title), Is.EqualTo(new[] { "New scan session" }));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    public void RenderShortcutReferenceAtMinimumWindowSize()
    {
        var window = new MainWindow(new MainViewModel(), discover: false) { Width = 760, Height = 680 };
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
            var dialog = window.OwnedWindows.Single();
            dialog.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)dialog.ClientSize.Width, (int)dialog.ClientSize.Height));
            bitmap.Render(dialog);
            if (Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, "keyboard-shortcuts.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            var close = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel);
            var bottom = close.TranslatePoint(new Avalonia.Point(0, close.Bounds.Height), dialog)!.Value.Y;

            Assert.That(bottom, Is.LessThanOrEqualTo(dialog.ClientSize.Height));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.Digit1, "DevicePicker")]
    [TestCase(PhysicalKey.Digit2, "PresetPicker")]
    [TestCase(PhysicalKey.Digit3, "SourcePicker")]
    [TestCase(PhysicalKey.Digit4, "ColorPicker")]
    [TestCase(PhysicalKey.Digit5, "DpiPicker")]
    [TestCase(PhysicalKey.Digit6, "SizePicker")]
    public void AltNumberOpensTheMatchingScannerOption(PhysicalKey key, string name)
    {
        var model = new MainViewModel();
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.ScanSettingsExpanded = false;
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(key, RawInputModifiers.Alt);

            Assert.That(window.FindControl<ComboBox>(name)!.IsDropDownOpen, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.S, RawInputModifiers.Control, "esyuscan · One document")]
    [TestCase(PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift, "esyuscan · Separate PDFs")]
    [TestCase(PhysicalKey.I, RawInputModifiers.Control | RawInputModifiers.Shift, "esyuscan · Separate images")]
    public void ExportShortcutOpensTheRequestedOptionsDialog(PhysicalKey key, RawInputModifiers modifiers, string title)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(key, modifiers);

            Assert.That(window.OwnedWindows.OfType<ExportWindow>().Select(dialog => dialog.Title), Is.EqualTo(new[] { title }));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.C, "esyuscan · Crop page")]
    [TestCase(PhysicalKey.A, "esyuscan · Adjust image")]
    public void PageEditorShortcutOpensTheMatchingDialog(PhysicalKey key, string title)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(key, RawInputModifiers.None);

            Assert.That(window.OwnedWindows.Select(dialog => dialog.Title), Is.EqualTo(new[] { title }));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void SettingsShortcutAlsoClosesSettingsWhileTypingInItsPopup()
    {
        var window = new MainWindow(new MainViewModel(), discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Alt);
            var input = window.FindControl<TextBox>("PresetNameBox")!;
            var root = TopLevel.GetTopLevel(input)!;

            root.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);

            Assert.That(window.FindControl<Button>("SettingsButton")!.Flyout!.IsOpen, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void TypingPageShortcutLettersInSettingsDoesNotEditTheDocument()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var before = session.Pages.ToArray();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Alt);
            var input = window.FindControl<TextBox>("PresetNameBox")!;
            var root = TopLevel.GetTopLevel(input)!;

            root.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
            root.KeyTextInput("r");
            root.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            root.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

            Assert.That(session.Pages, Is.EqualTo(before));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void SpaceOnACheckboxTogglesItWithoutStartingAScan()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<CheckBox>("SearchablePdfCheckBox")!.Focus();

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.That(model.SearchablePdf, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void AltOTogglesOcrButDoesNotBypassBusyState()
    {
        var model = new MainViewModel { Busy = true };
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Alt);

            Assert.That(model.SearchablePdf, Is.False);
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public void AltSTogglesTheScopeUsedByExportPrintAndEmail()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Alt);

            Assert.That(model.ExportSelected, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void F1ShowsTheGeneratedReferenceEvenDuringAnOperation()
    {
        var model = new MainViewModel { Busy = true };
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
            var reference = window.OwnedWindows.Single();

            Assert.That(reference.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text), Does.Contain("Ctrl+Shift+S"));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public void OcrTooltipKeepsLiveAvailabilityAlongsideItsShortcut()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            model.SetOcrAvailability(new(true, ["eng"], "Fixture OCR availability"));
            Dispatcher.UIThread.RunJobs();

            Assert.That(ToolTip.GetTip(window.FindControl<Button>("RefreshOcrButton")!), Is.EqualTo("Refresh installed OCR languages · Shift+F5\nFixture OCR availability"));
        }
        finally { window.Close(); }
    }
}
