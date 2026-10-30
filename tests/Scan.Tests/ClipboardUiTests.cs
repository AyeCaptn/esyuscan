using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class ClipboardUiTests : TestFiles
{
    [AvaloniaTest]
    [TestCase(true)]
    [TestCase(false)]
    public async Task CopyPublishesTheFocusedEditedPageAndSurvivesNewScans(bool shortcut)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 2, detailed: true);
        session.SetCrop(1, new PageCrop(10, 20, 400, 500));
        session.Rotate(1);
        model.SetSession(session);
        model.SelectPage(1);
        model.SelectAllPages();
        model.SelectedPage = model.Pages[1];
        model.ExportSelected = true;
        var window = new MainWindow(model, discover: false) { Width = 760, Height = 680 };
        window.Show();
        try
        {
            var copyButton = window.FindControl<Button>("CopyPageButton")!;
            var corner = copyButton.TranslatePoint(new Point(copyButton.Bounds.Width, copyButton.Bounds.Height), window)!.Value;
            Assert.That(corner.X, Is.LessThanOrEqualTo(window.ClientSize.Width));
            Assert.That(corner.Y, Is.LessThanOrEqualTo(window.ClientSize.Height));
            using var expected = PageRenderer.Png(session, session.Pages[1]);
            if (shortcut) window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            else window.FindControl<Button>("CopyPageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilIdle(model);

            Assert.That(model.Error, Is.Empty);
            Assert.That(model.Status, Does.StartWith("Page copied"));
            Assert.That(model.HasUnsavedScans, Is.True);
            // The headless clipboard returns the transfer-owned bitmap directly.
            var copied = await window.Clipboard!.TryGetBitmapAsync();
            Assert.That(copied, Is.Not.Null);
            Assert.That(copied!.PixelSize, Is.EqualTo(new PixelSize(500, 400)));
            using var encoded = new MemoryStream();
            copied.Save(encoded, new PngBitmapEncoderOptions());
            using var original = new Bitmap(expected);
            using var originalEncoded = new MemoryStream();
            original.Save(originalEncoded, new PngBitmapEncoderOptions());
            Assert.That(encoded.ToArray(), Is.EqualTo(originalEncoded.ToArray()));

            model.SetSession(Session.CreateTemporary());
            var retained = await window.Clipboard!.TryGetBitmapAsync();
            Assert.That(retained!.PixelSize, Is.EqualTo(new PixelSize(500, 400)));
        }
        finally { await window.Clipboard!.ClearAsync(); window.CloseForTest(); }
    }

    [AvaloniaTest]
    public async Task CopyInATextFieldDoesNotCopyTheScan()
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
            var text = window.FindControl<TextBox>("PresetNameBox")!;
            text.Text = "Copy this text";
            text.Focus();
            text.SelectAll();
            TopLevel.GetTopLevel(text)!.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            await Task.Yield();

            Assert.That(await window.Clipboard!.TryGetTextAsync(), Is.EqualTo("Copy this text"));
            Assert.That(model.Busy, Is.False);
        }
        finally { await window.Clipboard!.ClearAsync(); window.CloseForTest(); }
    }

    [AvaloniaTest]
    public async Task CopyIsDisabledWithoutAPageAndWhileBusy()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            await window.Clipboard!.SetTextAsync("Keep clipboard");
            var copy = window.FindControl<Button>("CopyPageButton")!;
            Assert.That(copy.IsEffectivelyEnabled, Is.False);
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            AddPages(model.Session!, 1);
            model.SyncPages();
            Assert.That(copy.IsEffectivelyEnabled, Is.True);
            model.Busy = true;
            Assert.That(copy.IsEffectivelyEnabled, Is.False);
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            Assert.That(await window.Clipboard!.TryGetTextAsync(), Is.EqualTo("Keep clipboard"));
        }
        finally { model.Busy = false; await window.Clipboard!.ClearAsync(); window.CloseForTest(); }
    }

    private static async Task UntilIdle(MainViewModel model)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (model.Busy) await Task.Delay(10, timeout.Token);
    }
}
