using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Scan;

public sealed class AdjustmentsWindow : Window
{
    private readonly Session session;
    private readonly Image preview = new() { Name = "AdjustmentPreview", Stretch = Stretch.Uniform, MinHeight = 150 };
    private readonly TextBlock status = new() { Name = "AdjustmentStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button apply = new() { Name = "ApplyAdjustments", Content = "Apply · Enter", IsDefault = true, Classes = { "primary" } };
    private readonly NumericUpDown[] fields;
    private readonly ComboBox correction = new() { Name = "Correction", ItemsSource = new[] { "No automatic correction", "Document · tones + noise reduction", "Photo · tones only" } };
    private readonly CheckBox horizontal = new() { Name = "FlipHorizontal", Content = "Flip horizontally" };
    private readonly CheckBox vertical = new() { Name = "FlipVertical", Content = "Flip vertically" };
    private readonly SemaphoreSlim renderGate = new(1);
    private CancellationTokenSource? rendering;
    private Bitmap? bitmap;
    private bool updating;
    private bool closed;

    public AdjustmentsWindow(Session session, SessionPage page)
    {
        this.session = session;
        Result = page;
        Title = "esyuscan · Adjust image";
        Width = 900;
        Height = 800;
        MinWidth = 420;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        fields = new[]
        {
            Field("Brightness", -1000, 1000, 50, "0", names, controls),
            Field("Contrast", -1000, 1000, 50, "0", names, controls),
            Field("Sharpness", 0, 1000, 50, "0", names, controls),
            Field("Angle", -180, 180, 0.1m, "0.0", names, controls)
        };
        foreach (var field in fields) field.ValueChanged += (_, _) => ReadFields();
        correction.SelectionChanged += (_, _) => ReadFields();
        horizontal.IsCheckedChanged += (_, _) => ReadFields();
        vertical.IsCheckedChanged += (_, _) => ReadFields();
        var options = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                controls, correction,
                new WrapPanel { Children = { horizontal, vertical } },
                new TextBlock
                {
                    Text = "Document correction adjusts paper/text tones and reduces noise together; it can soften fine detail. It leaves pure black/white pages unchanged. Photo corrects tones only.",
                    TextWrapping = TextWrapping.Wrap, Classes = { "muted" }
                },
                new TextBlock
                {
                    Text = "Clockwise angle is added after crop and 90° rotation, then flips follow the displayed axes. Rotation uses a white canvas and can clip edges. Crop selects before these adjustments.",
                    TextWrapping = TextWrapping.Wrap, Classes = { "muted" }
                }
            }
        };
        var reset = new Button { Name = "ResetAdjustments", Content = "Reset adjustments" };
        reset.Click += (_, _) => { Result = Result with { Adjustments = null }; WriteFields(); QueuePreview(); };
        var resetAll = new Button { Name = "ResetAllEdits", Content = "Reset all page edits" };
        resetAll.Click += (_, _) =>
        {
            Result = Result with { Adjustments = null, Crop = null, DeskewAngle = 0, Rotation = 0 };
            WriteFields();
            QueuePreview();
        };
        var cancel = new Button { Name = "CancelAdjustments", Content = "Cancel · Esc", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        apply.Click += (_, _) => { if (apply.IsEnabled) Close(true); };
        var buttons = new WrapPanel { Children = { apply, cancel, reset, resetAll } };
        foreach (var button in buttons.Children.OfType<Button>()) button.Margin = new Thickness(0, 0, 8, 8);
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,*,Auto,Auto"), RowSpacing = 12 };
        layout.Children.Add(new TextBlock { Name = "AdjustmentInstructions", Text = "Preview at original resolution. Apply saves one undoable edit; Cancel discards the draft.", TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(preview, 1);
        layout.Children.Add(preview);
        var scroll = new ScrollViewer { Name = "AdjustmentControls", Content = options, MinHeight = 80, MaxHeight = 310, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2);
        layout.Children.Add(scroll);
        Grid.SetRow(status, 3);
        layout.Children.Add(status);
        Grid.SetRow(buttons, 4);
        layout.Children.Add(buttons);
        Content = layout;
        foreach (var control in new Control[] { preview, status, correction, horizontal, vertical, apply, cancel, reset, resetAll, scroll, layout.Children[0] })
            names.Register(control.Name!, control);
        Closed += (_, _) =>
        {
            closed = true;
            rendering?.Cancel();
            preview.Source = null;
            bitmap?.Dispose();
        };
        WriteFields();
        QueuePreview();
    }

    // Draft only; the owning window commits it after a successful dialog result.
    public SessionPage Result { get; private set; }
    public Task PreviewReady { get; private set; } = Task.CompletedTask;

    private NumericUpDown Field(string name, decimal min, decimal max, decimal increment, string format,
        NameScope names, WrapPanel controls)
    {
        var number = new NumericUpDown { Name = name, Minimum = min, Maximum = max, Increment = increment, FormatString = format, Width = 115 };
        var slider = new Slider { Name = name + "Slider", Minimum = (double)min, Maximum = (double)max, TickFrequency = (double)increment, IsSnapToTickEnabled = true };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(number, 1);
        grid.Children.Add(slider);
        grid.Children.Add(number);
        slider.ValueChanged += (_, _) =>
        {
            if (!updating) number.Value = (decimal)slider.Value;
        };
        number.ValueChanged += (_, _) =>
        {
            var previous = updating;
            updating = true;
            slider.Value = (double)(number.Value ?? 0);
            updating = previous;
        };
        controls.Children.Add(new StackPanel
        {
            Width = 280,
            Margin = new Thickness(0, 0, 16, 12),
            Spacing = 4,
            Children = { new TextBlock { Text = name == "Angle" ? "Clockwise angle (°)" : name + (name == "Sharpness" ? " (0–1000)" : " (−1000–1000)") }, grid }
        });
        names.Register(number.Name!, number);
        names.Register(slider.Name!, slider);
        return number;
    }

    private void WriteFields()
    {
        updating = true;
        var edit = Result.Adjustments ?? new PageAdjustments();
        fields[0].Value = edit.Brightness;
        fields[1].Value = edit.Contrast;
        fields[2].Value = edit.Sharpness;
        fields[3].Value = (decimal)edit.Angle;
        horizontal.IsChecked = edit.FlipHorizontal;
        vertical.IsChecked = edit.FlipVertical;
        correction.SelectedIndex = (int)edit.Correction;
        updating = false;
    }

    private void ReadFields()
    {
        if (updating || closed) return;
        rendering?.Cancel();
        apply.IsEnabled = false;
        if (fields.Any(field => field.Value is null) || fields.Take(3).Any(field => field.Value != decimal.Truncate(field.Value!.Value)))
        {
            status.Text = "Enter whole numbers for brightness, contrast and sharpening, and a rotation angle.";
            return;
        }
        Result = Result with
        {
            Adjustments = new PageAdjustments((int)fields[0].Value!.Value, (int)fields[1].Value!.Value,
                (int)fields[2].Value!.Value, (double)fields[3].Value!.Value,
                horizontal.IsChecked == true, vertical.IsChecked == true, (PageCorrection)correction.SelectedIndex)
        };
        QueuePreview();
    }

    private void QueuePreview()
    {
        rendering?.Cancel();
        var cancellation = new CancellationTokenSource();
        rendering = cancellation;
        apply.IsEnabled = false;
        status.Text = "Rendering preview…";
        PreviewReady = UpdatePreview(Result, cancellation);
    }

    private async Task UpdatePreview(SessionPage draft, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(150, token);
            await renderGate.WaitAsync(token);
            try
            {
                using var png = await Task.Run(() => PageRenderer.Png(session, draft, token), token);
                token.ThrowIfCancellationRequested();
                if (closed) return;
                var next = new Bitmap(png);
                preview.Source = next;
                bitmap?.Dispose();
                bitmap = next;
                apply.IsEnabled = true;
                status.Text = draft.PdfSource is not null
                    ? draft.PreservePdf ? "Ready. PDF source passthrough restored." : "Ready. PDF export will use these pixels; the original PDF stays in the session."
                    : "Ready. Changes are saved only when you apply.";
            }
            finally { renderGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closed && !token.IsCancellationRequested) { status.Text = error.Message; apply.IsEnabled = false; }
        }
        finally
        {
            if (ReferenceEquals(rendering, cancellation)) rendering = null;
            cancellation.Dispose();
        }
    }
}
