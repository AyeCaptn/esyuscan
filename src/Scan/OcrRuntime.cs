using System.Diagnostics;
using System.Text.RegularExpressions;
using NAPS2.Ocr;
using NAPS2.Scan;

namespace Scan;

public record PdfOcrOptions(string Language);
public record ExportProgress(double Fraction, string Message);
public record OcrAvailability(bool EngineAvailable, string[] Languages, string Message);

/// <summary>Uses an explicitly provisioned Tesseract executable/data; never downloads or installs components.</summary>
public sealed partial class OcrRuntime
{
    public const string SetupHelp = "Install Tesseract 4/5 and the required language data (Arch: tesseract and tesseract-data-eng for English), "
        + "or set ESYUSCAN_TESSERACT and ESYUSCAN_TESSDATA. See docs/searchable-pdf.md, then refresh OCR languages.";
    private readonly string executable;
    private readonly string? dataDirectory;

    public OcrRuntime(string? executable = null, string? dataDirectory = null)
    {
        this.executable = executable ?? Environment.GetEnvironmentVariable("ESYUSCAN_TESSERACT") ?? "tesseract";
        this.dataDirectory = dataDirectory ?? Environment.GetEnvironmentVariable("ESYUSCAN_TESSDATA");
    }

    public async Task<OcrAvailability> Detect(CancellationToken token = default)
    {
        try
        {
            var version = await RunProbe("--version", token);
            if (!VersionPattern().IsMatch(version))
                throw new InvalidOperationException("Tesseract 4 or 5 is required.");
            var output = await RunProbe("--list-langs", token);
            var languages = output.Split('\n').Select(line => line.Trim())
                .Where(IsTextLanguage).Distinct().Order().ToArray();
            return new(true, languages, languages.Length == 0 ? $"No OCR languages found. {SetupHelp}"
                : $"OCR ready · {languages.Length} language(s): {string.Join(", ", languages)}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return new(false, [], $"OCR engine unavailable: {error.Message} {SetupHelp}");
        }
    }

    internal async Task<IOcrEngine> RequireEngine(PdfOcrOptions options, CancellationToken token)
    {
        // The SDK passes language codes in its process arguments. Accept installed, single language identifiers only.
        if (!IsTextLanguage(options.Language))
            throw new ArgumentException("Choose one OCR language code, such as eng, fra, or nld.");
        var available = await Detect(token);
        if (!available.EngineAvailable) throw new InvalidOperationException(available.Message);
        if (!available.Languages.Contains(options.Language))
            throw new InvalidOperationException($"OCR language '{options.Language}' is unavailable. Available: {string.Join(", ", available.Languages)}. {SetupHelp}");
        return TesseractOcrEngine.Custom(executable, dataDirectory);
    }

    private async Task<string> RunProbe(string argument, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(argument);
        if (dataDirectory is not null) start.Environment["TESSDATA_PREFIX"] = dataDirectory;
        using var process = Process.Start(start) ?? throw new IOException("Could not start Tesseract.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Tesseract detection timed out after 10 seconds.");
        }
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0) throw new IOException($"Tesseract exited with code {process.ExitCode}: {errors.Trim()}");
        return output + "\n" + errors;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex LanguagePattern();
    private static bool IsTextLanguage(string code) => LanguagePattern().IsMatch(code) && code is not ("osd" or "equ");
    [GeneratedRegex(@"tesseract\s+[45]\.", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}

/// <summary>
/// SDK 1.6.0 treats a null OCR result as an image-only success. Record every result and refuse publication on failure.
/// Drain active engine calls before releasing images/temp storage: the SDK queue can return early on cancellation.
/// </summary>
internal sealed class RequiredOcrEngine(IOcrEngine engine, CancellationToken token) : IOcrEngine
{
    private readonly object gate = new();
    private readonly List<Task<OcrResult?>> pending = [];
    private bool closed;
    private int completed;
    private Exception? failure;

    public Task<OcrResult?> ProcessImage(ScanningContext context, string imagePath, OcrParams options, CancellationToken cancelToken)
    {
        lock (gate)
        {
            if (closed || token.IsCancellationRequested) return Task.FromResult<OcrResult?>(null);
            var task = Process(context, imagePath, options);
            pending.Add(task);
            return task;
        }
    }

    private async Task<OcrResult?> Process(ScanningContext context, string path, OcrParams options)
    {
        try
        {
            var result = await engine.ProcessImage(context, path, options, token);
            token.ThrowIfCancellationRequested();
            if (result is null) throw new InvalidOperationException("Tesseract returned no OCR result. Check the engine and trained language data; no export was published. " + OcrRuntime.SetupHelp);
            Interlocked.Increment(ref completed);
            return result; // An actual blank page may legitimately contain no words.
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref failure, error, null);
            return null;
        }
    }

    public void Verify(int expected)
    {
        token.ThrowIfCancellationRequested();
        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
        if (completed != expected) throw new InvalidOperationException("OCR did not complete for every page; no export was published.");
    }

    public async Task Drain()
    {
        Task[] tasks;
        lock (gate) { closed = true; tasks = pending.ToArray(); }
        await Task.WhenAll(tasks);
    }
}

// Match the pinned SDK's distinction between image OCR and already-searchable PDF passthrough.
// Read PDF text with upstream PDFium through the same isolated internal API used by PdfExporter.
internal static class SdkPdfText
{
    public static bool RequiresOcr(NAPS2.Images.ProcessedImage image)
    {
        var storageType = image.Storage.GetType();
        var path = storageType.GetProperty("FullPath")?.GetValue(image.Storage) as string;
        var hint = storageType.GetProperty("TypeHint")?.GetValue(image.Storage) as string;
        var isPdf = path is not null ? Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) : hint == ".pdf";
        if (!isPdf || image.TransformState != NAPS2.Images.TransformState.Empty) return true;
        var type = typeof(NAPS2.Images.ProcessedImage).Assembly.GetType("NAPS2.Pdf.PdfiumPdfReader")
            ?? throw new NotSupportedException("This NAPS2 SDK does not provide PDF text inspection.");
        var reader = Activator.CreateInstance(type, nonPublic: true)!;
        object? text;
        if (path is not null)
            text = type.GetMethod("ReadTextByPage", [typeof(string), typeof(string)])!.Invoke(reader, [path, null]);
        else
        {
            var stream = (MemoryStream)(storageType.GetProperty("Stream")?.GetValue(image.Storage)
                ?? throw new InvalidDataException("Unsupported PDF page storage."));
            text = type.GetMethod("ReadTextByPage", [typeof(byte[]), typeof(int), typeof(string)])!
                .Invoke(reader, [stream.GetBuffer(), (int)stream.Length, null]);
        }
        return !((IEnumerable<string>)text!).Any(value => !string.IsNullOrWhiteSpace(value));
    }
}
