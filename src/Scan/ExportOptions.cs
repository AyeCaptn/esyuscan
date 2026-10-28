using System.Text.Json.Serialization;
using NAPS2.Images;
using NAPS2.Pdf;

namespace Scan;

public enum ImageOutput { Png, Jpeg, Tiff }
public enum PdfImageCompression { Lossless, Jpeg }

// Export-only values. Deliberately separate from session manifests and scan presets.
// Classes (not records) avoid generated ToString methods exposing runtime credentials.
public sealed class PdfPasswords
{
    [JsonIgnore] public string? Open { get; init; }
    [JsonIgnore] public string? Owner { get; init; }
}

public sealed record PdfPermissions
{
    public bool Printing { get; init; } = true;
    public bool FullQualityPrinting { get; init; } = true;
    public bool Copying { get; init; } = true;
    public bool Accessibility { get; init; } = true;
    public bool Modification { get; init; } = true;
    public bool Annotations { get; init; } = true;
    public bool FormFilling { get; init; } = true;
    public bool Assembly { get; init; } = true;
}

public sealed class ExportOptions
{
    public ImageOutput ImageFormat { get; init; } = ImageOutput.Png;
    public int? JpegQuality { get; init; }
    public int EffectiveQuality => JpegQuality ?? 90;
    public TiffCompressionType TiffCompression { get; init; } = TiffCompressionType.Auto;
    public PdfImageCompression PdfCompression { get; init; } = PdfImageCompression.Lossless;
    public bool AllowJbig2 { get; init; } = true;
    public PdfCompat Compatibility { get; init; } = PdfCompat.Default;
    public PdfMetadata Metadata { get; init; } = new() { Creator = "esyuscan" };
    public bool RasterizePdfSources { get; init; }
    public PdfPermissions Permissions { get; init; } = new();
    [JsonIgnore] public PdfPasswords? Passwords { get; init; }
    public bool Encrypt => Passwords is not null;

    public static bool IsPdf(ExportFormat format) => format is ExportFormat.CombinedPdf or ExportFormat.SeparatePdfs;
    public bool IsTiff(ExportFormat format) => format == ExportFormat.CombinedTiff || format == ExportFormat.Images && ImageFormat == ImageOutput.Tiff;

    public void Validate(ExportFormat format, PdfOcrOptions? ocr, bool hasPreservedPdf)
    {
        if (!Enum.IsDefined(format) || !Enum.IsDefined(ImageFormat) || !Enum.IsDefined(TiffCompression)
            || !Enum.IsDefined(PdfCompression) || !Enum.IsDefined(Compatibility))
            throw new ArgumentException("Unknown export setting.");
        var pdf = IsPdf(format);
        if (format != ExportFormat.Images && ImageFormat != ImageOutput.Png)
            throw new ArgumentException("Image format applies only to separate images. Use tiff for one multipage TIFF.");
        if (!pdf && ocr is not null) throw new ArgumentException("Searchable PDF OCR is only available for pdf and pdfs exports.");
        if (!IsTiff(format) && TiffCompression != TiffCompressionType.Auto)
            throw new ArgumentException("TIFF compression requires TIFF output.");
        if (JpegQuality is < 1 or > 100) throw new ArgumentException("JPEG quality must be an integer from 1 to 100.");
        if (JpegQuality is not null && !(format == ExportFormat.Images && ImageFormat == ImageOutput.Jpeg || pdf && PdfCompression == PdfImageCompression.Jpeg))
            throw new ArgumentException("JPEG quality requires JPEG images or JPEG compression in PDF.");
        if (!pdf && (PdfCompression != PdfImageCompression.Lossless || !AllowJbig2 || Compatibility != PdfCompat.Default
            || RasterizePdfSources || Encrypt || Permissions != new PdfPermissions() || Metadata != new PdfMetadata { Creator = "esyuscan" }))
            throw new ArgumentException("PDF metadata, compression, compatibility, passwords and permissions require PDF output.");
        if (!pdf) return;
        if (Compatibility != PdfCompat.Default && Encrypt)
            throw new ArgumentException("PDF/A cannot be encrypted. Choose standard PDF or remove passwords.");
        if (Compatibility != PdfCompat.Default && hasPreservedPdf && !RasterizePdfSources)
            throw new ArgumentException("NAPS2 cannot guarantee PDF/A for passed-through PDF content. Enable rasterize PDF sources (loses existing text/vectors), optionally with OCR, or choose standard PDF.");
        if (!Encrypt && Permissions != new PdfPermissions())
            throw new ArgumentException("PDF permissions require encryption and an owner password.");
        if (!Permissions.Printing && Permissions.FullQualityPrinting)
            throw new ArgumentException("Full-quality printing cannot be allowed when printing is disabled.");
        if (Encrypt)
        {
            if (string.IsNullOrEmpty(Passwords!.Owner)) throw new ArgumentException("Encryption requires an owner password; the open password is optional.");
            ValidatePassword(Passwords.Owner);
            if (!string.IsNullOrEmpty(Passwords.Open))
            {
                ValidatePassword(Passwords.Open);
                if (Passwords.Owner == Passwords.Open) throw new ArgumentException("Use different open and owner passwords.");
            }
        }
        // This SDK interpolates metadata directly into XMP without XML escaping. Refuse rather than emit invalid PDF/A.
        if (Compatibility != PdfCompat.Default && new[] { Metadata.Title, Metadata.Author, Metadata.Subject, Metadata.Keywords, Metadata.Creator }
            .Any(value => value.Any(c => c is '<' or '>' or '&' or '"' or '\'' || char.IsControl(c))))
            throw new ArgumentException("NAPS2 1.6.0 PDF/A metadata cannot contain XML punctuation (< > & quotes) or control characters. Use plain metadata or standard PDF.");
    }

    private static void ValidatePassword(string value)
    {
        // PdfSharp's legacy RC4 handler uses at most 32 single-byte characters. Avoid truncation/encoding surprises.
        if (value.Length > 32 || value.Any(c => c < 32 || c > 126))
            throw new ArgumentException("NAPS2 1.6.0 passwords must contain 1–32 printable ASCII characters (128-bit RC4; no AES support).");
    }

    public PdfExportParams ToPdfParams() => new()
    {
        Compat = Compatibility,
        Metadata = Metadata,
        Compression = new() { AllowJbig2 = AllowJbig2 },
        Encryption = new()
        {
            EncryptPdf = Encrypt,
            UserPassword = Passwords?.Open,
            OwnerPassword = Passwords?.Owner,
            AllowPrinting = Permissions.Printing,
            AllowFullQualityPrinting = Permissions.FullQualityPrinting,
            AllowContentCopying = Permissions.Copying,
            AllowContentCopyingForAccessibility = Permissions.Accessibility,
            AllowDocumentModification = Permissions.Modification,
            AllowAnnotations = Permissions.Annotations,
            AllowFormFilling = Permissions.FormFilling,
            AllowDocumentAssembly = Permissions.Assembly
        }
    };
}
