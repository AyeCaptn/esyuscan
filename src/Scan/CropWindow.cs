using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NAPS2.Images;

namespace Scan;

public sealed class CropWindow : Window
{
    private readonly IMemoryImage deskewedCanvas;
    private readonly SessionPage page;
    private readonly int originalWidth;
    private readonly int originalHeight;
    private readonly Bitmap fullPage;
    private readonly CropSelection selection;
    private readonly Image resultPreview = new() { Name = "CropResultPreview", Stretch = Stretch.Uniform };
    private readonly NumericUpDown[] fields;
    private readonly Button apply = new() { Name = "ApplyCrop", Content = "Apply crop · Enter", IsDefault = true, Classes = { "primary" } };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private Bitmap? croppedPreview;
    private bool updating;

    public CropWindow(Session session, SessionPage page)
    {
        this.page = page;
        Title = "esyuscan · Crop page";
        Width = 980;
        Height = 760;
        MinWidth = 680;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        deskewedCanvas = PageRenderer.Render(session, page with { Crop = null, Rotation = 0, Adjustments = null });
        (originalWidth, originalHeight) = (deskewedCanvas.Width, deskewedCanvas.Height);
        using (var png = PageRenderer.Png(deskewedCanvas, null, page.Rotation)) fullPage = new Bitmap(png);
        var crop = page.Crop ?? new PageCrop(0, 0, originalWidth, originalHeight);
        var width = originalWidth;
        var height = originalHeight;
        for (var angle = 0; angle < page.Rotation; angle += 90)
        {
            crop = crop.RotateClockwise(width, height);
            (width, height) = (height, width);
        }
        selection = new CropSelection(fullPage) { Name = "CropSelection", Selection = crop };
        fields = new[] { "Left", "Top", "Width", "Height" }.Select(name => new NumericUpDown
        {
            Name = "Crop" + name,
            Minimum = 0,
            Maximum = Math.Max(width, height),
            Increment = 1,
            FormatString = "0",
            Width = 110
        }).ToArray();
        var entries = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        for (var i = 0; i < fields.Length; i++)
        {
            entries.Children.Add(new StackPanel { Spacing = 5, Children = { new TextBlock { Text = new[] { "Left", "Top", "Width", "Height" }[i] }, fields[i] } });
            fields[i].ValueChanged += (_, _) => ReadFields();
        }
        selection.SelectionChanged += (_, _) => UpdateFields();
        var reset = new Button { Name = "ResetCrop", Content = "Full page" };
        reset.Click += (_, _) => { selection.Selection = new PageCrop(0, 0, fullPage.PixelSize.Width, fullPage.PixelSize.Height); UpdateFields(); };
        var cancel = new Button { Name = "CancelCrop", Content = "Cancel · Esc", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        apply.Click += (_, _) => { if (apply.IsEnabled) Close(true); };
        var previews = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 20 };
        var sourcePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10 };
        sourcePanel.Children.Add(new TextBlock { Text = "Drag to select · full page" });
        Grid.SetRow(selection, 1);
        sourcePanel.Children.Add(selection);
        previews.Children.Add(sourcePanel);
        var resultPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10 };
        resultPanel.Children.Add(new TextBlock { Text = "Result preview" });
        Grid.SetRow(resultPreview, 1);
        resultPanel.Children.Add(resultPreview);
        Grid.SetColumn(resultPanel, 1);
        previews.Children.Add(resultPanel);
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 16 };
        layout.Children.Add(new TextBlock { Text = "Select before image adjustments. Pixel coordinates follow the full page; the result includes all edits.", TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(previews, 1);
        layout.Children.Add(previews);
        Grid.SetRow(entries, 2);
        layout.Children.Add(entries);
        Grid.SetRow(error, 3);
        layout.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { apply, cancel, reset } };
        Grid.SetRow(buttons, 4);
        layout.Children.Add(buttons);
        Content = layout;
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        foreach (var control in new Control[] { apply, cancel, reset, selection, resultPreview }.Concat(fields))
            names.Register(control.Name!, control);
        Closed += (_, _) => { resultPreview.Source = null; croppedPreview?.Dispose(); fullPage.Dispose(); deskewedCanvas.Dispose(); };
        UpdateFields();
    }

    public PageCrop? Result { get; private set; }

    private void UpdateFields()
    {
        updating = true;
        var crop = selection.Selection;
        var values = new[] { crop.X, crop.Y, crop.Width, crop.Height };
        for (var i = 0; i < fields.Length; i++) fields[i].Value = values[i];
        updating = false;
        UpdatePreview(crop);
    }

    private void ReadFields()
    {
        if (updating) return;
        if (fields.Any(field => field.Value is null || field.Value != decimal.Truncate(field.Value.Value)))
        {
            apply.IsEnabled = false;
            error.Text = "Enter whole pixel coordinates in all four fields.";
            return;
        }
        var crop = new PageCrop((int)fields[0].Value!.Value, (int)fields[1].Value!.Value,
            (int)fields[2].Value!.Value, (int)fields[3].Value!.Value);
        UpdatePreview(crop);
        if (apply.IsEnabled) selection.Selection = crop;
    }

    private void UpdatePreview(PageCrop displayed)
    {
        try
        {
            var crop = PageCrop.FromDisplay(displayed, page.Rotation, originalWidth, originalHeight);
            Result = crop == new PageCrop(0, 0, originalWidth, originalHeight) ? null : crop;
            using var png = PageRenderer.Png(deskewedCanvas, Result, page.Rotation, page.Adjustments);
            var next = new Bitmap(png);
            resultPreview.Source = next;
            croppedPreview?.Dispose();
            croppedPreview = next;
            apply.IsEnabled = true;
            error.Text = "Changes are saved only when you apply. Full page removes the crop.";
        }
        catch (InvalidDataException exception) { apply.IsEnabled = false; error.Text = exception.Message; }
    }
}

// Only draws editor chrome. All image processing and the result preview use PageRenderer.
public sealed class CropSelection(Bitmap image) : Control
{
    private PageCrop selection = new(0, 0, image.PixelSize.Width, image.PixelSize.Height);
    private Point? start;
    public event EventHandler? SelectionChanged;
    public PageCrop Selection
    {
        get => selection;
        set { selection = value; InvalidateVisual(); }
    }

    private Rect ImageBounds
    {
        get
        {
            var scale = Math.Min(Bounds.Width / image.PixelSize.Width, Bounds.Height / image.PixelSize.Height);
            var size = new Size(image.PixelSize.Width * scale, image.PixelSize.Height * scale);
            return new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = ImageBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        context.DrawImage(image, new Rect(image.Size), bounds);
        var sx = bounds.Width / image.PixelSize.Width;
        var sy = bounds.Height / image.PixelSize.Height;
        var kept = new Rect(bounds.X + Selection.X * sx, bounds.Y + Selection.Y * sy, Selection.Width * sx, Selection.Height * sy);
        var shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
        context.DrawRectangle(shade, null, new Rect(bounds.X, bounds.Y, bounds.Width, kept.Y - bounds.Y));
        context.DrawRectangle(shade, null, new Rect(bounds.X, kept.Bottom, bounds.Width, bounds.Bottom - kept.Bottom));
        context.DrawRectangle(shade, null, new Rect(bounds.X, kept.Y, kept.X - bounds.X, kept.Height));
        context.DrawRectangle(shade, null, new Rect(kept.Right, kept.Y, bounds.Right - kept.Right, kept.Height));
        context.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 2), kept);
    }

    private Point PixelPoint(Point point)
    {
        var bounds = ImageBounds;
        return new Point(Math.Clamp((point.X - bounds.X) / bounds.Width, 0, 1) * image.PixelSize.Width,
            Math.Clamp((point.Y - bounds.Y) / bounds.Height, 0, 1) * image.PixelSize.Height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !ImageBounds.Contains(e.GetPosition(this))) return;
        start = PixelPoint(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (start is not { } first) return;
        var last = PixelPoint(e.GetPosition(this));
        var x = (int)Math.Floor(Math.Min(first.X, last.X));
        var y = (int)Math.Floor(Math.Min(first.Y, last.Y));
        var right = (int)Math.Ceiling(Math.Max(first.X, last.X));
        var bottom = (int)Math.Ceiling(Math.Max(first.Y, last.Y));
        if (right <= x || bottom <= y) return;
        Selection = new PageCrop(x, y, right - x, bottom - y);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        start = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => start = null;
}
