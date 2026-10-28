using System.Reflection;
using NAPS2.Images;
using NAPS2.Ocr;
using NAPS2.Scan;

namespace Scan;

internal static class SdkLosslessOcr
{
    // SDK 1.6 PdfExporter always JPEG-encodes its OCR input, even for lossless PNG pages.
    // Populate the very same upstream queue/cache with the canonical PNG first. PdfExporter
    // then reuses its result (same image, engine and OcrParams); verification/draining still
    // run through RequiredOcrEngine. Keep this binding isolated and covered by real OCR tests.
    private static readonly PropertyInfo Queue = typeof(ScanningContext).GetProperty("OcrRequestQueue", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new NotSupportedException("This NAPS2 SDK does not provide the lossless OCR queue.");
    private static readonly MethodInfo Enqueue = Queue.PropertyType.GetMethod("Enqueue")
        ?? throw new NotSupportedException("This NAPS2 SDK does not provide lossless OCR enqueueing.");
    private static readonly object Foreground = Enum.Parse(Enqueue.GetParameters()[5].ParameterType, "Foreground");

    public static async Task Recognize(ScanningContext context, IOcrEngine engine, ProcessedImage image,
        MemoryStream canonicalPng, OcrParams options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(context.TempFolderPath, $"esyuscan-ocr-{Guid.NewGuid():N}.png");
        var handedToQueue = false;
        try
        {
            canonicalPng.Position = 0;
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                await canonicalPng.CopyToAsync(output, token);
            token.ThrowIfCancellationRequested();
            var task = (Task<OcrResult?>)Enqueue.Invoke(Queue.GetValue(context), [context, engine, image, path, options, Foreground, token])!;
            handedToQueue = true;
            var result = await task;
            token.ThrowIfCancellationRequested();
            if (result is null) throw new InvalidOperationException("Tesseract returned no OCR result; no export was published. " + OcrRuntime.SetupHelp);
        }
        finally
        {
            // Once queued, the upstream queue owns this file and may still be draining cancellation.
            if (!handedToQueue && File.Exists(path)) File.Delete(path);
        }
    }
}
