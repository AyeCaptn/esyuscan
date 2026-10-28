using System.Collections.ObjectModel;
using NAPS2.Images;
using NAPS2.Scan;

namespace Scan;

public sealed partial class MainViewModel
{
    private static readonly ScanChoice<PageSize>[] KnownSizes = [new("A4", PageSize.A4), new("Letter", PageSize.Letter)];
    private readonly ScanPreferencesStore? preferencesStore;
    private ScanPreferences preferences;
    private RememberedScanSettings? desiredSettings;
    private ScanCaps? capabilities;
    private ScanDevice? device;
    private ScanChoice<PaperSource>? source;
    private ScanChoice<BitDepth>? color;
    private ScanChoice<PageSize>? size;
    private int dpi = 300;
    private bool applyingSettings;
    private bool capabilitiesReady;
    private bool usingFallbackDevice;
    private string preferenceWarning = "";
    private string deviceExplanation = "";
    private string fallbackExplanation = "";
    private string presetName = "";
    private ScanPreset? selectedPreset;
    private string appliedImportExplanation = "";

    // A null store provides an isolated in-memory model for previews and tests.
    public MainViewModel(ScanPreferencesStore? preferencesStore = null, ScannerCacheStore? scannerCache = null)
    {
        this.preferencesStore = preferencesStore;
        (preferences, preferenceWarning) = preferencesStore?.Load() ?? (ScanPreferences.Empty, "");
        desiredSettings = preferences.LastScan;
        foreach (var preset in preferences.Presets) Presets.Add(preset);
        this.scannerCache = scannerCache ?? (preferencesStore is null ? null
            : new ScannerCacheStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(preferencesStore.FilePath))!, "scanners.json")));
        RestoreKnownScanners();
    }

    public ObservableCollection<ScanChoice<PageSize>> Sizes { get; } = [];
    public ObservableCollection<ScanPreset> Presets { get; } = [];
    public bool HasValidScanSettings => capabilitiesReady && SelectedDevice is not null && Sources.Contains(SelectedSource!)
        && Colors.Contains(SelectedColor!) && Resolutions.Contains(Dpi) && Dpi > 0 && Sizes.Contains(SelectedSize!) && !HasGeometryError;
    public bool CanChangeScanSettings => !Busy && capabilitiesReady && !CheckingScanner;
    public bool CanSavePreset => !Busy && HasValidScanSettings;
    public bool CanUpdatePreset => CanSavePreset && SelectedPreset is not null;
    public bool CanManagePreset => !Busy && SelectedPreset is not null;
    public bool CanApplyPreset => CanManagePreset;
    public string SettingsExplanation => string.Join(" ", new[] { preferenceWarning, deviceExplanation, fallbackExplanation, appliedImportExplanation }.Where(s => s.Length > 0));
    public bool HasSettingsExplanation => SettingsExplanation.Length > 0;
    public string PresetName { get => presetName; set => Set(ref presetName, value); }
    public ScanPreset? SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (!Set(ref selectedPreset, value)) return;
            PresetName = value?.Name ?? "";
            NotifyState();
        }
    }

    public ScanDevice? SelectedDevice
    {
        get => device;
        set
        {
            if (ReferenceEquals(device, value)) return;
            var previous = CaptureSettings() ?? desiredSettings;
            Set(ref device, value);
            capabilitiesReady = false;
            capabilities = null;
            ScannerStatus = value is null ? "" : "Remembered scanner · not checked yet.";
            NotifyGeometry();
            if (!applyingSettings)
            {
                desiredSettings = previous is not null && value is not null
                    ? previous with { Device = ScannerIdentity.From(value), RequireExactDevice = false } : previous;
                deviceExplanation = "";
                usingFallbackDevice = false;
                Explain();
            }
            NotifyState();
        }
    }

    public ScanChoice<PaperSource>? SelectedSource
    {
        get => source;
        set
        {
            if (Equals(source, value)) return;
            var previous = CaptureSettings() ?? desiredSettings;
            Set(ref source, value);
            if (!applyingSettings && capabilitiesReady)
            {
                ApplySourceCapabilities(previous is null ? null : previous with { Source = value?.Value.ToString() ?? "" });
                RememberSettings();
            }
            NotifyState();
        }
    }
    public ScanChoice<BitDepth>? SelectedColor
    {
        get => color;
        set { if (Set(ref color, value)) SettingsEdited(); }
    }
    public int Dpi
    {
        get => dpi;
        set { if (Set(ref dpi, value)) SettingsEdited(); }
    }
    public ScanChoice<PageSize>? SelectedSize
    {
        get => size;
        set { if (Set(ref size, value)) { NotifyGeometry(); SettingsEdited(); } }
    }

    public void SetDevices(IEnumerable<ScanDevice> available)
    {
        desiredSettings = (usingFallbackDevice ? null : CaptureSettings()) ?? desiredSettings;
        var discovered = available.ToArray();
        applyingSettings = true;
        try
        {
            SelectedDevice = null;
            Devices.Clear();
            foreach (var item in discovered.Concat((preferences.ManualDevices ?? []).Select(d => new ScanDevice(Enum.Parse<Driver>(d.Driver), d.Id, d.Name))))
                if (!Devices.Any(d => ScannerIdentity.From(d).Matches(item))) Devices.Add(item);
            SelectedDevice = Devices.FirstOrDefault(d => desiredSettings?.Device.Matches(d) == true)
                ?? (desiredSettings?.RequireExactDevice == true ? null : Devices.FirstOrDefault());
            deviceExplanation = desiredSettings is not null && (SelectedDevice is null || !desiredSettings.Device.Matches(SelectedDevice))
                ? $"Saved scanner ‘{desiredSettings.Device.Name}’ ({desiredSettings.Device.Driver}: {desiredSettings.Device.Id}) is unavailable. "
                  + (SelectedDevice is null ? "Refresh when it is connected; saved settings are kept. Choose a scanner explicitly to use a different device." : $"Using ‘{SelectedDevice.Name}’ ({SelectedDevice.ID}); check settings before scanning.")
                : "";
            usingFallbackDevice = desiredSettings is not null && (SelectedDevice is null || !desiredSettings.Device.Matches(SelectedDevice));
            ClearCapabilityChoices();
        }
        finally { applyingSettings = false; }
        Explain();
        NotifyState();
    }

    // Device identity guards against late/cancelled responses from a previous selection.
    public void InvalidateCapabilities(ScanDevice forDevice)
    {
        if (SelectedDevice is null || !ScannerIdentity.From(forDevice).Matches(SelectedDevice)) return;
        capabilitiesReady = false;
        NotifyState();
    }

    public void SetCapabilities(ScanDevice forDevice, ScanCaps caps)
    {
        if (SelectedDevice is null || !ScannerIdentity.From(forDevice).Matches(SelectedDevice)) return;
        desiredSettings = (usingFallbackDevice ? null : CaptureSettings()) ?? desiredSettings;
        applyingSettings = true;
        try
        {
            capabilities = caps;
            capabilitiesReady = false;
            Sources.Clear();
            if (caps.PaperSourceCaps?.SupportsFlatbed == true) Sources.Add(new("Flatbed · one page at a time", PaperSource.Flatbed));
            if (caps.PaperSourceCaps?.SupportsFeeder == true) Sources.Add(new("Document feeder · a stack", PaperSource.Feeder));
            if (caps.PaperSourceCaps?.SupportsDuplex == true) Sources.Add(new("Duplex feeder · both sides", PaperSource.Duplex));
            SelectedSource = Sources.FirstOrDefault(s => s.Value.ToString() == desiredSettings?.Source) ?? Sources.FirstOrDefault();
            ApplySourceCapabilities(desiredSettings);
            capabilitiesReady = true;
            ScannerStatus = "";
            NotifyGeometry();
        }
        finally { applyingSettings = false; }
        NotifyState();
        // Capability discovery alone never rewrites a missing/corrupt preference file or a saved device's identity.
    }

    private void ClearCapabilityChoices()
    {
        fallbackExplanation = "";
        Sources.Clear();
        Colors.Clear();
        Resolutions.Clear();
        Sizes.Clear();
        SelectedSource = null;
        SelectedColor = null;
        Dpi = 0;
        SelectedSize = null;
    }

    private PerSourceCaps? CurrentSourceCapabilities => SelectedSource?.Value switch
    {
        PaperSource.Flatbed => capabilities?.FlatbedCaps,
        PaperSource.Feeder => capabilities?.FeederCaps,
        PaperSource.Duplex => capabilities?.DuplexCaps,
        _ => null
    };

    private void ApplySourceCapabilities(RememberedScanSettings? desired)
    {
        var wasApplying = applyingSettings;
        applyingSettings = true;
        try
        {
            var caps = CurrentSourceCapabilities;
            var explanations = new List<string>();
            if (desired is not null && SelectedSource?.Value.ToString() != desired.Source)
                explanations.Add($"Paper source ‘{desired.Source}’ is unsupported; using {SelectedSource?.Label ?? "no source"}.");
            Colors.Clear();
            if (caps?.BitDepthCaps?.SupportsGrayscale == true) Colors.Add(new("Grayscale", BitDepth.Grayscale));
            if (caps?.BitDepthCaps?.SupportsColor == true) Colors.Add(new("Color", BitDepth.Color));
            if (caps?.BitDepthCaps?.SupportsBlackAndWhite == true) Colors.Add(new("Black & white", BitDepth.BlackAndWhite));
            SelectedColor = Colors.FirstOrDefault(c => c.Value.ToString() == desired?.Color) ?? Colors.FirstOrDefault();
            if (desired is not null && SelectedColor?.Value.ToString() != desired.Color)
                explanations.Add($"Color ‘{desired.Color}’ is unsupported; using {SelectedColor?.Label ?? "no color mode"}.");
            Resolutions.Clear();
            foreach (var resolution in (caps?.DpiCaps?.Values ?? []).Where(d => d > 0).Distinct().Order()) Resolutions.Add(resolution);
            Dpi = desired is not null && Resolutions.Contains(desired.Dpi) ? desired.Dpi
                : Resolutions.Contains(300) ? 300 : Resolutions.FirstOrDefault();
            if (desired is not null && Dpi != desired.Dpi)
                explanations.Add($"{desired.Dpi} DPI is unsupported; using {Dpi} DPI.");
            Sizes.Clear();
            foreach (var choice in KnownSizes.Where(s => caps is not null && caps.PageSizeCaps?.Fits(s.Value) != false)) Sizes.Add(choice);
            var bounds = caps?.PageSizeCaps?.ScanArea;
            if (caps?.PageSizeCaps?.SupportsCustomPageSize == true && bounds is not null
                && bounds.WidthInMm is > 0 and <= 2000 && bounds.HeightInMm is > 0 and <= 2000)
                Sizes.Add(new("Custom", bounds));
            var customFits = caps?.PageSizeCaps?.SupportsCustomPageSize == true && desired?.Size == "Custom" && desired.Geometry is { IsValid: true } geometry
                && bounds is not null && geometry.WidthMm <= bounds.WidthInMm && geometry.HeightMm <= bounds.HeightInMm;
            SelectedSize = Sizes.FirstOrDefault(s => s.Label == desired?.Size && (s.Label != "Custom" || customFits)) ?? Sizes.FirstOrDefault();
            RestoreGeometry(desired?.Size == "Custom" && !customFits ? null : desired?.Geometry);
            if (desired?.Size == "Custom" && !customFits)
                explanations.Add($"Custom paper dimensions exceed this source's bounds or custom paper is unsupported; using {SelectedSize?.Label ?? "no available size"}. Review the dimensions before scanning.");
            else if (desired is not null && SelectedSize?.Label != desired.Size)
                explanations.Add($"Paper size ‘{desired.Size}’ is unsupported; using {SelectedSize?.Label ?? "no available size"}.");
            if (caps is not null && caps.PageSizeCaps?.ScanArea is null)
                explanations.Add("The scanner did not report page-size limits. Verify the paper size before scanning.");
            if (SelectedSource is null || SelectedColor is null || Dpi <= 0 || SelectedSize is null)
                explanations.Add("Scanner capabilities are incomplete or no supported paper size fits. Scanning is disabled; refresh or choose another source/scanner.");
            fallbackExplanation = string.Join(" ", explanations);
        }
        finally { applyingSettings = wasApplying; }
        Explain();
    }

    public void PreparePreset()
    {
        if (SelectedPreset is null) return;
        desiredSettings = SelectedPreset!.Settings with { RequireExactDevice = SelectedPreset.ImportedProfile is not null || SelectedPreset.Settings.RequireExactDevice };
        appliedImportExplanation = SelectedPreset.ImportedProfile is not { } imported ? "" :
            $"Imported from NAPS2 profile ‘{imported.Name}’. " + string.Join(" ", imported.Notices);
        // Clear active choices first so refresh cannot capture and replace the preset's intent.
        capabilitiesReady = false;
        SetDevices(Devices.ToArray());
    }

    public void SavePreset()
    {
        if (!CanSavePreset) return;
        var preset = new ScanPreset(Guid.NewGuid().ToString("N"), ValidatePresetName(), CaptureSettings()!);
        CommitPreferences(preferences with { LastScan = preset.Settings, Presets = [.. Presets, preset] });
        Presets.Add(preset);
        SelectedPreset = preset;
    }

    public void RenamePreset()
    {
        if (!CanManagePreset) return;
        ReplacePreset(SelectedPreset! with { Name = ValidatePresetName(SelectedPreset!.Id) });
    }

    public void UpdatePreset()
    {
        if (!CanManagePreset || !CanSavePreset) return;
        ReplacePreset(SelectedPreset! with { Settings = CaptureSettings()! });
    }

    public void DeletePreset()
    {
        if (!CanManagePreset) return;
        var removed = SelectedPreset!;
        CommitPreferences(preferences with { Presets = Presets.Where(p => p.Id != removed.Id).ToList() });
        Presets.Remove(removed);
        SelectedPreset = null;
    }

    // Save the full batch once before changing observable state. Import never changes LastScan or applies settings.
    public int ImportProfiles(IEnumerable<Naps2Profile> profiles, ProfileDuplicatePolicy policy)
    {
        if (Busy) throw new InvalidOperationException("Wait for the current operation before importing presets.");
        var plan = Naps2Profiles.Plan(profiles, Presets, policy);
        var next = Presets.ToList();
        var count = 0;
        foreach (var item in plan.Where(item => item.Preset is not null))
        {
            var preset = item.Preset!;
            var index = next.FindIndex(p => p.Id == preset.Id);
            if (index < 0) next.Add(preset); else next[index] = preset;
            count++;
        }
        if (count == 0) return 0;
        CommitPreferences(preferences with { Presets = next });
        var selectedId = SelectedPreset?.Id;
        Presets.Clear();
        foreach (var preset in next) Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == selectedId);
        return count;
    }

    private string ValidatePresetName(string? replacingId = null)
    {
        var name = PresetName.Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Give the preset a name of 1–80 characters.");
        if (Presets.Any(p => p.Id != replacingId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A preset with that name already exists. Choose another name.");
        return name;
    }

    private void ReplacePreset(ScanPreset replacement)
    {
        var index = Presets.IndexOf(SelectedPreset!);
        var next = Presets.ToList();
        next[index] = replacement;
        CommitPreferences(preferences with { Presets = next });
        Presets[index] = replacement;
        SelectedPreset = replacement;
    }

    private RememberedScanSettings? CaptureSettings() => !HasValidScanSettings ? null : new(
        ScannerIdentity.From(SelectedDevice!), SelectedSource!.Value.ToString(), SelectedColor!.Value.ToString(), Dpi, SelectedSize!.Label, CaptureGeometry(), desiredSettings?.RequireExactDevice == true);

    private void SettingsEdited()
    {
        if (!applyingSettings && HasValidScanSettings)
        {
            // An explicit edit accepts the current validated choices; old substitutions no longer describe them.
            fallbackExplanation = CurrentSourceCapabilities?.PageSizeCaps?.ScanArea is null
                ? "The scanner did not report page-size limits. Verify the paper size before scanning." : "";
            RememberSettings();
            Explain();
        }
        NotifyGeometry();
        NotifyState();
    }

    private void RememberSettings()
    {
        if (applyingSettings || CaptureSettings() is not { } current) return;
        desiredSettings = current;
        usingFallbackDevice = false;
        try { CommitPreferences(preferences with { LastScan = current }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            preferenceWarning = $"Scan settings could not be saved: {error.Message} Current settings can still be used; change a setting to retry.";
            Explain();
        }
    }

    // Called once after a user-selected scanner/preset loads, before starting any scan.
    public void RememberCurrentSettings() => RememberSettings();

    private void CommitPreferences(ScanPreferences next)
    {
        preferencesStore?.Save(next);
        preferences = next;
        preferenceWarning = "";
        Explain();
    }

    private void Explain()
    {
        Notify(nameof(SettingsExplanation));
        Notify(nameof(HasSettingsExplanation));
    }
}
