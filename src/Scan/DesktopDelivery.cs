using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Scan;

public record CommandResult(int ExitCode, string Output, string Error);
public interface IDesktopCommand
{
    Task<CommandResult> Run(string command, IReadOnlyList<string> arguments, CancellationToken token);
}

public sealed class DesktopCommand : IDesktopCommand
{
    public async Task<CommandResult> Run(string command, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new IOException($"Couldn't start {command}. Install/configure its desktop integration and try again: {error.Message}", error);
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            if (!token.IsCancellationRequested) throw new IOException($"{command} did not respond within 30 seconds. Check the desktop application or CUPS queue before retrying.");
            throw;
        }
        return new(process.ExitCode, await output, await errors);
    }
}

public enum PrintSides { OneSided, LongEdge, ShortEdge }
public record PrintOptions(string Printer, int Copies = 1, PrintSides Sides = PrintSides.OneSided, string Media = "Default", bool FitToPage = true);

public sealed class CupsPrinting(IDesktopCommand command)
{
    private static readonly Regex QueueName = new(@"^[^\s/#\p{C}]+$", RegexOptions.CultureInvariant);
    private static readonly Regex JobId = new(@"^request id is ([^\s/#\p{C}]+-[0-9]+)(?:\s|$)", RegexOptions.CultureInvariant);

    public async Task<string[]> Printers(CancellationToken token)
    {
        var result = await command.Run("lpstat", ["-e"], token);
        RequireSuccess(result, "Couldn't list CUPS printers");
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => QueueName.IsMatch(name)).Distinct().Order().ToArray();
    }

    public async Task<string> Submit(string pdf, string title, PrintOptions options, CancellationToken token)
    {
        if (!QueueName.IsMatch(options.Printer) || options.Printer.StartsWith('-')) throw new ArgumentException("Choose a CUPS printer.");
        if (options.Copies is < 1 or > 999) throw new ArgumentException("Copies must be between 1 and 999.");
        if (!Enum.IsDefined(options.Sides) || options.Media is not ("Default" or "A4" or "Letter")) throw new ArgumentException("Invalid print options.");
        token.ThrowIfCancellationRequested();
        var args = new List<string> { "-H", "hold", "-d", options.Printer, "-n", options.Copies.ToString(CultureInfo.InvariantCulture), "-t", title,
            "-o", "collate=true", "-o", "sides=" + (options.Sides switch { PrintSides.LongEdge => "two-sided-long-edge", PrintSides.ShortEdge => "two-sided-short-edge", _ => "one-sided" }) };
        if (options.Media != "Default") args.AddRange(["-o", "media=" + options.Media]);
        args.AddRange(["-o", options.FitToPage ? "fit-to-page" : "fit-to-page=false", Path.GetFullPath(pdf)]);
        // Do not kill lp mid-submission: obtain its ID, then cancel that exact held job.
        var submitted = await command.Run("lp", args, CancellationToken.None);
        RequireSuccess(submitted, "CUPS submission failed; check the queue for a held job before retrying");
        var match = JobId.Match(submitted.Output.Trim());
        if (!match.Success) throw new IOException("CUPS did not return a recognizable job ID. A held job may exist; inspect the printer queue before retrying.");
        var id = match.Groups[1].Value;
        try
        {
            token.ThrowIfCancellationRequested();
            var released = await command.Run("lp", ["-i", id, "-H", "resume"], CancellationToken.None);
            RequireSuccess(released, $"Couldn't release held CUPS job {id}");
            token.ThrowIfCancellationRequested();
            return id;
        }
        catch (Exception error)
        {
            try { await Cancel(id); }
            catch (Exception cancelError) { throw new IOException($"Job {id}: {error.Message} Cancellation also failed: {cancelError.Message}. Inspect the CUPS queue.", error); }
            throw;
        }
    }

    public async Task Cancel(string id)
    {
        if (id.StartsWith('-') || !JobId.IsMatch("request id is " + id + " ") || id.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid CUPS job ID.");
        RequireSuccess(await command.Run("cancel", [id], CancellationToken.None), $"Couldn't cancel CUPS job {id}; it may already be complete");
    }

    internal static void RequireSuccess(CommandResult result, string message)
    {
        if (result.ExitCode != 0) throw new IOException($"{message}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim())} (exit {result.ExitCode}).");
    }
}

public sealed class DesktopMail(IDesktopCommand command)
{
    public async Task Compose(string pdf, string subject, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // No recipients, mail transport, or send command. The user's composer owns the draft.
        // Once launched, cancelling esyuscan cannot reliably retract an opened composer.
        var result = await command.Run("xdg-email", ["--utf8", "--subject", subject, "--attach", Path.GetFullPath(pdf)], CancellationToken.None);
        CupsPrinting.RequireSuccess(result, "Couldn't open the mail composer; check your default mail client and its attachment support");
    }
}

// A mail client may read its attachment long after xdg-email exits, including after esyuscan closes.
public sealed class OutgoingDocument : IDisposable
{
    private readonly FileStream lease;
    private bool retain;
    public string DirectoryPath { get; }
    public string PdfPath { get; }

    public OutgoingDocument(string name, string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "esyuscan", "outgoing");
        Directory.CreateDirectory(root);
        Prune(root, DateTime.UtcNow - TimeSpan.FromDays(7));
        DirectoryPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop delivery requires Linux.");
        Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        lease = new FileStream(Path.Combine(DirectoryPath, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_').Take(80).ToArray()).Trim();
        PdfPath = Path.Combine(DirectoryPath, (string.IsNullOrEmpty(safe) ? "document" : safe) + ".pdf");
    }

    public void RetainForMail()
    {
        File.SetLastWriteTimeUtc(Path.Combine(DirectoryPath, ".lease"), DateTime.UtcNow);
        retain = true;
    }

    public void Dispose()
    {
        lease.Dispose();
        if (!retain) Directory.Delete(DirectoryPath, recursive: true);
    }

    public static void Prune(string root, DateTime olderThan)
    {
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                var marker = Path.Combine(folder, ".lease");
                if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(marker) >= olderThan) continue;
                using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (File.GetLastWriteTimeUtc(marker) >= olderThan) continue;
                    foreach (var file in Directory.EnumerateFiles(folder).Where(file => file != marker)) File.Delete(file);
                }
                File.Delete(marker);
                Directory.Delete(folder);
            }
            catch (IOException) { /* Another esyuscan process may still own this file. */ }
            catch (UnauthorizedAccessException) { /* Retain rather than disrupting a new export. */ }
        }
    }
}

public delegate Task PdfPreparation(Session session, string path, CancellationToken token, PdfOcrOptions? ocr);

public sealed class DocumentDelivery(PdfPreparation prepare, CupsPrinting printing, DesktopMail mail, string? temporaryRoot = null)
{
    public async Task<string> Print(Session session, PrintOptions options, CancellationToken token)
    {
        Guard(session);
        using var outgoing = new OutgoingDocument(session.Name, temporaryRoot);
        await prepare(session, outgoing.PdfPath, token, null);
        return await printing.Submit(outgoing.PdfPath, session.Name, options, token);
    }

    public async Task<string> Email(Session session, PdfOcrOptions? ocr, CancellationToken token)
    {
        Guard(session);
        using var outgoing = new OutgoingDocument(session.Name, temporaryRoot);
        await prepare(session, outgoing.PdfPath, token, ocr);
        // Retain even on a launcher error: a client may already have opened before reporting failure.
        token.ThrowIfCancellationRequested();
        outgoing.RetainForMail();
        await mail.Compose(outgoing.PdfPath, session.Name, token);
        return outgoing.PdfPath;
    }

    private static void Guard(Session session)
    {
        if (session.ManualDuplex is not null) throw new InvalidOperationException("Finish manual duplex review or keep scan order first.");
        if (session.Pages.Count == 0) throw new InvalidOperationException("Scan or import some pages first.");
    }
}
