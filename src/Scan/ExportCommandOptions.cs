using NAPS2.Images;
using NAPS2.Pdf;

namespace Scan;

public sealed class ExportCommandOptions
{
    public bool Documents { get; init; }
    public required ExportFormat Format { get; init; }
    public required string Destination { get; init; }
    public required ExportOptions Options { get; init; }
    public PdfOcrOptions? Ocr { get; init; }

    public static ExportCommandOptions Parse(string[] args, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var flags = new HashSet<string> { "--searchable", "--json", "--no-jbig2", "--rasterize-pdf" };
        var values = new HashSet<string> { "--format", "--output", "--image-format", "--quality", "--tiff-compression",
            "--pdf-compression", "--pdfa", "--title", "--author", "--subject", "--keywords", "--creator",
            "--pdf-open-password-env", "--pdf-owner-password-env", "--deny", "--ocr-language" };
        var supplied = new Dictionary<string, string>();
        for (var i = 2; i < args.Length; i++)
        {
            var name = args[i];
            if (!flags.Contains(name) && !values.Contains(name)) throw new ArgumentException("Unknown export option. Use esyuscan --help.");
            if (supplied.ContainsKey(name)) throw new ArgumentException($"Duplicate {name}.");
            if (flags.Contains(name)) supplied.Add(name, "true");
            else
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {name}.");
                supplied.Add(name, args[i]);
            }
        }
        string? Get(string name) => supplied.GetValueOrDefault(name);
        var format = Get("--format") switch
        {
            "images" => ExportFormat.Images,
            "pdfs" => ExportFormat.SeparatePdfs,
            "pdf" or "documents" => ExportFormat.CombinedPdf,
            "tiff" => ExportFormat.CombinedTiff,
            _ => throw new ArgumentException("Format must be images, pdfs, pdf, documents, or tiff (one multipage TIFF).")
        };
        var pdf = ExportOptions.IsPdf(format);
        if (format != ExportFormat.Images && supplied.ContainsKey("--image-format")) throw new ArgumentException("--image-format requires --format images.");
        if (!pdf && supplied.Keys.Any(name => name.StartsWith("--pdf", StringComparison.Ordinal) || name is "--no-jbig2" or "--rasterize-pdf" or "--deny"
            or "--title" or "--author" or "--subject" or "--keywords" or "--creator"))
            throw new ArgumentException("PDF options require --format pdf or pdfs.");
        var imageFormat = Get("--image-format") switch
        {
            null or "png" => ImageOutput.Png,
            "jpeg" or "jpg" => ImageOutput.Jpeg,
            "tiff" or "tif" => ImageOutput.Tiff,
            _ => throw new ArgumentException("Image format must be png, jpeg, or tiff.")
        };
        var compression = Get("--tiff-compression") switch
        {
            null or "auto" => TiffCompressionType.Auto,
            "lzw" => TiffCompressionType.Lzw,
            "g4" => TiffCompressionType.Ccitt4,
            "none" => TiffCompressionType.None,
            _ => throw new ArgumentException("TIFF compression must be auto, lzw, g4, or none; JPEG-in-TIFF is not supported by NAPS2 1.6.0.")
        };
        if (supplied.ContainsKey("--tiff-compression") && format != ExportFormat.CombinedTiff && !(format == ExportFormat.Images && imageFormat == ImageOutput.Tiff))
            throw new ArgumentException("--tiff-compression requires TIFF output.");
        var pdfCompression = Get("--pdf-compression") switch
        {
            null or "lossless" => PdfImageCompression.Lossless,
            "jpeg" => PdfImageCompression.Jpeg,
            _ => throw new ArgumentException("PDF compression must be lossless or jpeg.")
        };
        var compat = Get("--pdfa") switch
        {
            null or "none" => PdfCompat.Default,
            "1b" => PdfCompat.PdfA1B,
            "2b" => PdfCompat.PdfA2B,
            "3b" => PdfCompat.PdfA3B,
            "3u" => PdfCompat.PdfA3U,
            _ => throw new ArgumentException("PDF/A must be none, 1b, 2b, 3b, or 3u.")
        };
        int? quality = null;
        if (Get("--quality") is { } qualityText)
        {
            if (!int.TryParse(qualityText, out var parsed)) throw new ArgumentException("JPEG quality must be an integer from 1 to 100.");
            quality = parsed;
        }
        var denied = Get("--deny")?.Split(',').ToHashSet() ?? [];
        var permissions = new HashSet<string> { "print", "full-quality-print", "copy", "accessibility", "modify", "annotate", "forms", "assemble" };
        if (denied.Except(permissions).Any()) throw new ArgumentException("Unknown permission in --deny. Use print, full-quality-print, copy, accessibility, modify, annotate, forms, assemble.");
        var searchable = supplied.ContainsKey("--searchable");
        if (Get("--ocr-language") is not null && !searchable) throw new ArgumentException("Use --searchable with --ocr-language.");
        string? Secret(string name)
        {
            if (Get(name) is not { } variable) return null;
            if (string.IsNullOrEmpty(variable) || variable.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new ArgumentException("Password environment variable names must contain ASCII letters, digits or underscores.");
            var secret = environment(variable);
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException($"The environment variable for {name} is unset or empty.");
            return secret;
        }
        var options = new ExportOptions
        {
            ImageFormat = imageFormat,
            JpegQuality = quality,
            TiffCompression = compression,
            PdfCompression = pdfCompression,
            Compatibility = compat,
            AllowJbig2 = !supplied.ContainsKey("--no-jbig2"),
            RasterizePdfSources = supplied.ContainsKey("--rasterize-pdf"),
            Metadata = new PdfMetadata
            {
                Title = Get("--title") ?? "",
                Author = Get("--author") ?? "",
                Subject = Get("--subject") ?? "",
                Keywords = Get("--keywords") ?? "",
                Creator = Get("--creator") ?? "esyuscan"
            },
            Passwords = supplied.ContainsKey("--pdf-owner-password-env") || supplied.ContainsKey("--pdf-open-password-env")
                ? new PdfPasswords { Open = Secret("--pdf-open-password-env"), Owner = Secret("--pdf-owner-password-env") } : null,
            Permissions = new PdfPermissions
            {
                Printing = !denied.Contains("print"),
                FullQualityPrinting = !denied.Contains("print") && !denied.Contains("full-quality-print"),
                Copying = !denied.Contains("copy"),
                Accessibility = !denied.Contains("accessibility"),
                Modification = !denied.Contains("modify"),
                Annotations = !denied.Contains("annotate"),
                FormFilling = !denied.Contains("forms"),
                Assembly = !denied.Contains("assemble")
            }
        };
        var ocr = searchable ? new PdfOcrOptions(Get("--ocr-language") ?? "eng") : null;
        options.Validate(format, ocr, hasPreservedPdf: false);
        return new() { Documents = Get("--format") == "documents", Format = format, Destination = Get("--output") ?? throw new ArgumentException("Missing --output."), Options = options, Ocr = ocr };
    }
}
