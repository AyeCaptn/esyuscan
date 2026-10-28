using NAPS2.Scan;

namespace Scan;

public sealed partial class MainViewModel
{
    private readonly ScannerCacheStore? scannerCache;
    private readonly List<ScanDevice> knownScanners = [];
    private Task cacheWrite = Task.CompletedTask;
    private bool findingScanners, checkingScanner, checkingOcr;
    private string scannerStatus = "";
    public bool FindingScanners { get => findingScanners; set { Set(ref findingScanners, value); Notify(nameof(CanRefreshScanners)); Notify(nameof(RefreshScannersLabel)); } }
    public bool CheckingScanner { get => checkingScanner; set { Set(ref checkingScanner, value); NotifyState(); } }
    public bool CheckingOcr { get => checkingOcr; set { Set(ref checkingOcr, value); Notify(nameof(CanRefreshOcr)); Notify(nameof(RefreshOcrLabel)); } }
    public bool CanRefreshScanners => !Busy && !FindingScanners;
    public bool CanRefreshOcr => !Busy && !CheckingOcr;
    public string RefreshScannersLabel => FindingScanners ? "Looking…" : "Refresh";
    public string RefreshOcrLabel => CheckingOcr ? "Checking OCR…" : "Refresh OCR";
    public string ScannerStatus { get => scannerStatus; set { Set(ref scannerStatus, value); Notify(nameof(HasScannerStatus)); } }
    public bool HasScannerStatus => ScannerStatus.Length > 0;

    private void RestoreKnownScanners()
    {
        var cached = scannerCache?.Load();
        if (cached is { } loaded)
        {
            knownScanners.AddRange(loaded.Devices);
            if (loaded.Warning.Length > 0) DiscoveryExplanation = loaded.Warning;
        }
        var previous = preferences.LastScan?.Device;
        var devices = knownScanners.AsEnumerable();
        if (previous is { Driver: "Escl" or "Sane" })
            devices = devices.Append(new ScanDevice(Enum.Parse<Driver>(previous.Driver), previous.Id, previous.Name));
        SetDevices(devices);
    }

    public Task RememberScanners(CancellationToken token = default)
    {
        if (scannerCache is null) return Task.CompletedTask;
        var snapshot = knownScanners.ToArray();
        // Queue snapshots in UI request order, including after a failed write. An older save must
        // not finish after a manual removal and resurrect a forgotten address.
        cacheWrite = cacheWrite.ContinueWith(_ =>
        {
            token.ThrowIfCancellationRequested();
            scannerCache.Save(snapshot);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return cacheWrite;
    }

    private string discoveryExplanation = "Refresh searches AirScan/eSCL on the network and SANE backends for USB/network devices.";
    private string manualAddress = "", manualName = "";
    private ScanChoice<Driver> manualDriver = ManualDriverChoices[0];
    public static IReadOnlyList<ScanChoice<Driver>> ManualDriverChoices { get; } =
        [new("AirScan / eSCL", Driver.Escl), new("SANE backend ID", Driver.Sane)];
    public string DiscoveryExplanation { get => discoveryExplanation; private set => Set(ref discoveryExplanation, value); }
    public void ReportDiscoveryIssue(string message) => DiscoveryExplanation += "\n" + message;
    public string ManualAddress { get => manualAddress; set => Set(ref manualAddress, value); }
    public string ManualName { get => manualName; set => Set(ref manualName, value); }
    public ScanChoice<Driver> ManualDriver { get => manualDriver; set => Set(ref manualDriver, value); }
    public bool CanRemoveManualDevice => !Busy && SelectedDevice is not null
        && (preferences.ManualDevices ?? []).Any(d => d.Matches(SelectedDevice));
    public void SetDiscovery(ScannerDiscovery result)
    {
        DiscoveryExplanation = string.Join("\n", result.Drivers.Select(d => d.ToString()))
            + "\nRemembered scanners remain listed even when not found. Selecting one checks its live capabilities.";
        foreach (var found in result.Devices)
        {
            var identity = ScannerIdentity.From(found);
            var index = knownScanners.FindIndex(identity.Matches);
            if (index < 0) knownScanners.Add(found); else knownScanners[index] = found;
            if (!Devices.Any(identity.Matches)) Devices.Add(found);
        }
        // Do not reset the active scanner, capabilities, or user-edited settings when discovery finishes.
        if (SelectedDevice is null) SetDevices(Devices.ToArray());
    }
    public void AddManualDevice(ScanDevice validated, ScanCaps caps)
    {
        var identity = ScannerIdentity.From(validated);
        // Validate/persist before changing the visible device list. Only successful probes reach here.
        var saved = (preferences.ManualDevices ?? []).Where(d => !d.Matches(validated)).Append(identity).ToList();
        CommitPreferences(preferences with { ManualDevices = saved });
        SetDevices(Devices.Where(d => !identity.Matches(d)).Prepend(validated).ToArray());
        SelectedDevice = Devices.First(d => identity.Matches(d));
        SetCapabilities(SelectedDevice, caps);
        RememberCurrentSettings();
        NotifyState();
    }
    public void RemoveManualDevice()
    {
        if (!CanRemoveManualDevice) return;
        var removed = SelectedDevice!;
        CommitPreferences(preferences with
        {
            ManualDevices = (preferences.ManualDevices ?? []).Where(d => !d.Matches(removed)).ToList(),
            LastScan = preferences.LastScan?.Device.Matches(removed) == true ? null : preferences.LastScan
        });
        knownScanners.RemoveAll(ScannerIdentity.From(removed).Matches);
        capabilitiesReady = false;
        desiredSettings = preferences.LastScan;
        SetDevices(Devices.Where(d => !ScannerIdentity.From(removed).Matches(d)).ToArray());
    }
}
