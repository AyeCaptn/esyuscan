using System.ComponentModel;

namespace Scan;

/// <summary>Display-only dimensions; no document or crop coordinates are changed.</summary>
public sealed class PreviewZoom : INotifyPropertyChanged
{
    public const double Minimum = 0.1;
    public const double Maximum = 4;
    private double pageWidth, pageHeight, viewportWidth, viewportHeight;
    public bool IsFit { get; private set; } = true;
    public double Scale { get; private set; } = 1;
    public double Width => pageWidth * Scale;
    public double Height => pageHeight * Scale;
    public string Label => IsFit ? $"Fit · {Scale:P0}" : $"{Scale:P0}";

    public void SetPage(double width, double height) { pageWidth = width; pageHeight = height; Update(); }
    public void SetViewport(double width, double height) { viewportWidth = width; viewportHeight = height; Update(); }
    public void Fit() { IsFit = true; Update(); }
    public void Step(int direction)
    {
        SetScale(Scale * Math.Pow(1.25, direction));
    }
    public void SetScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) return;
        // A large scan may fit below the manual minimum. Zooming out must never enlarge it.
        if (scale < Scale && Scale < Minimum) return;
        IsFit = false;
        Scale = Math.Clamp(scale, Minimum, Maximum);
        Update();
    }
    private void Update()
    {
        if (IsFit && pageWidth > 0 && pageHeight > 0 && viewportWidth > 0 && viewportHeight > 0)
            Scale = Math.Min(Maximum, Math.Min(viewportWidth / pageWidth, viewportHeight / pageHeight));
        foreach (var name in new[] { nameof(Width), nameof(Height), nameof(Scale), nameof(IsFit), nameof(Label) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
