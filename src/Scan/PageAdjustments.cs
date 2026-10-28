namespace Scan;

public enum PageCorrection { None, Document, Photo }

// SDK units: tone/sharpness in thousandths, clockwise rotation in degrees.
public sealed record PageAdjustments(int Brightness = 0, int Contrast = 0, int Sharpness = 0,
    double Angle = 0, bool FlipHorizontal = false, bool FlipVertical = false,
    PageCorrection Correction = PageCorrection.None)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsIdentity => Brightness == 0 && Contrast == 0 && Sharpness == 0 && Angle == 0
        && !FlipHorizontal && !FlipVertical && Correction == PageCorrection.None;

    public void Validate()
    {
        if (Brightness is < -1000 or > 1000 || Contrast is < -1000 or > 1000 || Sharpness is < 0 or > 1000
            || !double.IsFinite(Angle) || Math.Abs(Angle) > 180 || !Enum.IsDefined(Correction))
            throw new InvalidDataException("Invalid page adjustments: brightness/contrast must be -1000–1000, sharpening 0–1000, and rotation -180–180 degrees.");
    }
}
