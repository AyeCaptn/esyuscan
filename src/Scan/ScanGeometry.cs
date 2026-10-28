using NAPS2.Images;
using NAPS2.Scan;

namespace Scan;

// Physical units in preferences are always millimetres; Unit controls presentation only.
public sealed record ScanRegion(decimal X, decimal Y, decimal Width, decimal Height)
{
    public bool Fits(decimal width, decimal height) => X >= 0 && Y >= 0 && Width > 0 && Height > 0
        && X <= width && Y <= height && Width <= width - X && Height <= height - Y;

    public PageCrop ToPixels(decimal dpi, int imageWidth, int imageHeight, decimal? verticalDpi = null)
    {
        if (dpi <= 0 || verticalDpi is <= 0 || !Fits(2000, 2000)) throw new InvalidDataException("Invalid scan area.");
        int Pixel(decimal mm, decimal resolution) => checked((int)decimal.Round(mm * resolution / 25.4m, 0, MidpointRounding.AwayFromZero));
        var x = Pixel(X, dpi);
        var y = Pixel(Y, verticalDpi ?? dpi);
        var crop = new PageCrop(x, y, Pixel(X + Width, dpi) - x, Pixel(Y + Height, verticalDpi ?? dpi) - y);
        crop.Validate(imageWidth, imageHeight);
        return crop;
    }
}
public sealed record ScanGeometry(decimal WidthMm, decimal HeightMm, HorizontalAlign Alignment = HorizontalAlign.Right,
    ScanRegion? Area = null, string Unit = "mm")
{
    public bool IsValid => WidthMm is > 0 and <= 2000 && HeightMm is > 0 and <= 2000
        && Enum.IsDefined(Alignment) && Unit is "mm" or "in" && (Area is null || Area.Fits(WidthMm, HeightMm));
}

public static class ScanValidation
{
    public static PerSourceCaps? Source(ScanCaps caps, PaperSource source) => source switch
    {
        PaperSource.Flatbed when caps.PaperSourceCaps?.SupportsFlatbed == true => caps.FlatbedCaps,
        PaperSource.Feeder when caps.PaperSourceCaps?.SupportsFeeder == true => caps.FeederCaps,
        PaperSource.Duplex when caps.PaperSourceCaps?.SupportsDuplex == true => caps.DuplexCaps,
        _ => null
    };

    public static string? Error(ScanSettings settings, ScanCaps caps)
    {
        var source = Source(caps, settings.Source);
        if (source is null) return "Select a supported paper source.";
        var supportedColor = settings.Color switch
        {
            BitDepth.Color => source.BitDepthCaps?.SupportsColor,
            BitDepth.Grayscale => source.BitDepthCaps?.SupportsGrayscale,
            BitDepth.BlackAndWhite => source.BitDepthCaps?.SupportsBlackAndWhite,
            _ => false
        };
        if (supportedColor != true) return "Select a supported color mode.";
        if (settings.Dpi <= 0 || source.DpiCaps?.Values?.Contains(settings.Dpi) != true) return "Select a supported DPI.";
        var width = settings.Size.WidthInMm;
        var height = settings.Size.HeightInMm;
        if (width is <= 0 or > 2000 || height is <= 0 or > 2000) return "Paper dimensions must be greater than zero and at most 2000 mm.";
        if (decimal.Round(width * settings.Dpi / 25.4m) < 1 || decimal.Round(height * settings.Dpi / 25.4m) < 1)
            return "Paper must cover at least one pixel at the selected DPI.";
        if (!Enum.IsDefined(settings.Alignment)) return "Select a supported alignment.";
        if (settings.Alignment != HorizontalAlign.Right && source.PageSizeCaps?.ScanArea is null)
            return "Center and far-edge alignment require reported source bounds. Choose Origin edge or another source.";
        if (source.PageSizeCaps?.Fits(settings.Size) == false) return "The paper size exceeds this source's scan bounds.";
        var standard = new[] { PageSize.A4, PageSize.Letter }.Any(size => Math.Abs(size.WidthInMm - width) < .001m && Math.Abs(size.HeightInMm - height) < .001m);
        if ((settings.CustomSize || !standard) && (source.PageSizeCaps?.SupportsCustomPageSize != true
            || source.PageSizeCaps.ScanArea is not { } bounds || width > bounds.WidthInMm || height > bounds.HeightInMm))
            return "Custom paper requires reported bounds and custom-size support; keep its dimensions inside those bounds.";
        if (settings.Area is not null && !settings.Area.Fits(width, height)) return "The scan area must have positive dimensions and stay inside the selected paper (X + width, Y + height).";
        if (settings.Area is { } region)
        {
            try { region.ToPixels(settings.Dpi, int.MaxValue, int.MaxValue); }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return "The scan area must cover at least one pixel at the selected DPI."; }
        }
        return null;
    }
}
