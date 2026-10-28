using Avalonia.Interactivity;
using NAPS2.Scan;

namespace Scan;

public sealed partial class MainWindow
{
    private readonly Func<IScannerDiscovery> scannerDiscoveryFactory;
    private readonly Func<CancellationToken, Task<OcrAvailability>> ocrProbe;
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? capabilityCheck;
    private ScanDevice? checkingDevice;
    private int capabilityRequest;
    private bool capabilityPending;
    private bool pendingRemember, rememberSelectionAfterCheck;
    private bool opened, closed;

    private async Task RefreshScannersAsync()
    {
        if (closed || model.FindingScanners) return;
        model.FindingScanners = true;
        var token = shutdown.Token;
        try
        {
            var result = await Task.Run(async () =>
            {
                using var finder = scannerDiscoveryFactory();
                return await finder.Discover(token);
            }, token);
            token.ThrowIfCancellationRequested();
            if (closed) return;
            changingDevice = true;
            try { model.SetDiscovery(result); }
            finally { changingDevice = false; }
            if (model.SelectedDevice is null) model.ScannerStatus = "No scanner found. Add an address in Settings.";
            else if (!model.HasValidScanSettings && !model.CheckingScanner
                && result.Devices.Any(ScannerIdentity.From(model.SelectedDevice).Matches))
                _ = CheckSelectedScannerAsync();
            if (result.Devices.Count > 0) await model.RememberScanners(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed) model.ReportDiscoveryIssue(error.Message);
            Console.Error.WriteLine(error);
        }
        finally { if (!closed) model.FindingScanners = false; }
    }

    private void CancelCapabilityCheck()
    {
        capabilityRequest++;
        if (capabilityCheck is { } previous) _ = CancelCheckAsync(previous);
        capabilityCheck = null;
        checkingDevice = null;
        rememberSelectionAfterCheck = false;
        if (!closed) model.CheckingScanner = false;
    }

    private static async Task CancelCheckAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync(); }
        catch (Exception error) { Console.Error.WriteLine(error); }
    }

    private void ForegroundBusyChanged()
    {
        if (closed) return;
        if (model.Busy && model.CheckingScanner)
        {
            capabilityPending = true;
            pendingRemember |= rememberSelectionAfterCheck;
            CancelCapabilityCheck();
        }
        else if (!model.Busy && capabilityPending)
        {
            var remember = pendingRemember;
            capabilityPending = pendingRemember = false;
            _ = CheckSelectedScannerAsync(remember);
        }
    }

    private async Task CheckSelectedScannerAsync(bool remember = false)
    {
        if (closed) return;
        if (model.Busy) { capabilityPending = true; pendingRemember |= remember; return; }
        var device = model.SelectedDevice;
        if (device is null) { CancelCapabilityCheck(); return; }
        if (model.CheckingScanner && checkingDevice is not null && ScannerIdentity.From(device).Matches(checkingDevice))
        {
            rememberSelectionAfterCheck |= remember;
            return;
        }
        CancelCapabilityCheck();
        rememberSelectionAfterCheck = remember;
        var request = capabilityRequest;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        capabilityCheck = cancellation;
        checkingDevice = device;
        var token = cancellation.Token;
        model.CheckingScanner = true;
        model.ScannerStatus = "Checking scanner… Other session actions remain available.";
        try
        {
            var caps = await Task.Run(async () =>
            {
                using var finder = scannerDiscoveryFactory();
                return await finder.Capabilities(device, token);
            }, token);
            token.ThrowIfCancellationRequested();
            if (closed || request != capabilityRequest || model.SelectedDevice is null
                || !ScannerIdentity.From(device).Matches(model.SelectedDevice)) return;
            if (model.Busy) { capabilityPending = true; pendingRemember |= rememberSelectionAfterCheck; return; }
            model.SetCapabilities(device, caps);
            if (rememberSelectionAfterCheck) model.RememberCurrentSettings();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed && request == capabilityRequest)
            {
                model.InvalidateCapabilities(device);
                model.ScannerStatus = $"Not verified: {error.Message} Refresh or select a scanner to retry.";
            }
            Console.Error.WriteLine(error);
        }
        finally
        {
            if (!closed && request == capabilityRequest)
            {
                capabilityCheck = null;
                checkingDevice = null;
                rememberSelectionAfterCheck = false;
                model.CheckingScanner = false;
            }
        }
    }

    private async Task RefreshOcrAsync()
    {
        if (closed || model.CheckingOcr) return;
        model.CheckingOcr = true;
        var token = shutdown.Token;
        try
        {
            var available = await Task.Run(() => ocrProbe(token), token);
            token.ThrowIfCancellationRequested();
            if (!closed) model.SetOcrAvailability(available);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed) model.SetOcrAvailability(new(false, [], $"OCR detection failed: {error.Message}"));
            Console.Error.WriteLine(error);
        }
        finally { if (!closed) model.CheckingOcr = false; }
    }

    private void RefreshDevices(object? sender, RoutedEventArgs args)
    {
        _ = CheckSelectedScannerAsync();
        _ = RefreshScannersAsync();
    }

    private async void RefreshOcr(object? sender, RoutedEventArgs args) => await RefreshOcrAsync();
}
