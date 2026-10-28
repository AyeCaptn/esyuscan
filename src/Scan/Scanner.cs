using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Images.Transforms;
using NAPS2.ImportExport;
using NAPS2.Pdf;
using NAPS2.Ocr;
using NAPS2.Util;
using NAPS2.Scan;
using PdfSharpCore.Pdf.IO;

namespace Scan;

public enum ExportFormat { Images, SeparatePdfs, CombinedPdf, CombinedTiff }
public record ScanSettings(ScanDevice Device, PaperSource Source, BitDepth Color, int Dpi, PageSize Size, bool AutoDeskew = false,
    HorizontalAlign Alignment = HorizontalAlign.Right, ScanRegion? Area = null, bool CustomSize = false)
{
    public ScanOptions ToOptions() => new()
    {
        Device = Device,
        PaperSource = Source,
        BitDepth = Color,
        Dpi = Dpi,
        PageSize = Size,
        MaxQuality = true,
        AutoDeskew = AutoDeskew,
        PageAlign = Alignment
    };
}

public sealed class Scanner : IScannerDiscovery
{
    private readonly ScanningContext context = new(new GtkImageContext());
    private readonly IScannerBackend backend;
    private readonly ScannerDevices devices;
    private readonly string workingDirectory;
    private readonly OcrRuntime ocr;

    public Scanner(string? temporaryRoot = null, OcrRuntime? ocr = null, IScannerBackend? backend = null)
    {
        this.ocr = ocr ?? new OcrRuntime();
        var root = temporaryRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "esyuscan");
        workingDirectory = Path.Combine(root, $"temporary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(workingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        context.TempFolderPath = workingDirectory;
        context.FileStorageManager = new FileStorageManager(workingDirectory);
        this.backend = backend ?? new SdkScannerDevices(new ScanController(context));
        devices = new ScannerDevices(this.backend);
    }

    public async Task<List<ScanDevice>> Devices(CancellationToken token = default) => [.. (await Discover(token)).Devices];
    public Task<ScannerDiscovery> Discover(CancellationToken token) => devices.Discover(token);
    public Task<ScanCaps> Capabilities(ScanDevice device, CancellationToken token) => devices.Capabilities(device, token);
    public Task<(ScanDevice Device, ScanCaps Caps)> Probe(Driver driver, string address, string name, CancellationToken token) => devices.Probe(driver, address, name, token);

    // SDK 1.6.0 retries an offline SANE ID by display name. An empty transport name disables
    // that heuristic (SDK-generated SANE names always contain a backend suffix), preserving the exact driver/ID used by sane_open. Never persist it.
    public static ScanOptions ExactDeviceOptions(ScanSettings settings)
    {
        var options = settings.ToOptions();
        if (options.Device!.Driver == Driver.Sane)
            options.Device = options.Device with { Name = "" };
        return options;
    }

    public async Task Scan(Session session, ScanSettings settings, Action pageAdded,
        Action<double> progress, CancellationToken token)
    {
        var caps = await Capabilities(settings.Device, token);
        if (ScanValidation.Error(settings, caps) is { } error) throw new InvalidOperationException(error);
        token.ThrowIfCancellationRequested();
        var options = ExactDeviceOptions(settings);
        await foreach (var image in backend.Scan(options, progress, token))
        {
            using (image)
            {
                // Each completed page is durable before the feeder starts the next page.
                var target = session.NextOriginalPath();
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (settings.Area is { } area)
                    {
                        // SDK has no portable X/Y origin option. Acquire the aligned paper, then
                        // apply a physical-coordinate selection before saving the session original.
                        using var rendered = image.Render();
                        decimal Resolution(float value) => float.IsFinite(value) && value > 0 ? (decimal)value : settings.Dpi;
                        var crop = area.ToPixels(Resolution(rendered.HorizontalResolution), rendered.Width, rendered.Height,
                            Resolution(rendered.VerticalResolution));
                        using var selected = rendered.Clone().PerformTransform(new CropTransform(crop.X,
                            rendered.Width - crop.X - crop.Width, crop.Y, rendered.Height - crop.Y - crop.Height));
                        selected.Save(target, ImageFileFormat.Png);
                    }
                    else image.Save(target);
                    FlushFile(target);
                    session.Add(target);
                }
                catch { File.Delete(target); throw; }
                pageAdded();
            }
        }
        token.ThrowIfCancellationRequested();
    }

    public async Task Import(Session session, IEnumerable<string> files, Action pageAdded, CancellationToken token)
    {
        var importer = new FileImporter(new PdfImporter(context, new RejectPdfPasswords()), new ImageImporter(context));
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            string? sourcePath = null;
            var savedPages = 0;
            try
            {
                if (IsPdf(file))
                {
                    sourcePath = session.NextPdfSourcePath();
                    await CopySource(file, sourcePath, token);
                }
                await foreach (var image in importer.Import(sourcePath ?? file, progress: token))
                {
                    using (image)
                    {
                        token.ThrowIfCancellationRequested();
                        var target = session.NextOriginalPath();
                        try
                        {
                            // PNGs are previews/fallbacks for PDF pages; the copied PDF remains authoritative.
                            image.Save(target);
                            FlushFile(target);
                            token.ThrowIfCancellationRequested();
                            session.Add(target, sourcePath is null ? null
                                : new PdfPageSource(Path.GetFileName(sourcePath), savedPages));
                        }
                        catch { File.Delete(target); throw; }
                        savedPages++;
                        pageAdded();
                    }
                }
                token.ThrowIfCancellationRequested();
                if (savedPages == 0) throw new InvalidDataException("The file contains no importable pages.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new IOException($"Couldn't import '{Path.GetFileName(file)}': {error.Message}"
                    + (sourcePath is null ? "" : " The PDF may be damaged or use unsupported encryption. If password-protected, save an unlocked copy first."), error);
            }
            finally
            {
                // A partially imported PDF stays intact when any of its pages were already saved.
                if (sourcePath is not null && savedPages == 0) File.Delete(sourcePath);
            }
        }
    }

    public async Task Export(Session session, string destination, ExportFormat format, CancellationToken token,
        PdfOcrOptions? searchablePdf = null, Action<ExportProgress>? progress = null, ExportOptions? options = null, IReadOnlyCollection<string>? selectedPages = null)
    {
        RequireExportReady(session);
        var selected = selectedPages?.ToHashSet(StringComparer.Ordinal);
        if (selected is not null && selected.Any(id => !session.Pages.Any(page => page.Identity == id)))
            throw new ArgumentException("The selection contains a page outside this session.", nameof(selectedPages));
        var pages = session.Pages.Where(page => selected is null || selected.Contains(page.Identity)).ToArray();
        await ExportPages(session, pages, destination, format, token, searchablePdf, progress, options);
    }

    private static void RequireExportReady(Session session)
    {
        if (session.ManualDuplex is not null) throw new InvalidOperationException("Finish manual duplex review or keep scan order before exporting.");
        if (session.Batch is { } batch && (batch.Phase != BatchPhase.Review || !batch.Reviewed))
            throw new InvalidOperationException("Review batch boundaries and separator sheets before exporting.");
    }

    private async Task ExportPages(Session session, IReadOnlyList<SessionPage> pages, string destination,
        ExportFormat format, CancellationToken token, PdfOcrOptions? searchablePdf, Action<ExportProgress>? progress, ExportOptions? options = null)
    {
        token.ThrowIfCancellationRequested();
        options ??= new ExportOptions();
        if (pages.Count == 0) throw new InvalidOperationException("Select at least one page to export.");
        options.Validate(format, searchablePdf, pages.Any(page => page.PreservePdf));
        destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("The export already exists. Choose a different name.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The export's parent folder does not exist.");
        RequiredOcrEngine? requiredOcr = null;
        if (searchablePdf is not null)
        {
            progress?.Invoke(new(0, "Checking OCR engine and language…"));
            requiredOcr = new RequiredOcrEngine(await ocr.RequireEngine(searchablePdf, token), token);
        }
        var staging = Path.Combine(parent, $".esyuscan-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var images = new List<ProcessedImage>();
        var requiredOcrCounts = new List<int>();
        var preparation = Path.Combine(staging, "prepared");
        try
        {
            context.OcrEngine = requiredOcr;
            var importer = new FileImporter(context);
            var ocrParams = searchablePdf is null ? null : new OcrParams(searchablePdf.Language, TimeoutInSeconds: 300);
            foreach (var page in pages)
            {
                token.ThrowIfCancellationRequested();
                var loadedBefore = images.Count;
                // Edited pages use exactly the preview pipeline; unchanged PDFs retain their content.
                if (!ExportOptions.IsPdf(format) || !page.PreservePdf || options.RasterizePdfSources)
                {
                    using var rendered = PageRenderer.Render(session, page, token);
                    rendered.UpdateLogicalPixelFormat();
                    if (options.Compatibility == PdfCompat.PdfA1B && rendered.LogicalPixelFormat == ImagePixelFormat.ARGB32)
                        throw new NotSupportedException("NAPS2 1.6.0 cannot guarantee PDF/A-1b for transparent pages. Choose PDF/A-2b/3b/3u, or flatten the image before importing.");
                    // Explicit JPEG encoding is necessary: the SDK otherwise keeps PNG-origin pages lossless
                    // regardless of ImageMetadata.Quality. The upstream JPEG importer embeds these bytes directly.
                    using var encoded = rendered.SaveToMemoryStream(
                        ExportOptions.IsPdf(format) && options.PdfCompression == PdfImageCompression.Jpeg ? ImageFileFormat.Jpeg : ImageFileFormat.Png,
                        new ImageSaveOptions { Quality = options.EffectiveQuality, PixelFormatHint = ImagePixelFormat.RGB24 });
                    await foreach (var image in importer.Import(encoded, progress: token)) images.Add(image);
                    if (requiredOcr is not null)
                    {
                        progress?.Invoke(new(0.2 * images.Count / pages.Count, $"Recognizing final page pixels ({searchablePdf!.Language}) · {images.Count} of {pages.Count}…"));
                        using var canonicalPng = rendered.SaveToMemoryStream(ImageFileFormat.Png);
                        await SdkLosslessOcr.Recognize(context, requiredOcr, images[^1], canonicalPng, ocrParams!, token);
                    }
                }
                else
                {
                    var source = page.PdfSource!;
                    var count = 0;
                    await foreach (var image in importer.Import(session.PdfSourcePath(source),
                        new ImportParams { Slice = Slice.Item(source.PageIndex) }, progress: token))
                    {
                        if (searchablePdf is not null || page.Rotation != 0)
                        {
                            // Apply viewing orientation before the final exporter adds encryption/PDF metadata.
                            // OCR additionally needs normalized coordinates, preserving PDF objects.
                            using (image)
                            {
                                Directory.CreateDirectory(preparation);
                                var prepared = Path.Combine(preparation, $"{images.Count}.pdf");
                                if (!await new PdfExporter(context).Export(prepared, [image], progress: token))
                                    throw new OperationCanceledException(token);
                                RotatePdfPages(prepared, [page]);
                                if (searchablePdf is not null) NormalizePdfRotation(prepared);
                                await foreach (var rotated in importer.Import(prepared, progress: token)) images.Add(rotated);
                            }
                        }
                        else images.Add(image);
                        count++;
                    }
                    token.ThrowIfCancellationRequested();
                    if (count != 1) throw new InvalidDataException($"Session page could not be loaded: {page.FileName}");
                }
                token.ThrowIfCancellationRequested();
                if (images.Count != loadedBefore + 1) throw new InvalidDataException($"Session page could not be loaded: {page.FileName}");
                var needsOcr = searchablePdf is not null && SdkPdfText.RequiresOcr(images[^1]);
                requiredOcrCounts.Add(requiredOcrCounts.LastOrDefault() + (needsOcr ? 1 : 0));
                progress?.Invoke(new(0.2 * images.Count / pages.Count, $"Preparing page {images.Count} of {pages.Count}…"));
            }
            if (Directory.Exists(preparation)) Directory.Delete(preparation, recursive: true);
            var pdf = new PdfExporter(context);
            string ExportMessage(int done) => searchablePdf is null ? $"Exporting page {done} of {images.Count}…"
                : $"Recognizing text ({searchablePdf.Language}) and exporting · {done} of {images.Count} pages…";
            ProgressHandler PdfProgress(int offset, int total) => new((current, _) =>
                progress?.Invoke(new(0.2 + 0.75 * (offset + current) / total, ExportMessage(offset + current))), token);
            if (format is ExportFormat.CombinedPdf or ExportFormat.CombinedTiff)
            {
                var file = Path.Combine(staging, format == ExportFormat.CombinedPdf ? "document.pdf" : "document.tiff");
                if (format == ExportFormat.CombinedPdf)
                {
                    if (!await pdf.Export(file, images, options.ToPdfParams(), ocrParams, PdfProgress(0, images.Count))) throw new OperationCanceledException(token);
                    VerifyPdfProtection(file, options);
                }
                else SaveTiff(file, images, options, PdfProgress(0, images.Count));
                token.ThrowIfCancellationRequested();
                requiredOcr?.Verify(requiredOcrCounts.LastOrDefault());
                progress?.Invoke(new(0.95, "Publishing completed export…"));
                token.ThrowIfCancellationRequested();
                File.Move(file, destination);
            }
            else
            {
                for (var i = 0; i < images.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (format == ExportFormat.Images)
                    {
                        var extension = options.ImageFormat switch { ImageOutput.Jpeg => "jpg", ImageOutput.Tiff => "tiff", _ => "png" };
                        var file = Path.Combine(staging, $"page-{i + 1:000}.{extension}");
                        if (options.ImageFormat == ImageOutput.Tiff) SaveTiff(file, [images[i]], options, PdfProgress(i, images.Count));
                        else
                        {
                            using var rendered = images[i].Render();
                            rendered.Save(file, options.ImageFormat == ImageOutput.Jpeg ? ImageFileFormat.Jpeg : ImageFileFormat.Png,
                                new ImageSaveOptions { Quality = options.EffectiveQuality });
                        }
                    }
                    else
                    {
                        var file = Path.Combine(staging, $"page-{i + 1:000}.pdf");
                        if (!await pdf.Export(file, [images[i]], options.ToPdfParams(), ocrParams, PdfProgress(i, images.Count))) throw new OperationCanceledException(token);
                        VerifyPdfProtection(file, options);
                    }
                    progress?.Invoke(new(0.2 + 0.75 * (i + 1) / images.Count, ExportMessage(i + 1)));
                }
                token.ThrowIfCancellationRequested();
                // Lossless raster OCR can complete ahead of PDF pages. Verify the exact total
                // after all files are staged, before publishing any of them.
                requiredOcr?.Verify(requiredOcrCounts.LastOrDefault());
                progress?.Invoke(new(0.95, "Publishing completed export…"));
                token.ThrowIfCancellationRequested();
                Directory.Move(staging, destination);
            }
            progress?.Invoke(new(1, "Export complete."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (options.Encrypt)
        {
            // Neither SDK exceptions nor their inner exceptions may reach diagnostics containing credentials.
            throw new IOException("Encrypted PDF export failed; no export was published. Check the destination and PDF source compatibility. NAPS2 1.6.0 supports 128-bit RC4 only; try explicitly rasterizing PDF sources if passthrough fails.");
        }
        finally
        {
            if (requiredOcr is not null) await requiredOcr.Drain();
            context.OcrEngine = null;
            foreach (var image in images) image.Dispose();
            // Staging is a fresh, app-owned directory; originals and existing exports are never removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private void SaveTiff(string path, IList<ProcessedImage> images, ExportOptions options, ProgressHandler progress)
    {
        var rendered = new List<IMemoryImage>();
        try
        {
            foreach (var image in images)
            {
                progress.CancelToken.ThrowIfCancellationRequested();
                var frame = image.Render();
                rendered.Add(frame);
                frame.UpdateLogicalPixelFormat();
                // The SDK would silently threshold color/grayscale to one bit when forced to CCITT4.
                if (options.TiffCompression == TiffCompressionType.Ccitt4 && frame.LogicalPixelFormat != ImagePixelFormat.BW1)
                    throw new ArgumentException("CCITT Group 4 requires black-and-white (1-bit) pages. Choose Auto, LZW or None to retain color/grayscale.");
            }
            if (!context.ImageContext.TiffWriter.SaveTiff(rendered, path, options.TiffCompression, progress))
                throw new OperationCanceledException(progress.CancelToken);
            progress.CancelToken.ThrowIfCancellationRequested();
        }
        finally { foreach (var image in rendered) image.Dispose(); }
    }

    private static void VerifyPdfProtection(string path, ExportOptions options)
    {
        if (!options.Encrypt) return;
        using var document = PdfReader.Open(path, options.Passwords!.Owner, PdfDocumentOpenMode.Import);
        var expected = options.Permissions;
        var actual = document.SecuritySettings;
        if (document.SecurityHandler.ValidatePassword(options.Passwords.Owner) != PasswordValidity.OwnerPassword
            || document.SecurityHandler.ValidatePassword(options.Passwords.Open ?? "") != PasswordValidity.UserPassword
            || actual.PermitPrint != expected.Printing || actual.PermitFullQualityPrint != expected.FullQualityPrinting
            || actual.PermitExtractContent != expected.Copying || actual.PermitAccessibilityExtractContent != expected.Accessibility
            || actual.PermitModifyDocument != expected.Modification || actual.PermitAnnotations != expected.Annotations
            || actual.PermitFormsFill != expected.FormFilling || actual.PermitAssembleDocument != expected.Assembly)
            throw new InvalidDataException("The SDK did not retain the requested PDF encryption and permissions.");
    }

    // Detection runs from durable originals, so cancellation never loses an acquired page.
    public async Task DetectBatchBarcodes(Session session, CancellationToken token, Action? changed = null)
    {
        if (session.Batch is not { Phase: BatchPhase.Review } batch)
            throw new InvalidOperationException("Stop scanning and review the batch first.");
        if (batch.Options.Grouping != BatchGrouping.Barcode) return;
        foreach (var page in batch.Scans.SelectMany(scan => scan.Pages).Where(page => !page.DetectionDone))
        {
            token.ThrowIfCancellationRequested();
            var barcode = await DetectBarcode(Path.Combine(session.DirectoryPath, "originals", page.FileName), token);
            session.RecordBatchBarcode(page.FileName, barcode.IsDetected ? barcode.DetectedText : null);
            changed?.Invoke();
        }
    }

    public async Task<Barcode> DetectBarcode(string path, CancellationToken token)
    {
        var importer = new FileImporter(context);
        var count = 0;
        var result = Barcode.NoDetection;
        await foreach (var image in importer.Import(path, new ImportParams
        {
            BarcodeDetectionOptions = new BarcodeDetectionOptions
            {
                DetectBarcodes = true,
                PatchTOnly = false,
                ZXingOptions = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = [ZXing.BarcodeFormat.QR_CODE, ZXing.BarcodeFormat.CODE_128]
                }
            }
        }, progress: token))
        {
            using (image) { result = image.PostProcessingData.Barcode; count++; }
        }
        token.ThrowIfCancellationRequested();
        if (count != 1 || !result.IsDetectionAttempted)
            throw new InvalidDataException("NAPS2 did not complete barcode detection for this page.");
        return result;
    }

    public async Task ExportBatchDocuments(Session session, string destination, CancellationToken token,
        PdfOcrOptions? searchablePdf = null, Action<ExportProgress>? progress = null, ExportOptions? options = null)
    {
        RequireExportReady(session);
        var documents = session.BatchDocuments();
        if (documents.Count == 0) throw new InvalidOperationException("The batch has no included document pages.");
        destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("The export already exists. Choose a different name.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The export's parent folder does not exist.");
        token.ThrowIfCancellationRequested();
        var staging = Path.Combine(parent, $".esyuscan-batch-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var captured = documents.SelectMany(document => document.PageIds).ToHashSet();
            var byId = session.Pages.Where(page => captured.Contains(page.FileName)).ToDictionary(page => page.FileName);
            foreach (var document in documents)
            {
                token.ThrowIfCancellationRequested();
                await ExportPages(session, document.PageIds.Select(id => byId[id]).ToArray(),
                    Path.Combine(staging, $"document-{document.Number:000}.pdf"), ExportFormat.CombinedPdf,
                    token, searchablePdf, update => progress?.Invoke(new(
                        (document.Number - 1 + update.Fraction) / documents.Count,
                        $"Document {document.Number} of {documents.Count} · {update.Message}")), options);
            }
            // Explicit page identities make the published order auditable without barcode-derived filenames.
            File.WriteAllText(Path.Combine(staging, "documents.json"), System.Text.Json.JsonSerializer.Serialize(documents));
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static void RotatePdfPages(string path, IReadOnlyList<SessionPage> pages)
    {
        if (!pages.Any(page => page.PreservePdf && page.Rotation != 0)) return;
        // NAPS2's RotationTransform rasterizes PDF pages. Use its bundled PdfSharp instead:
        // /Rotate changes viewing orientation without changing text, paths, or image objects.
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        // PdfSharp otherwise swaps the media box when saving an imported /Rotate 90/270 page.
        foreach (var pdfPage in document.Pages) pdfPage.Orientation = PdfSharpCore.PageOrientation.Portrait;
        for (var i = 0; i < pages.Count; i++)
            if (pages[i].PreservePdf)
                document.Pages[i].Rotate = (document.Pages[i].Rotate + pages[i].Rotation) % 360;
        document.Save(path);
    }

    private static void NormalizePdfRotation(string path)
    {
        // SDK 1.6.0 places OCR text outside the visible box on /Rotate pages. Normalize the
        // page's coordinates through upstream PdfSharp while retaining its text/vector objects.
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        foreach (var page in document.Pages)
        {
            var rotation = (page.Rotate % 360 + 360) % 360;
            if (rotation == 0) continue;
            var media = page.MediaBox;
            var crop = page.CropBox;
            if (crop.Width <= 0 || crop.Height <= 0) crop = media;
            var width = media.Width;
            var height = media.Height;
            double[] matrix = rotation switch
            {
                90 => [0, -1, 1, 0, -media.Y1, width + media.X1],
                180 => [-1, 0, 0, -1, width + media.X1, height + media.Y1],
                270 => [0, 1, -1, 0, height + media.Y1, -media.X1],
                _ => throw new InvalidDataException("PDF page rotation must be a quarter turn.")
            };
            (double X, double Y) Transform(double x, double y) =>
                (matrix[0] * x + matrix[2] * y + matrix[4], matrix[1] * x + matrix[3] * y + matrix[5]);
            var first = Transform(crop.X1, crop.Y1);
            var last = Transform(crop.X2, crop.Y2);
            var command = "q " + string.Join(" ", matrix.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + " cm\n";
            page.Contents.PrependContent().CreateStream(System.Text.Encoding.ASCII.GetBytes(command));
            page.Contents.AppendContent().CreateStream("\nQ\n"u8.ToArray());
            page.Rotate = 0;
            page.Orientation = PdfSharpCore.PageOrientation.Portrait;
            page.MediaBox = new PdfSharpCore.Pdf.PdfRectangle(new PdfSharpCore.Drawing.XRect(0, 0, rotation is 90 or 270 ? height : width, rotation is 90 or 270 ? width : height));
            page.CropBox = new PdfSharpCore.Pdf.PdfRectangle(new PdfSharpCore.Drawing.XRect(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Abs(first.X - last.X), Math.Abs(first.Y - last.Y)));
        }
        document.Save(path);
    }

    private static bool IsPdf(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[7];
        var count = stream.Read(header);
        return count >= 4 && header[..4].SequenceEqual("%PDF"u8)
            || count == 7 && header.SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf, 0x25, 0x50, 0x44, 0x46 });
    }

    private static async Task CopySource(string source, string target, CancellationToken token)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, token);
        output.Flush(flushToDisk: true);
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.Flush(flushToDisk: true);
    }

    private sealed class RejectPdfPasswords : IPdfPasswordProvider
    {
        public bool ProvidePassword(string fileName, int attempt, out string password)
        {
            password = "";
            throw new InvalidDataException("This PDF requires a password. Save an unlocked copy and import it again.");
        }
    }

    public void Dispose()
    {
        context.Dispose();
        if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true);
    }
}
