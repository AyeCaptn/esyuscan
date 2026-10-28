using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NAPS2.Scan;

namespace Scan;

public sealed record ScanGeometryDiagram(decimal BedWidth, decimal BedHeight, decimal PaperWidth, decimal PaperHeight,
    HorizontalAlign Alignment, ScanRegion? Area);

// A scale drawing of the reported bounds, aligned paper and kept area, without any scanner job.
public sealed class ScanGeometryPreview : Control
{
    public static readonly StyledProperty<ScanGeometryDiagram?> DiagramProperty =
        AvaloniaProperty.Register<ScanGeometryPreview, ScanGeometryDiagram?>(nameof(Diagram));
    public ScanGeometryDiagram? Diagram { get => GetValue(DiagramProperty); set => SetValue(DiagramProperty, value); }
    static ScanGeometryPreview() => AffectsRender<ScanGeometryPreview>(DiagramProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Diagram is not { BedWidth: > 0, BedHeight: > 0, PaperWidth: > 0, PaperHeight: > 0 } data) return;
        var scale = Math.Min((Bounds.Width - 8) / (double)data.BedWidth, (Bounds.Height - 8) / (double)data.BedHeight);
        if (scale <= 0) return;
        var accent = this.FindResource("AccentBrush") as IBrush ?? Brushes.Teal;
        var border = this.FindResource("BorderBrush") as IBrush ?? Brushes.Gray;
        var origin = new Point((Bounds.Width - (double)data.BedWidth * scale) / 2, 4);
        Rect Box(decimal x, decimal y, decimal width, decimal height) => new(origin.X + (double)x * scale,
            origin.Y + (double)y * scale, (double)width * scale, (double)height * scale);
        context.DrawRectangle(null, new Pen(border, 1), Box(0, 0, data.BedWidth, data.BedHeight));
        var offset = data.Alignment switch
        {
            HorizontalAlign.Center => (data.BedWidth - data.PaperWidth) / 2,
            HorizontalAlign.Left => data.BedWidth - data.PaperWidth,
            _ => 0
        };
        using (context.PushOpacity(.15)) context.DrawRectangle(accent, null, Box(offset, 0, data.PaperWidth, data.PaperHeight));
        context.DrawRectangle(null, new Pen(accent, 1), Box(offset, 0, data.PaperWidth, data.PaperHeight));
        if (data.Area is { } area)
            context.DrawRectangle(null, new Pen(accent, 3), Box(offset + area.X, area.Y, area.Width, area.Height));
        context.DrawEllipse(accent, null, new Point(origin.X + (double)offset * scale, origin.Y), 3, 3);
    }
}
