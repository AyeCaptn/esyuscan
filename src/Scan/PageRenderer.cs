using System.Reflection;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Images.Transforms;

namespace Scan;

// Pixel edges in the deskewed, unrotated original canvas. Right/bottom are exclusive.
public sealed record PageCrop(int X, int Y, int Width, int Height)
{
    public void Validate(int width = int.MaxValue, int height = int.MaxValue)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0
            || (long)X + Width > width || (long)Y + Height > height)
            throw new InvalidDataException("The crop must keep at least one pixel and stay inside the page.");
    }

    public PageCrop RotateClockwise(int width, int height) => new(height - Y - Height, X, Height, Width);

    public static PageCrop FromDisplay(PageCrop crop, int rotation, int originalWidth, int originalHeight)
    {
        var width = rotation is 90 or 270 ? originalHeight : originalWidth;
        var height = rotation is 90 or 270 ? originalWidth : originalHeight;
        crop.Validate(width, height);
        for (var angle = rotation; angle < 360 && angle > 0; angle += 90)
        {
            crop = crop.RotateClockwise(width, height);
            (width, height) = (height, width);
        }
        return crop;
    }
}

public static class PageRenderer
{
    // Full-resolution order: deskew → crop → quarter-turn → angle → flips → correction → tone → sharpen.
    public static IMemoryImage Render(Session session, SessionPage page, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        page.Adjustments?.Validate();
        IMemoryImage image = new GtkImageContext().Load(session.OriginalPath(page));
        try
        {
            token.ThrowIfCancellationRequested();
            if (page.DeskewAngle != 0) image = image.PerformTransform(new RotationTransform(page.DeskewAngle));
        }
        catch { image.Dispose(); throw; }
        return Finish(image, page.Crop, page.Rotation, page.Adjustments, token);
    }

    // Crop selection uses the deskewed canvas; its result preview includes all remaining edits.
    public static MemoryStream Png(IMemoryImage deskewedCanvas, PageCrop? crop, int rotation,
        PageAdjustments? adjustments = null, CancellationToken token = default)
    {
        using var image = Finish(deskewedCanvas.Clone(), crop, rotation, adjustments, token);
        return image.SaveToMemoryStream(ImageFileFormat.Png);
    }

    private static IMemoryImage Finish(IMemoryImage image, PageCrop? crop, int rotation,
        PageAdjustments? adjustments, CancellationToken token)
    {
        try
        {
            void Transform(Transform transform)
            {
                token.ThrowIfCancellationRequested();
                if (!transform.IsNull) image = image.PerformTransform(transform);
            }
            token.ThrowIfCancellationRequested();
            adjustments?.Validate();
            if (crop is not null)
            {
                crop.Validate(image.Width, image.Height);
                Transform(new CropTransform(crop.X, image.Width - crop.X - crop.Width,
                    crop.Y, image.Height - crop.Y - crop.Height));
            }
            Transform(new RotationTransform(rotation));
            if (adjustments is { } edit)
            {
                Transform(new RotationTransform(edit.Angle));
                if (edit.FlipHorizontal) { token.ThrowIfCancellationRequested(); image = SdkFlip.Apply(image, true); }
                if (edit.FlipVertical) { token.ThrowIfCancellationRequested(); image = SdkFlip.Apply(image, false); }
                if (edit.Correction != PageCorrection.None)
                    Transform(new CorrectionTransform(edit.Correction == PageCorrection.Document ? CorrectionMode.Document : CorrectionMode.Photo));
                Transform(new BrightnessTransform(edit.Brightness));
                Transform(new TrueContrastTransform(edit.Contrast));
                Transform(new SharpenTransform(edit.Sharpness));
            }
            token.ThrowIfCancellationRequested();
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public static MemoryStream Png(Session session, SessionPage page, CancellationToken token = default)
    {
        using var image = Render(session, page, token);
        var stream = image.SaveToMemoryStream(ImageFileFormat.Png);
        if (token.IsCancellationRequested) { stream.Dispose(); token.ThrowIfCancellationRequested(); }
        return stream;
    }

    public static void ValidateCrop(Session session, SessionPage page)
    {
        if (page.Crop is null) return;
        using var original = new GtkImageContext().Load(session.OriginalPath(page));
        page.Crop.Validate(original.Width, original.Height);
    }

    public static double DetectDeskew(Session session, SessionPage page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Detect afresh on the full original in the user's current quarter-turn orientation.
        using var image = Render(session, page with { DeskewAngle = 0, Crop = null, Adjustments = null }, token);
        var angle = SdkDeskew.Detect(image);
        token.ThrowIfCancellationRequested();
        return angle;
    }
}

internal static class SdkDeskew
{
    // NAPS2.Sdk 1.6.0 exposes AutoDeskew for scans but keeps its Hough detector internal.
    // Bind only the upstream method; do not copy or replace its algorithm. Guarded by integration tests.
    private static readonly Func<IMemoryImage, RotationTransform> DetectTransform =
        (typeof(ProcessedImage).Assembly.GetType("NAPS2.Images.Deskewer")
            ?.GetMethod("GetDeskewTransform", BindingFlags.Public | BindingFlags.Static)
            ?? throw new NotSupportedException("This NAPS2 SDK does not provide esyuscan's deskew detector."))
        .CreateDelegate<Func<IMemoryImage, RotationTransform>>();

    public static double Detect(IMemoryImage image)
    {
        var angle = DetectTransform(image).Angle;
        return angle > 180 ? angle - 360 : angle;
    }
}

// NAPS2 1.6 has no FlipTransform. Use its public GTK backend and the upstream GdkPixbuf
// mirror operation, preserving the SDK image metadata and ownership contract.
internal static class SdkFlip
{
    public static IMemoryImage Apply(IMemoryImage image, bool horizontal)
    {
        if (image is not GtkImage gtk) throw new NotSupportedException("Flipping requires esyuscan's GTK image backend.");
        var result = new GtkImage(gtk.Pixbuf.Flip(horizontal))
        {
            OriginalFileFormat = image.OriginalFileFormat,
            LogicalPixelFormat = image.LogicalPixelFormat
        };
        result.SetResolution(image.HorizontalResolution, image.VerticalResolution);
        image.Dispose();
        return result;
    }
}
