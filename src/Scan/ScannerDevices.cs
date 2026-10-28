using System.Runtime.CompilerServices;
using NAPS2.Images;
using NAPS2.Scan;

namespace Scan;

// Background enquiries own a separate context from document import/export and physical scanning.
public interface IScannerDiscovery : IDisposable
{
    Task<ScannerDiscovery> Discover(CancellationToken token);
    Task<ScanCaps> Capabilities(ScanDevice device, CancellationToken token);
}

public interface IScannerDevices
{
    IAsyncEnumerable<ScanDevice> Discover(Driver driver, CancellationToken token);
    Task<ScanCaps> Capabilities(ScanDevice device, CancellationToken token);
}

public interface IScannerBackend : IScannerDevices
{
    IAsyncEnumerable<ProcessedImage> Scan(ScanOptions options, Action<double> progress, CancellationToken token);
}

internal sealed class SdkScannerDevices(ScanController controller) : IScannerBackend
{
    public IAsyncEnumerable<ScanDevice> Discover(Driver driver, CancellationToken token) => controller.GetDevices(driver, token);
    public Task<ScanCaps> Capabilities(ScanDevice device, CancellationToken token) => controller.GetCaps(device, token);
    public async IAsyncEnumerable<ProcessedImage> Scan(ScanOptions options, Action<double> progress, [EnumeratorCancellation] CancellationToken token)
    {
        void OnProgress(object? sender, PageProgressEventArgs args) => progress(args.Progress);
        controller.PageProgress += OnProgress;
        try
        {
            await foreach (var image in controller.Scan(options, token)) yield return image;
        }
        finally { controller.PageProgress -= OnProgress; }
    }
}

public sealed record DriverDiscovery(Driver Driver, int Count, string? Error)
{
    public override string ToString() => $"{ScannerDevices.DriverLabel(Driver)}: " + (Error ?? $"{Count} found");
}
public sealed record ScannerDiscovery(IReadOnlyList<ScanDevice> Devices, IReadOnlyList<DriverDiscovery> Drivers);

// Discovery failures are isolated by driver. A missing SANE library must not hide AirScan devices.
public sealed class ScannerDevices(IScannerDevices backend, TimeSpan? capabilityTimeout = null)
{
    public static string DriverLabel(Driver driver) => driver == Driver.Sane ? "SANE / USB" : "AirScan / eSCL";

    public async Task<ScannerDiscovery> Discover(CancellationToken token)
    {
        var devices = new List<ScanDevice>();
        var reports = new List<DriverDiscovery>();
        foreach (var driver in new[] { Driver.Escl, Driver.Sane })
        {
            token.ThrowIfCancellationRequested();
            var found = new List<ScanDevice>();
            string? error = null;
            try
            {
                await foreach (var device in backend.Discover(driver, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (device.Driver == driver && !string.IsNullOrWhiteSpace(device.ID)
                        && !found.Any(d => ScannerIdentity.From(d).Matches(device))) found.Add(device);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = Failure(driver, ex); }
            token.ThrowIfCancellationRequested();
            devices.AddRange(found);
            reports.Add(new(driver, found.Count, error));
        }
        return new(devices, reports);
    }

    public async Task<ScanCaps> Capabilities(ScanDevice device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(capabilityTimeout ?? TimeSpan.FromSeconds(20));
        try
        {
            var caps = await backend.Capabilities(device, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (caps.PaperSourceCaps is not { } sources || !(sources.SupportsFlatbed || sources.SupportsFeeder || sources.SupportsDuplex))
                throw new InvalidDataException("No usable paper sources were reported. Check the address, backend and scanner power, then retry.");
            return caps;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) { throw new IOException("Scanner connection timed out. Check its address, power and network/USB connection, then retry.", ex); }
        catch (Exception ex) { throw new IOException(Failure(device.Driver, ex), ex); }
    }

    public async Task<(ScanDevice Device, ScanCaps Caps)> Probe(Driver driver, string address, string name, CancellationToken token)
    {
        var device = ManualScannerAddress.Create(driver, address, name);
        var caps = await Capabilities(device, token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name) && caps.MetadataCaps?.Model is { } model)
        {
            var label = string.Concat(model.Where(c => !char.IsControl(c))).Trim();
            if (label.Length > 0) device = device with { Name = label[..Math.Min(80, label.Length)] };
        }
        return (device, caps);
    }

    private static string Failure(Driver driver, Exception ex) => driver == Driver.Sane
        ? $"SANE: {ex.Message} Requires native libsane.so.1 and a backend for this scanner (matching ARM64/x64). Check backend/firmware support, USB permissions and connection; see docs/scanners.md. AirScan can be used independently."
        : $"AirScan/eSCL: {ex.Message} Check scanner power, network access, HTTP/HTTPS port and eSCL path; see docs/scanners.md.";
}

public static class ManualScannerAddress
{
    public static ScanDevice Create(Driver driver, string address, string name = "")
    {
        address ??= "";
        name ??= "";
        if (address.Any(char.IsControl)) throw new ArgumentException("Scanner addresses must not contain control characters.");
        address = address.Trim();
        if (address.Length is 0 or > 2048)
            throw new ArgumentException("Enter a scanner address without control characters (up to 2048 characters).");
        if (name.Trim().Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Scanner name must be at most 80 characters without control characters.");
        if (driver == Driver.Escl)
        {
            if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
                || uri.Port is < 1 or > 65535 || Uri.CheckHostName(uri.Host.Trim('[', ']')) == UriHostNameType.Unknown)
                throw new ArgumentException("Use an HTTP/HTTPS scanner host or URL, optionally with a port and eSCL path. Credentials, query strings and fragments are not supported.");
            var builder = new UriBuilder(uri);
            builder.Path = uri.AbsolutePath == "/" ? "/eSCL/" : uri.AbsolutePath.TrimEnd('/') + "/";
            address = builder.Uri.AbsoluteUri;
        }
        else if (driver == Driver.Sane)
        {
            var colon = address.IndexOf(':');
            if (colon <= 0 || colon == address.Length - 1
                || !address[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                throw new ArgumentException("Enter the exact SANE backend device ID, such as genesys:libusb:001:004 or net:host:backend:device. A USB path or bare IP is not a SANE ID.");
        }
        else throw new ArgumentException("Select AirScan/eSCL or SANE.");
        return new(driver, address, string.IsNullOrWhiteSpace(name) ? address[..Math.Min(80, address.Length)] : name.Trim());
    }
}
