using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class AutomaticProcessingUiTests : TestFiles
{
    [AvaloniaTest]
    [TestCase("ProcessSelectedButton", 1, PageCorrection.Document)]
    [TestCase("ProcessSelectedButton", 1, PageCorrection.Photo)]
    [TestCase("ProcessAllButton", 3, PageCorrection.Document)]
    [TestCase("ProcessAllButton", 3, PageCorrection.Photo)]
    public async Task ProcessingActionsApplyTheChosenPresetToTheirScope(string action, int count, PageCorrection correction)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 3);
        model.SetSession(session);
        model.SelectPage(1);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<Button>("AutoProcessButton")!.Flyout!.ShowAt(window.FindControl<Button>("AutoProcessButton")!);
            window.FindControl<ComboBox>("ProcessingPresetPicker")!.SelectedItem = model.ProcessingPresets.Single(preset => preset.Value == correction);

            window.FindControl<Button>(action)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilIdle(model);

            Assert.That(session.Pages.Count(page => page.Adjustments?.Correction == correction), Is.EqualTo(count), model.Error);
        }
        finally { window.CloseForTest(); }
    }

    [AvaloniaTest]
    public async Task ProcessingSelectionPreservesFocusAndSelectionThroughUndo()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 3);
        model.SetSession(session);
        model.SelectPage(0);
        model.SelectPage(2, toggle: true);
        var selected = model.Selection.Selected.ToArray();
        var focus = model.Selection.Focus;
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<Button>("ProcessSelectedButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilIdle(model);

            window.FindControl<Button>("UndoButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That((model.Selection.Selected.ToArray(), model.Selection.Focus), Is.EqualTo((selected, focus)));
        }
        finally { window.CloseForTest(); }
    }

    [AvaloniaTest]
    public async Task ProcessingRefreshesTheMainPreviewUsingTheChosenCorrection()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<Button>("ProcessAllButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilIdle(model);
            using var expectedPng = PageRenderer.Png(session, session.Pages[0]);
            using var actualPng = new MemoryStream();
            model.Preview!.Save(actualPng, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            actualPng.Position = 0;
            using var expected = SkiaSharp.SKBitmap.Decode(expectedPng);
            using var actual = SkiaSharp.SKBitmap.Decode(actualPng);

            Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels), model.Error);
        }
        finally { window.CloseForTest(); }
    }

    [AvaloniaTest]
    public void ProcessingIsDisabledWhileBusy()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            model.Busy = true;

            Assert.That(window.FindControl<Button>("AutoProcessButton")!.IsEffectivelyEnabled, Is.False);
        }
        finally { model.Busy = false; window.CloseForTest(); }
    }

    [AvaloniaTest]
    public async Task AllPagesCanBeProcessedWithoutAThumbnailSelection()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        model.SelectPage(0, toggle: true);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<Button>("ProcessAllButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilIdle(model);

            Assert.That(session.Pages[0].Adjustments?.Correction, Is.EqualTo(PageCorrection.Document), model.Error);
        }
        finally { window.CloseForTest(); }
    }

    [AvaloniaTest]
    [TestCase(760)]
    [TestCase(1280)]
    public void ProcessingMenuRemainsAccessibleInNarrowAndWideWindows(int width)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false) { Width = width, Height = 680 };
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control | RawInputModifiers.Shift);

            Assert.That(window.FindControl<Button>("AutoProcessButton")!.Flyout!.IsOpen, Is.True);
        }
        finally { window.CloseForTest(); }
    }

    private static async Task UntilIdle(MainViewModel model)
    {
        if (!model.Busy) return;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (!model.Busy) idle.TrySetResult();
        }
        model.PropertyChanged += Changed;
        try
        {
            if (!model.Busy) idle.TrySetResult();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }
        finally { model.PropertyChanged -= Changed; }
    }
}
