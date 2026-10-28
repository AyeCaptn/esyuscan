using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NUnit.Framework;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture]
public sealed class AdjustmentUiTests : TestFiles
{
    private static void Click(Window window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public async Task ApplyCancelUndoRedoAndResetThroughWorkspace()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        session.Rotate(0);
        session.SetCrop(0, new(2, 3, 38, 60));
        var original = session.Pages[0];
        var manifest = File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json"));
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            Click(window, "AdjustButton");
            var editor = window.OwnedWindows.OfType<AdjustmentsWindow>().Single();
            editor.FindControl<NumericUpDown>("Brightness")!.Value = 100;
            editor.FindControl<CheckBox>("FlipHorizontal")!.IsChecked = true;
            await editor.PreviewReady;
            Assert.That(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(manifest));
            AssertPreview(session, editor);
            Click(editor, "CancelAdjustments");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.That(session.Pages[0], Is.EqualTo(original));

            Click(window, "AdjustButton");
            editor = window.OwnedWindows.OfType<AdjustmentsWindow>().Single();
            editor.FindControl<NumericUpDown>("Contrast")!.Value = 200;
            editor.FindControl<NumericUpDown>("Angle")!.Value = 14.5m;
            editor.FindControl<ComboBox>("Correction")!.SelectedIndex = 1;
            await editor.PreviewReady;
            var expected = editor.Result;
            Assert.That(editor.FindControl<Button>("ApplyAdjustments")!.IsEnabled, Is.True);
            Click(editor, "ApplyAdjustments");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.That(session.Pages[0], Is.EqualTo(expected));
            using (var png = PageRenderer.Png(session, expected)) AssertBitmap(png, model.Preview!);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(session.Pages[0], Is.EqualTo(original));
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.That(session.Pages[0], Is.EqualTo(expected));

            Click(window, "AdjustButton");
            editor = window.OwnedWindows.OfType<AdjustmentsWindow>().Single();
            Click(editor, "ResetAdjustments");
            await editor.PreviewReady;
            Assert.That(editor.Result, Is.EqualTo(original));
            AssertPreview(session, editor);
            Click(editor, "ApplyAdjustments");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.That(session.Pages[0], Is.EqualTo(original));
            session.Undo();
            Assert.That(session.Pages[0], Is.EqualTo(expected), "Reset must also be one undoable edit.");
        }
        finally { foreach (var editor in window.OwnedWindows.ToArray()) editor.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public async Task ResetAllRestoresPdfPassthroughOnlyOnApply()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var original = session.OriginalPath(session.Pages[0]);
        session.Remove(0);
        var pdf = session.NextPdfSourcePath();
        PdfImportTests.WritePdfFile(pdf, "Original");
        session.Add(original, new(Path.GetFileName(pdf), 0));
        session.SetEdits(0, session.Pages[0] with { Crop = new(2, 3, 38, 60), DeskewAngle = 2, Rotation = 90, Adjustments = AdjustmentTests.Combined });
        var edited = session.Pages[0];
        var window = new AdjustmentsWindow(session, edited);
        window.Show();
        try
        {
            Click(window, "ResetAllEdits");
            await window.PreviewReady;
            Assert.That(window.Result.PreservePdf, Is.True);
            Assert.That(window.FindControl<TextBlock>("AdjustmentStatus")!.Text, Does.Contain("passthrough restored"));
            Assert.That(session.Pages[0], Is.EqualTo(edited));
            AssertPreview(session, window);
            session.SetEdits(0, window.Result);
            Assert.That(session.Pages[0].PreservePdf, Is.True);
            session.Undo();
            Assert.That(session.Pages[0], Is.EqualTo(edited));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task LatestDraftWinsAndClosingCancelsPendingPreviewWithoutSaving()
    {
        using var session = CreateSession();
        AddPages(session, 1, detailed: true);
        var original = session.Pages[0];
        var window = new AdjustmentsWindow(session, original);
        window.Show();
        var initial = window.PreviewReady;
        var brightness = window.FindControl<NumericUpDown>("Brightness")!;
        for (var i = 0; i < 20; i++) brightness.Value = i * 10;
        var latest = window.PreviewReady;
        await latest;
        await initial;
        Assert.That(window.Result.Adjustments!.Brightness, Is.EqualTo(190));
        Assert.That(window.FindControl<Button>("ApplyAdjustments")!.IsEnabled, Is.True);
        AssertPreview(session, window);
        brightness.Value = 200;
        var pending = window.PreviewReady;
        window.Close();
        await pending;
        Assert.That(session.Pages[0], Is.EqualTo(original));
    }

    [AvaloniaTest]
    public async Task MissingOrFractionalToneValueBlocksApplyUntilValidPreview()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var window = new AdjustmentsWindow(session, session.Pages[0]);
        window.Show();
        try
        {
            await window.PreviewReady;
            var field = window.FindControl<NumericUpDown>("Brightness")!;
            field.Value = null;
            Assert.That(window.FindControl<Button>("ApplyAdjustments")!.IsEnabled, Is.False);
            field.Value = 1.2m;
            Assert.That(window.FindControl<Button>("ApplyAdjustments")!.IsEnabled, Is.False);
            field.Value = 100;
            Assert.That(window.FindControl<Button>("ApplyAdjustments")!.IsEnabled, Is.False, "Apply waits for this draft's pixels.");
            await window.PreviewReady;
            Assert.That(window.FindControl<Button>("ApplyAdjustments")!.IsEnabled, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(true)]
    [TestCase(false)]
    public async Task EscapeAndWindowCloseDiscardDraft(bool escape)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var original = session.Pages[0];
        var owner = new Window();
        owner.Show();
        var editor = new AdjustmentsWindow(session, original);
        var result = editor.ShowDialog<bool>(owner);
        try
        {
            editor.FindControl<Slider>("BrightnessSlider")!.Value = 200;
            Assert.That(editor.FindControl<NumericUpDown>("Brightness")!.Value, Is.EqualTo(200));
            await editor.PreviewReady;
            if (escape) editor.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            else editor.Close();
            Assert.That(await result, Is.False);
            Assert.That(session.Pages[0], Is.EqualTo(original));
        }
        finally { editor.Close(); owner.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    [TestCase(900, 800)]
    [TestCase(420, 640)]
    public async Task EditorFitsTiledWindowAndRendersPreview(int width, int height)
    {
        using var session = CreateSession();
        AddPages(session, 1, detailed: true);
        session.SetAdjustments(0, AdjustmentTests.Combined);
        var window = new AdjustmentsWindow(session, session.Pages[0]) { Width = width, Height = height };
        window.Show();
        try
        {
            await window.PreviewReady;
            window.UpdateLayout();
            foreach (var name in new[] { "ApplyAdjustments", "CancelAdjustments", "ResetAdjustments", "ResetAllEdits" })
            {
                var button = window.FindControl<Button>(name)!;
                var bottomRight = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                Assert.That(bottomRight.X, Is.LessThanOrEqualTo(window.ClientSize.Width));
                Assert.That(bottomRight.Y, Is.LessThanOrEqualTo(window.ClientSize.Height));
            }
            var preview = window.FindControl<Image>("AdjustmentPreview")!;
            var instructions = window.FindControl<TextBlock>("AdjustmentInstructions")!;
            var controls = window.FindControl<ScrollViewer>("AdjustmentControls")!;
            Assert.That(preview.Bounds.Height, Is.GreaterThanOrEqualTo(150));
            Assert.That(preview.TranslatePoint(default, window)!.Value.Y,
                Is.GreaterThanOrEqualTo(instructions.TranslatePoint(new Point(0, instructions.Bounds.Height), window)!.Value.Y));
            Assert.That(preview.TranslatePoint(new Point(0, preview.Bounds.Height), window)!.Value.Y,
                Is.LessThanOrEqualTo(controls.TranslatePoint(default, window)!.Value.Y));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            var folder = Environment.GetEnvironmentVariable("ESYUSCAN_ADJUSTMENTS_SCREENSHOT_DIR");
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"adjustments-{width}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    private static void AssertPreview(Session session, AdjustmentsWindow editor)
    {
        using var expected = PageRenderer.Png(session, editor.Result);
        AssertBitmap(expected, (Bitmap)editor.FindControl<Image>("AdjustmentPreview")!.Source!);
    }

    private static void AssertBitmap(Stream expected, Bitmap actual)
    {
        using var png = new MemoryStream();
        actual.Save(png, new PngBitmapEncoderOptions());
        png.Position = 0;
        using var actualImage = SKBitmap.Decode(png);
        using var expectedImage = SKBitmap.Decode(expected);
        Assert.That((actualImage.Width, actualImage.Height), Is.EqualTo((expectedImage.Width, expectedImage.Height)));
        Assert.That(actualImage.Pixels, Is.EqualTo(expectedImage.Pixels));
    }
}
