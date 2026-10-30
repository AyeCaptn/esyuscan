using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class CropUiTests : TestFiles
{
    [AvaloniaTest]
    [TestCase("CropButton", "CropButton")]
    [TestCase("CropButton", "AdjustButton")]
    [TestCase("AdjustButton", "CropButton")]
    public void RepeatedPageEditorRequestsKeepOnlyOneEditorOpen(string first, string second)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<Button>(first)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<Button>(second)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(window.OwnedWindows.Count(), Is.EqualTo(1));
        }
        finally
        {
            foreach (var editor in window.OwnedWindows.ToArray()) editor.Close();
            window.CloseForTest();
        }
    }

    [AvaloniaTest]
    [TestCase(1, 1)]
    [TestCase(1, 30)]
    [TestCase(30, 1)]
    public async Task SmallCropsWithAdjustmentsCanBeApplied(int width, int height)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.SetAdjustments(0, AdjustmentTests.Combined);
        var editor = new CropWindow(session, session.Pages[0]);
        editor.Show();
        try
        {
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = width;
            editor.FindControl<NumericUpDown>("CropHeight")!.Value = height;
            await editor.PreviewReady;

            Assert.That(editor.FindControl<Button>("ApplyCrop")!.IsEnabled, Is.True);
        }
        finally { editor.Close(); }
    }

    [AvaloniaTest]
    public async Task CancellingCropAllowsOpeningAnotherEditor()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var crop = window.FindControl<Button>("CropButton")!;
            crop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.OwnedWindows.Single().Close(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });

            crop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(window.OwnedWindows.OfType<CropWindow>().Count(), Is.EqualTo(1));
        }
        finally
        {
            foreach (var editor in window.OwnedWindows.ToArray()) editor.Close();
            window.CloseForTest();
        }
    }

    [AvaloniaTest]
    public async Task RapidChangesPreviewOnlyTheLatestCrop()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var editor = new CropWindow(session, session.Pages[0]);
        editor.Show();
        try
        {
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = 10;
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = 20;
            editor.FindControl<NumericUpDown>("CropHeight")!.Value = 30;
            await editor.PreviewReady;

            var preview = (Avalonia.Media.Imaging.Bitmap)editor.FindControl<Image>("CropResultPreview")!.Source!;
            Assert.That(preview.PixelSize, Is.EqualTo(new Avalonia.PixelSize(20, 30)));
        }
        finally { editor.Close(); }
    }

    [AvaloniaTest]
    public async Task InvalidCropCancelsPendingPreviewAndDisablesApply()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var editor = new CropWindow(session, session.Pages[0]);
        editor.Show();
        try
        {
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = 20;
            editor.FindControl<NumericUpDown>("CropWidth")!.Value = 0;
            await editor.PreviewReady;

            Assert.That(editor.FindControl<Button>("ApplyCrop")!.IsEnabled, Is.False);
        }
        finally { editor.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingCropCancelsPendingPreview()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var editor = new CropWindow(session, session.Pages[0]);
        editor.Show();

        editor.Close();
        await editor.PreviewReady;

        Assert.That(editor.FindControl<Image>("CropResultPreview")!.Source, Is.Null);
    }
}
