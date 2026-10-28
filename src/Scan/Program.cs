using System.Text.Json;
using Avalonia;

namespace Scan;

public static class Program
{
    public static string? SessionArgument { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "open")
        {
            SessionArgument = args.Length > 1 ? args[1] : null;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        try { return RunCommand(args).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if (args.Contains("--json")) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message }));
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseWayland().UseSkia().UseHarfBuzz()
        .With(new WaylandPlatformOptions { AppId = "esyuscan" }).LogToTrace();

    private static async Task<int> RunCommand(string[] args)
    {
        if (args[0] is "--help" or "help")
        {
            Console.WriteLine("""
                esyuscan open [session-folder]
                esyuscan check [--json]
                esyuscan devices [--json]
                esyuscan export SESSION --format images|pdfs|pdf|tiff|documents --output PATH [--json]
                  --image-format png|jpeg|tiff (images only)
                  --quality 1..100 (JPEG; default 90) --tiff-compression auto|lzw|g4|none
                  --pdf-compression lossless|jpeg --no-jbig2 --pdfa none|1b|2b|3b|3u
                  --title TEXT --author TEXT --subject TEXT --keywords TEXT --creator TEXT
                  --pdf-owner-password-env NAME [--pdf-open-password-env NAME]
                  --deny print,full-quality-print,copy,accessibility,modify,annotate,forms,assemble
                  --rasterize-pdf (loses PDF source text/vectors)
                  --searchable --ocr-language eng (PDF only)
                PDF/A with PDF sources requires --rasterize-pdf; PDF/A forbids encryption.
                Encryption uses SDK 128-bit RC4, 1–32 printable ASCII password characters.
                Permissions depend on the reader. See docs/export-options.md.
                esyuscan ocr [--json]
                esyuscan skill install
                """);
            return 0;
        }
        if (args[0] == "ocr")
        {
            var available = await new OcrRuntime().Detect();
            WriteResult(available, args);
            return available.EngineAvailable && available.Languages.Length > 0 ? 0 : 1;
        }
        if (args[0] == "check")
        {
            using var engine = new Scanner();
            var discovery = await engine.Discover(CancellationToken.None);
            WriteResult(new { ok = true, engine = "NAPS2 SDK", scanners = discovery.Devices.Count, drivers = discovery.Drivers, theme = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/state/omarchy/current/theme/colors.toml")) }, args);
            return 0;
        }
        if (args[0] == "devices")
        {
            using var engine = new Scanner();
            var devices = await engine.Devices();
            WriteResult(devices.Select(device => new { id = device.ID, name = device.Name, driver = device.Driver.ToString() }).ToArray(), args);
            return 0;
        }
        if (args[0] == "export" && args.Length >= 2)
        {
            var export = ExportCommandOptions.Parse(args);
            var format = export.Format;
            var ocr = export.Ocr;
            var searchable = ocr is not null;
            var documentExport = export.Documents;
            using var cancellation = new CancellationTokenSource();
            void Cancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); }
            Console.CancelKeyPress += Cancel;
            try
            {
                using var session = Session.Open(args[1]);
                using var engine = new Scanner();
                var destination = export.Destination;
                void Progress(ExportProgress progress) => Console.Error.WriteLine($"{progress.Fraction:P0} {progress.Message}");
                if (documentExport) await engine.ExportBatchDocuments(session, destination, cancellation.Token, ocr, Progress, export.Options);
                else await engine.Export(session, destination, format, cancellation.Token, ocr, Progress, export.Options);
                var exportedPages = documentExport ? session.BatchDocuments().Sum(document => document.PageIds.Length) : session.Pages.Count;
                WriteResult(new { ok = true, pages = exportedPages, output = Path.GetFullPath(destination), searchablePdf = searchable, ocrLanguage = ocr?.Language }, args);
                return 0;
            }
            finally { Console.CancelKeyPress -= Cancel; }
        }
        if (args is ["skill", "install"])
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents/skills/esyuscan");
            Directory.CreateDirectory(folder);
            using var output = new StreamWriter(new FileStream(Path.Combine(folder, "SKILL.md"), FileMode.CreateNew));
            output.Write("---\nname: esyuscan\ndescription: Inspect scanners and export esyuscan scan sessions.\n---\nUse `esyuscan check --json` and `esyuscan devices --json` for read-only diagnostics. Export with `esyuscan export SESSION --format images|pdfs|pdf|tiff|documents --output PATH --json`. Separate images default to PNG; choose JPEG/TIFF with --image-format jpeg|tiff. --quality controls JPEG encoding; --tiff-compression auto|lzw|g4|none controls TIFF. See docs/export-options.md for PDF/A, metadata, compression and permissions. Password options accept environment variable names only; never put password values in command arguments. Add `--searchable --ocr-language eng` for searchable PDFs; `esyuscan ocr --json` lists installed languages. See docs/searchable-pdf.md for provisioning. The target must not exist. Sessions open in the GUI are locked; ask the user to close the session before CLI export. Never start physical scans or printing without the user's approval.\n");
            Console.WriteLine($"Installed skill in {folder}");
            return 0;
        }
        throw new ArgumentException("Unknown command. Use esyuscan --help.");
    }

    private static void WriteResult<T>(T value, string[] args) => Console.WriteLine(JsonSerializer.Serialize(value,
        new JsonSerializerOptions { WriteIndented = !args.Contains("--json") }));
}
