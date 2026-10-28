using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Scan;

public sealed partial class MainWindow
{
    private ListBox tray = null!;
    private ScrollViewer previewScroller = null!;
    private Point? dragStart;
    private string? dragPage;
    private IPointer? dragPointer;
    private bool dragging;
    private bool collapseOnRelease;
    private Point? panStart;
    private Vector panOffset;
    private int dropBoundary;
    private double? pinchStartScale;

    private void InitializeNavigation()
    {
        tray = this.FindControl<ListBox>("PageTray")!;
        tray.Focusable = true;
        previewScroller = this.FindControl<ScrollViewer>("PreviewScroller")!;
        tray.AddHandler(PointerPressedEvent, TrayPressed, RoutingStrategies.Tunnel);
        tray.AddHandler(PointerMovedEvent, TrayMoved, RoutingStrategies.Tunnel);
        tray.AddHandler(PointerReleasedEvent, TrayReleased, RoutingStrategies.Tunnel);
        tray.PointerCaptureLost += (_, _) => ClearDrag();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Selection)) SyncTraySelection();
            if (args.PropertyName == nameof(MainViewModel.Preview))
            {
                previewScroller.Offset = default;
                pinchStartScale = null;
            }
        };
        tray.LayoutUpdated += (_, _) => SyncTraySelection();
        previewScroller.PropertyChanged += (_, args) =>
        {
            if (args.Property == ScrollViewer.ViewportProperty)
                model.Zoom.SetViewport(previewScroller.Viewport.Width, previewScroller.Viewport.Height);
        };
        previewScroller.AddHandler(PointerWheelChangedEvent, (_, args) =>
        {
            if (!args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.Delta.Y == 0) return;
            model.Zoom.Step(args.Delta.Y > 0 ? 1 : -1);
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        previewScroller.GestureRecognizers.Add(new PinchGestureRecognizer());
        previewScroller.Pinch += (_, args) =>
        {
            if (!model.HasPages) return;
            pinchStartScale ??= model.Zoom.Scale;
            model.Zoom.SetScale(pinchStartScale.Value * args.Scale);
            args.Handled = true;
        };
        previewScroller.PinchEnded += (_, _) => pinchStartScale = null;
        previewScroller.PointerTouchPadGestureMagnify += (_, args) =>
        {
            if (!model.HasPages) return;
            model.Zoom.SetScale(model.Zoom.Scale * (1 + args.Delta.Y));
            args.Handled = true;
        };
        previewScroller.PointerPressed += (_, args) =>
        {
            if (args.Source is not Image) return;
            previewScroller.Focus();
            if (args.Pointer.Type != PointerType.Mouse || !args.GetCurrentPoint(previewScroller).Properties.IsLeftButtonPressed) return;
            panStart = args.GetPosition(previewScroller);
            panOffset = previewScroller.Offset;
            args.Pointer.Capture(previewScroller);
            args.Handled = true;
        };
        previewScroller.PointerMoved += (_, args) =>
        {
            if (panStart is not { } start) return;
            var delta = args.GetPosition(previewScroller) - start;
            previewScroller.Offset = panOffset - delta;
            args.Handled = true;
        };
        previewScroller.PointerReleased += (_, args) => { panStart = null; args.Pointer.Capture(null); };
        previewScroller.PointerCaptureLost += (_, _) => panStart = null;
        SyncTraySelection();
    }

    private void SyncTraySelection()
    {
        var wanted = model.Pages.Where(page => model.Selection.Selected.Contains(page.Page.Identity)).ToArray();
        if (tray.SelectedItems is not { } items) return;
        foreach (var item in items.Cast<object>().Except(wanted).ToArray()) items.Remove(item);
        foreach (var item in wanted) if (!items.Contains(item)) items.Add(item);
        foreach (var container in tray.GetRealizedContainers().OfType<ListBoxItem>())
        {
            var focused = container.DataContext is PagePreview page && page.Page.Identity == model.Selection.Focus;
            container.BorderThickness = new Thickness(focused ? 2 : 0);
            container.BorderBrush = (Avalonia.Media.IBrush?)this.FindResource("AccentBrush");
        }
    }

    private PagePreview? HitPage(object? source) => (source as Visual)?.GetSelfAndVisualAncestors()
        .OfType<ListBoxItem>().FirstOrDefault()?.DataContext as PagePreview;

    private void TrayPressed(object? sender, PointerPressedEventArgs args)
    {
        if (model.Busy || !args.GetCurrentPoint(tray).Properties.IsLeftButtonPressed || HitPage(args.Source) is not { } page) return;
        tray.Focus();
        var modifiers = args.KeyModifiers;
        collapseOnRelease = modifiers == KeyModifiers.None && model.Selection.Selected.Contains(page.Page.Identity);
        model.SelectPage(model.Pages.IndexOf(page), modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Shift), collapseOnRelease);
        if (model.Selection.Selected.Contains(page.Page.Identity))
        {
            dragStart = args.GetPosition(tray);
            dragPage = page.Page.Identity;
            dragPointer = args.Pointer;
            args.Pointer.Capture(tray);
        }
        args.Handled = true;
    }

    private int BoundaryAt(Point position)
    {
        foreach (var item in tray.GetRealizedContainers().OfType<ListBoxItem>())
        {
            var top = item.TranslatePoint(default, tray)?.Y ?? 0;
            if (position.Y < top + item.Bounds.Height / 2)
                return model.Pages.IndexOf((PagePreview)item.DataContext!);
        }
        return model.Pages.Count;
    }

    private void TrayMoved(object? sender, PointerEventArgs args)
    {
        if (dragStart is not { } start || !model.CanEditSelection) return;
        var position = args.GetPosition(tray);
        if (!dragging && Math.Abs(position.X - start.X) + Math.Abs(position.Y - start.Y) < 6) return;
        dragging = true;
        dropBoundary = BoundaryAt(position);
        var hint = this.FindControl<TextBlock>("ReorderHint")!;
        hint.Text = $"Move {model.Selection.Selected.Count} selected · insert before page {dropBoundary + 1} · Esc cancels";
        if (dropBoundary == model.Pages.Count) hint.Text = $"Move {model.Selection.Selected.Count} selected · insert at end · Esc cancels";
        hint.IsVisible = true;
        var scroll = tray.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null && (position.Y < 24 || position.Y > tray.Bounds.Height - 24))
            scroll.Offset += new Vector(0, position.Y < 24 ? -24 : 24);
        args.Handled = true;
    }

    private void TrayReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (dragStart is null) return;
        var position = args.GetPosition(tray);
        if (dragging && new Rect(tray.Bounds.Size).Contains(position))
            EditSelection(() => model.ReorderSelection(BoundaryAt(position)));
        else if (!dragging && collapseOnRelease && dragPage is not null)
            model.SelectPage(model.Pages.ToList().FindIndex(page => page.Page.Identity == dragPage));
        ClearDrag();
        args.Pointer.Capture(null);
        args.Handled = true;
    }

    private void ClearDrag()
    {
        dragStart = null;
        dragging = false;
        dragPage = null;
        var pointer = dragPointer;
        dragPointer = null;
        pointer?.Capture(null);
        this.FindControl<TextBlock>("ReorderHint")!.IsVisible = false;
    }

    private static bool IsInputControl(object? source) => source is Visual visual && visual.GetSelfAndVisualAncestors()
        .Any(control => control is TextBox or ComboBox or NumericUpDown);

    private bool TryNavigationKey(KeyEventArgs args)
    {
        if (args.Key == Key.Escape && dragStart is not null) { ClearDrag(); return true; }
        if (model.Busy || !model.HasPages) return false;
        var modifiers = args.KeyModifiers;
        if (modifiers.HasFlag(KeyModifiers.Alt) || modifiers.HasFlag(KeyModifiers.Meta)) return false;
        if (this.FindControl<Button>("SettingsButton")!.Flyout!.IsOpen) return false;
        var index = model.Pages.ToList().FindIndex(page => page.Page.Identity == model.Selection.Focus);
        var target = args.Key switch
        {
            Key.Left or Key.Up or Key.PageUp => Math.Max(0, index - 1),
            Key.Right or Key.Down or Key.PageDown => Math.Min(model.Pages.Count - 1, index + 1),
            Key.Home => 0,
            Key.End => model.Pages.Count - 1,
            _ => -1
        };
        if (target >= 0)
        {
            var control = modifiers.HasFlag(KeyModifiers.Control);
            var shift = modifiers.HasFlag(KeyModifiers.Shift);
            model.SelectPage(target, control, shift, control && !shift);
            tray.ScrollIntoView(target);
            return true;
        }
        if (args.Key == Key.Space && tray.IsKeyboardFocusWithin)
        {
            model.SelectPage(index, toggle: modifiers.HasFlag(KeyModifiers.Control));
            return true;
        }
        return false;
    }

    private void ZoomIn(object? sender, RoutedEventArgs args) => model.Zoom.Step(1);
    private void ZoomOut(object? sender, RoutedEventArgs args) => model.Zoom.Step(-1);
    private void FitPage(object? sender, RoutedEventArgs args) { model.Zoom.Fit(); previewScroller.Offset = default; }
}
