using System.Globalization;
using NAPS2.Images;
using NAPS2.Scan;

namespace Scan;

public sealed partial class MainViewModel
{
    private string geometryUnit = "mm";
    private string paperWidth = "210", paperHeight = "297";
    private string areaX = "0", areaY = "0", areaWidth = "210", areaHeight = "297";
    private bool selectScanArea;
    private ScanChoice<HorizontalAlign> alignment = AlignmentChoices[0];

    public static IReadOnlyList<string> GeometryUnits { get; } = ["mm", "in"];
    // NAPS2 calls the zero-offset edge Right in both eSCL and SANE.
    public static IReadOnlyList<ScanChoice<HorizontalAlign>> AlignmentChoices { get; } =
    [new("Origin edge", HorizontalAlign.Right), new("Center", HorizontalAlign.Center), new("Far edge", HorizontalAlign.Left)];
    public string GeometryUnit
    {
        get => geometryUnit;
        set
        {
            if (value is not ("mm" or "in") || value == geometryUnit) return;
            var factor = value == "in" ? 1 / 25.4m : 25.4m;
            string Convert(string text)
            {
                try { return TryNumber(text, out var number) ? Display(decimal.Round(number * factor, value == "mm" ? 6 : 8)) : text; }
                catch (OverflowException) { return "Invalid dimension"; }
            }
            // Convert before setting the unit; one final settings edit persists the complete result.
            paperWidth = Convert(paperWidth); paperHeight = Convert(paperHeight);
            areaX = Convert(areaX); areaY = Convert(areaY);
            areaWidth = Convert(areaWidth); areaHeight = Convert(areaHeight);
            Set(ref geometryUnit, value);
            NotifyGeometry();
            SettingsEdited();
        }
    }
    public string PaperWidth { get => paperWidth; set { if (Set(ref paperWidth, value)) SettingsEdited(); } }
    public string PaperHeight { get => paperHeight; set { if (Set(ref paperHeight, value)) SettingsEdited(); } }
    public string AreaX { get => areaX; set { if (Set(ref areaX, value)) SettingsEdited(); } }
    public string AreaY { get => areaY; set { if (Set(ref areaY, value)) SettingsEdited(); } }
    public string AreaWidth { get => areaWidth; set { if (Set(ref areaWidth, value)) SettingsEdited(); } }
    public string AreaHeight { get => areaHeight; set { if (Set(ref areaHeight, value)) SettingsEdited(); } }
    public bool SelectScanArea
    {
        get => selectScanArea;
        set
        {
            if (!Set(ref selectScanArea, value)) return;
            if (value && !applyingSettings && EffectiveSize() is { } paper)
            {
                AreaX = AreaY = "0";
                AreaWidth = Display(FromMm(paper.WidthInMm));
                AreaHeight = Display(FromMm(paper.HeightInMm));
            }
            SettingsEdited();
        }
    }
    public ScanChoice<HorizontalAlign> SelectedAlignment { get => alignment; set { if (Set(ref alignment, value)) SettingsEdited(); } }
    public bool IsCustomSize => SelectedSize?.Label == "Custom";
    public bool CanEditCustomSize => CanChangeScanSettings && IsCustomSize;
    public string GeometryExplanation
    {
        get
        {
            var bounds = CurrentSourceCapabilities?.PageSizeCaps?.ScanArea;
            var limits = bounds is null ? "Scan bounds not reported. Custom paper is unavailable."
                : $"Source bounds: {Display(FromMm(bounds.WidthInMm))} × {Display(FromMm(bounds.HeightInMm))} {GeometryUnit}.";
            var paper = EffectiveSize();
            var dimensions = paper is null ? "" : $" Paper: {Display(FromMm(paper.WidthInMm))} × {Display(FromMm(paper.HeightInMm))} {GeometryUnit}.";
            return limits + dimensions + " Origin edge starts at the source's minimum X; far edge uses the opposite side. Area X/Y start at the top-left of the aligned paper. esyuscan acquires the whole paper, then crops the area before saving (after auto-deskew).";
        }
    }
    public string GeometryError
    {
        get
        {
            if (!capabilitiesReady || SelectedDevice is null || SelectedSource is null || SelectedColor is null || SelectedSize is null) return "";
            try { return ScanValidation.Error(BuildScanSettings(), capabilities!) ?? ""; }
            catch (Exception ex) when (ex is ArgumentException or OverflowException) { return ex.Message; }
        }
    }
    public bool HasGeometryError => GeometryError.Length > 0;
    public ScanGeometryDiagram? GeometryDiagram
    {
        get
        {
            if (!HasValidScanSettings || EffectiveSize() is not { } paper) return null;
            var bounds = CurrentSourceCapabilities?.PageSizeCaps?.ScanArea ?? paper;
            var settings = BuildScanSettings();
            return new(bounds.WidthInMm, bounds.HeightInMm, paper.WidthInMm, paper.HeightInMm, settings.Alignment, settings.Area);
        }
    }

    private static bool TryNumber(string text, out decimal value) => decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
        CultureInfo.CurrentCulture, out value);
    private static string Display(decimal value) => value.ToString("0.########", CultureInfo.CurrentCulture);
    private decimal ToMm(string text)
    {
        if (!TryNumber(text, out var number)) throw new ArgumentException("Enter valid dimensions and coordinates using the displayed unit.");
        return GeometryUnit == "in" ? decimal.Round(number * 25.4m, 6) : number;
    }
    private decimal FromMm(decimal number) => GeometryUnit == "in" ? number / 25.4m : number;
    private PageSize? EffectiveSize()
    {
        if (!IsCustomSize) return SelectedSize?.Value;
        try { return new(ToMm(PaperWidth), ToMm(PaperHeight), PageSizeUnit.Millimetre); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { return null; }
    }
    private ScanSettings BuildScanSettings() => new(SelectedDevice!, SelectedSource!.Value, SelectedColor!.Value, Dpi,
        EffectiveSize() ?? throw new ArgumentException("Enter valid paper dimensions."), AutoDeskew, SelectedAlignment.Value,
        SelectScanArea ? new(ToMm(AreaX), ToMm(AreaY), ToMm(AreaWidth), ToMm(AreaHeight)) : null, IsCustomSize);

    public ScanSettings CurrentScanSettings()
    {
        if (!HasValidScanSettings) throw new InvalidOperationException("Choose valid scanner settings and scan geometry before scanning. " + GeometryError);
        return BuildScanSettings();
    }

    private ScanGeometry? CaptureGeometry()
    {
        var settings = BuildScanSettings();
        if (!IsCustomSize && settings.Alignment == HorizontalAlign.Right && settings.Area is null && GeometryUnit == "mm") return null;
        return new(settings.Size.WidthInMm, settings.Size.HeightInMm, settings.Alignment, settings.Area, GeometryUnit);
    }

    private void RestoreGeometry(ScanGeometry? geometry)
    {
        geometryUnit = geometry?.Unit ?? "mm";
        var paper = SelectedSize?.Value;
        paperWidth = Display(FromMm(geometry?.WidthMm ?? paper?.WidthInMm ?? 210));
        paperHeight = Display(FromMm(geometry?.HeightMm ?? paper?.HeightInMm ?? 297));
        alignment = AlignmentChoices.First(c => c.Value == (geometry?.Alignment ?? HorizontalAlign.Right));
        var area = geometry?.Area;
        selectScanArea = area is not null;
        areaX = Display(FromMm(area?.X ?? 0)); areaY = Display(FromMm(area?.Y ?? 0));
        areaWidth = Display(FromMm(area?.Width ?? paper?.WidthInMm ?? 210));
        areaHeight = Display(FromMm(area?.Height ?? paper?.HeightInMm ?? 297));
        NotifyGeometry();
    }

    private void NotifyGeometry()
    {
        foreach (var property in new[] { nameof(GeometryUnit), nameof(PaperWidth), nameof(PaperHeight), nameof(AreaX), nameof(AreaY),
            nameof(AreaWidth), nameof(AreaHeight), nameof(SelectScanArea), nameof(SelectedAlignment), nameof(IsCustomSize),
            nameof(CanEditCustomSize), nameof(GeometryDiagram), nameof(GeometryExplanation), nameof(GeometryError), nameof(HasGeometryError) }) Notify(property);
    }
}
