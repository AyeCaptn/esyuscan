using NAPS2.Images;
using NAPS2.Scan;
using NUnit.Framework;

namespace Scan.Tests;

internal static class ScanTestData
{
    public static ScanDevice Device(string id = "urn:uuid:scanner-1", string name = "Office scanner", Driver driver = Driver.Escl) => new(driver, id, name);
    public static RememberedScanSettings Settings(ScanDevice? device = null) => new(
        ScannerIdentity.From(device ?? Device()), "Feeder", "Color", 600, "Letter");
    public static PerSourceCaps Source(bool color = true, params int[] dpi) => new()
    {
        BitDepthCaps = new() { SupportsGrayscale = true, SupportsColor = color },
        DpiCaps = new() { Values = [.. dpi.Length == 0 ? new[] { 150, 300, 600 } : dpi] },
        PageSizeCaps = new() { ScanArea = new PageSize(216, 356, PageSizeUnit.Millimetre) }
    };
    public static ScanCaps Caps() => new()
    {
        PaperSourceCaps = new() { SupportsFlatbed = true, SupportsFeeder = true, SupportsDuplex = true },
        FlatbedCaps = Source(),
        FeederCaps = Source(),
        DuplexCaps = Source()
    };
}

[TestFixture]
public sealed class ScanPreferencesTests : TestFiles
{
    private ScanPreferencesStore Store => new(Path.Combine(Root, "preferences.json"));
    private void SaveLast(RememberedScanSettings settings) => Store.Save(new(1, settings, []));

    private MainViewModel ReadyModel()
    {
        var model = new MainViewModel(Store);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        return model;
    }

    [Test]
    public void MissingPreferencesUseDefaultsWithoutWritingAFile()
    {
        using var model = ReadyModel();
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedSource!.Value, Is.EqualTo(PaperSource.Flatbed));
            Assert.That(model.Dpi, Is.EqualTo(300));
            Assert.That(model.SelectedSize!.Label, Is.EqualTo("A4"));
            Assert.That(model.HasValidScanSettings, Is.True);
            Assert.That(File.Exists(Store.FilePath), Is.False);
        });
    }

    [Test]
    public void AllSettingsSurviveRestartAndApplyAfterSourceCapabilities()
    {
        using (var model = ReadyModel())
        {
            model.SelectedSource = model.Sources.Single(s => s.Value == PaperSource.Feeder);
            model.SelectedColor = model.Colors.Single(c => c.Value == BitDepth.Color);
            model.Dpi = 600;
            model.SelectedSize = model.Sizes.Single(s => s.Label == "Letter");
        }
        using var restarted = new MainViewModel(Store);
        var device = ScanTestData.Device(name: "Renamed scanner");
        restarted.SetDevices([device]);
        Assert.Multiple(() =>
        {
            Assert.That(restarted.HasValidScanSettings, Is.False);
            Assert.That(restarted.CanSavePreset, Is.False);
            Assert.That(restarted.Colors, Is.Empty);
        });
        restarted.SetCapabilities(device, ScanTestData.Caps());
        Assert.Multiple(() =>
        {
            Assert.That(restarted.SelectedSource!.Value, Is.EqualTo(PaperSource.Feeder));
            Assert.That(restarted.SelectedColor!.Value, Is.EqualTo(BitDepth.Color));
            Assert.That(restarted.Dpi, Is.EqualTo(600));
            Assert.That(restarted.SelectedSize!.Label, Is.EqualTo("Letter"));
            Assert.That(restarted.HasSettingsExplanation, Is.False);
        });
    }

    [Test]
    public void SourceChangesAndCapabilityRefreshPreserveValidChoices()
    {
        SaveLast(ScanTestData.Settings());
        using var model = ReadyModel();
        model.SelectedSource = model.Sources.Single(s => s.Value == PaperSource.Duplex);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedSource!.Value, Is.EqualTo(PaperSource.Duplex));
            Assert.That(model.SelectedColor!.Value, Is.EqualTo(BitDepth.Color));
            Assert.That(model.Dpi, Is.EqualTo(600));
            Assert.That(model.SelectedSize!.Label, Is.EqualTo("Letter"));
            Assert.That(model.HasSettingsExplanation, Is.False);
        });
    }

    [Test]
    public void MatchingUsesDriverAndIdEvenWhenDisplayNamesCollide()
    {
        var target = ScanTestData.Device();
        SaveLast(ScanTestData.Settings(target));
        using var model = new MainViewModel(Store);
        model.SetDevices([ScanTestData.Device("another-id"), ScanTestData.Device(driver: Driver.Sane), target]);
        Assert.That(model.SelectedDevice, Is.SameAs(target));
    }

    [Test]
    public void MissingScannerExplainsFallbackAndDoesNotForgetSavedDeviceOnRefresh()
    {
        SaveLast(ScanTestData.Settings());
        using var model = new MainViewModel(Store);
        model.SetDevices([]);
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedDevice, Is.Null);
            Assert.That(model.SettingsExplanation, Does.Contain("unavailable"));
            Assert.That(model.CanScan, Is.False);
        });
        var other = ScanTestData.Device("other");
        model.SetDevices([other]);
        model.SetCapabilities(other, ScanTestData.Caps());
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedDevice, Is.SameAs(other));
            Assert.That(model.SettingsExplanation, Does.Contain("Using"));
            Assert.That(Store.Load().Preferences.LastScan!.Device.Id, Is.EqualTo(ScanTestData.Device().ID));
        });
        // Without user edits, rediscovery should still prefer the originally saved device.
        model.SetDevices([other, ScanTestData.Device()]);
        Assert.That(model.SelectedDevice!.ID, Is.EqualTo(ScanTestData.Device().ID));
    }

    [Test]
    public void UnsupportedSettingsFallBackWithSpecificExplanations()
    {
        SaveLast(ScanTestData.Settings() with { Source = "UnknownSource", Color = "UnknownColor", Dpi = -12, Size = "Legal" });
        using var model = ReadyModel();
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedSource!.Value, Is.EqualTo(PaperSource.Flatbed));
            Assert.That(model.SelectedColor!.Value, Is.EqualTo(BitDepth.Grayscale));
            Assert.That(model.Dpi, Is.EqualTo(300));
            Assert.That(model.SelectedSize!.Label, Is.EqualTo("A4"));
            Assert.That(model.SettingsExplanation, Does.Contain("UnknownSource").And.Contain("UnknownColor").And.Contain("-12 DPI").And.Contain("Legal"));
        });
    }

    [Test]
    public void ExplicitCorrectionClearsOutdatedFallbackExplanations()
    {
        SaveLast(ScanTestData.Settings() with { Color = "UnsupportedColor", Dpi = 123 });
        using var model = ReadyModel();
        Assert.That(model.SettingsExplanation, Does.Contain("UnsupportedColor").And.Contain("123 DPI"));
        model.SelectedColor = model.Colors.Single(c => c.Value == BitDepth.Color);
        Assert.Multiple(() =>
        {
            Assert.That(model.HasSettingsExplanation, Is.False);
            Assert.That(Store.Load().Preferences.LastScan!.Color, Is.EqualTo("Color"));
            Assert.That(Store.Load().Preferences.LastScan!.Dpi, Is.EqualTo(300));
        });
    }

    [Test]
    public void SourceSpecificLimitsAreValidatedBeforeSaving()
    {
        SaveLast(ScanTestData.Settings());
        using var model = ReadyModel();
        var caps = ScanTestData.Caps();
        caps = new ScanCaps
        {
            PaperSourceCaps = caps.PaperSourceCaps,
            FeederCaps = caps.FeederCaps,
            FlatbedCaps = new()
            {
                BitDepthCaps = new() { SupportsBlackAndWhite = true },
                DpiCaps = new() { Values = [0, -1, 200, 200] },
                PageSizeCaps = new() { ScanArea = PageSize.A4 }
            }
        };
        model.SetCapabilities(model.SelectedDevice!, caps);
        model.SelectedSource = model.Sources.Single(s => s.Value == PaperSource.Flatbed);
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedColor!.Value, Is.EqualTo(BitDepth.BlackAndWhite));
            Assert.That(model.Resolutions, Is.EqualTo(new[] { 200 }));
            Assert.That(model.Dpi, Is.EqualTo(200));
            Assert.That(model.Sizes.Select(s => s.Label), Is.EqualTo(new[] { "A4", "Custom" }));
            Assert.That(model.SettingsExplanation, Does.Contain("600 DPI").And.Contain("Letter"));
            Assert.That(Store.Load().Preferences.LastScan!.Source, Is.EqualTo("Flatbed"));
        });
    }

    [Test]
    public void MissingCapabilitiesDisableScanningAndStaleDeviceResponsesAreIgnored()
    {
        using var model = ReadyModel();
        model.SetSession(CreateSession());
        model.SelectedDevice = ScanTestData.Device("other");
        model.SetCapabilities(ScanTestData.Device(), ScanTestData.Caps());
        Assert.That(model.CanScan, Is.False);
        model.SetCapabilities(model.SelectedDevice!, new ScanCaps());
        Assert.Multiple(() =>
        {
            Assert.That(model.CanScan, Is.False);
            Assert.That(model.CanSavePreset, Is.False);
            Assert.That(model.SettingsExplanation, Does.Contain("Scanning is disabled"));
        });
    }

    [TestCase("{broken")]
    [TestCase("null")]
    [TestCase("{\"Version\":2,\"Presets\":[]}")]
    [TestCase("{\"Version\":1,\"Presets\":[null]}")]
    [TestCase("{\"Version\":1,\"LastScan\":{},\"Presets\":[]}")]
    public void CorruptOrUnsupportedFilesUseDefaultsAndRemainUntouched(string contents)
    {
        File.WriteAllText(Store.FilePath, contents);
        using var model = ReadyModel();
        Assert.Multiple(() =>
        {
            Assert.That(model.Presets, Is.Empty);
            Assert.That(model.HasValidScanSettings, Is.True);
            Assert.That(model.SettingsExplanation, Does.Contain("could not be read"));
            Assert.That(File.ReadAllText(Store.FilePath), Is.EqualTo(contents));
        });
        model.Dpi = 600;
        Assert.That(Store.Load().Warning, Is.Empty);
    }

    [Test]
    public void PresetCrudPersistsAndApplyingWaitsForCapabilities()
    {
        SaveLast(ScanTestData.Settings());
        using var model = ReadyModel();
        model.PresetName = "Receipts";
        model.SavePreset();
        var id = model.SelectedPreset!.Id;
        model.PresetName = "Archive";
        model.RenamePreset();
        model.Dpi = 150;
        model.UpdatePreset();
        model.Dpi = 300;
        model.PreparePreset();
        Assert.That(model.HasValidScanSettings, Is.False);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        Assert.That(model.Dpi, Is.EqualTo(150));
        using (var restarted = new MainViewModel(Store))
        {
            Assert.That(restarted.Presets.Single(), Is.EqualTo(new ScanPreset(id, "Archive", ScanTestData.Settings() with { Dpi = 150 })));
        }
        model.DeletePreset();
        Assert.Multiple(() =>
        {
            Assert.That(Store.Load().Preferences.Presets, Is.Empty);
            Assert.That(model.SelectedPreset, Is.Null);
        });
    }

    [Test]
    public void PresetForAnotherDeviceAppliesOnlyAfterThatDevicesSourceCapabilities()
    {
        var second = ScanTestData.Device("scanner-2");
        var preset = new ScanPreset("archive", "Archive", ScanTestData.Settings(second) with { Source = "Duplex" });
        Store.Save(new(1, ScanTestData.Settings(), [preset]));
        using var model = ReadyModel();
        model.SetDevices([ScanTestData.Device(), second]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SelectedPreset = model.Presets.Single();
        // The window's Run wrapper marks the operation busy before preparing a preset.
        model.Busy = true;
        model.PreparePreset();
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedDevice, Is.SameAs(second));
            Assert.That(model.SelectedColor, Is.Null);
            Assert.That(model.CanScan, Is.False);
        });
        model.SetCapabilities(second, new ScanCaps
        {
            PaperSourceCaps = new() { SupportsDuplex = true },
            DuplexCaps = ScanTestData.Source(true, 600)
        });
        model.RememberCurrentSettings();
        model.Busy = false;
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedSource!.Value, Is.EqualTo(PaperSource.Duplex));
            Assert.That(model.SelectedColor!.Value, Is.EqualTo(BitDepth.Color));
            Assert.That(model.Dpi, Is.EqualTo(600));
            Assert.That(model.SelectedSize!.Label, Is.EqualTo("Letter"));
            Assert.That(Store.Load().Preferences.LastScan!.Device.Id, Is.EqualTo(second.ID));
        });
    }

    [Test]
    public void ApplyingMissingDevicePresetDoesNotOverwriteItsSavedSettings()
    {
        var preset = new ScanPreset("archive", "Archive", ScanTestData.Settings(ScanTestData.Device("missing")));
        Store.Save(new(1, null, [preset]));
        using var model = ReadyModel();
        model.SelectedPreset = model.Presets.Single();
        model.PreparePreset();
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.RememberCurrentSettings();
        Assert.Multiple(() =>
        {
            Assert.That(model.HasValidScanSettings, Is.True);
            Assert.That(model.SettingsExplanation, Does.Contain("unavailable"));
            Assert.That(Store.Load().Preferences.Presets.Single().Settings.Device.Id, Is.EqualTo("missing"));
            Assert.That(Store.Load().Preferences.LastScan!.Device.Id, Is.EqualTo(ScanTestData.Device().ID));
        });
    }

    [Test]
    public void SmallScannersUseCustomPaperAndUnknownAreaWarns()
    {
        using var model = ReadyModel();
        model.SetSession(CreateSession());
        var source = new PerSourceCaps
        {
            BitDepthCaps = new() { SupportsColor = true },
            DpiCaps = new() { Values = [300] },
            PageSizeCaps = new() { ScanArea = new PageSize(100, 100, PageSizeUnit.Millimetre) }
        };
        model.SetCapabilities(model.SelectedDevice!, new ScanCaps
        {
            PaperSourceCaps = new() { SupportsFlatbed = true },
            FlatbedCaps = source
        });
        Assert.Multiple(() =>
        {
            Assert.That(model.Sizes.Select(s => s.Label), Is.EqualTo(new[] { "Custom" }));
            Assert.That(model.CanScan, Is.True);
            Assert.That(model.CurrentScanSettings().Size.WidthInMm, Is.EqualTo(100));
            Assert.That(model.CurrentScanSettings().Size.HeightInMm, Is.EqualTo(100));
            Assert.That(model.SettingsExplanation, Does.Contain("A4").And.Contain("Custom"));
        });
        model.SetCapabilities(model.SelectedDevice!, new ScanCaps
        {
            PaperSourceCaps = new() { SupportsFlatbed = true },
            FlatbedCaps = new() { BitDepthCaps = source.BitDepthCaps, DpiCaps = source.DpiCaps }
        });
        Assert.Multiple(() =>
        {
            Assert.That(model.CanScan, Is.True);
            Assert.That(model.SettingsExplanation, Does.Contain("did not report page-size limits"));
        });
    }

    [Test]
    public void PresetNamesMustBeNonemptyAndUniqueIgnoringCase()
    {
        using var model = ReadyModel();
        Assert.Throws<ArgumentException>(model.SavePreset);
        model.PresetName = "One";
        model.SavePreset();
        model.PresetName = " one ";
        Assert.Throws<ArgumentException>(model.SavePreset);
        Assert.That(model.Presets.Count, Is.EqualTo(1));
    }

    [Test]
    public void AtomicReplacementLeavesNoTemporaryFilesAndRejectsInvalidWrites()
    {
        SaveLast(ScanTestData.Settings());
        var original = File.ReadAllText(Store.FilePath);
        Assert.Throws<InvalidDataException>(() => Store.Save(new(2, null, [])));
        Assert.That(File.ReadAllText(Store.FilePath), Is.EqualTo(original));
        SaveLast(ScanTestData.Settings() with { Dpi = 150 });
        Assert.Multiple(() =>
        {
            Assert.That(Store.Load().Preferences.LastScan!.Dpi, Is.EqualTo(150));
            Assert.That(Directory.GetFiles(Root), Has.Length.EqualTo(1));
        });
        var targetDirectory = Path.Combine(Root, "blocked.json");
        Directory.CreateDirectory(targetDirectory);
        Assert.That(new ScanPreferencesStore(targetDirectory).Load().Warning, Does.Contain("could not be read"));
        Assert.Throws<IOException>(() => new ScanPreferencesStore(targetDirectory).Save(ScanPreferences.Empty));
        Assert.That(Directory.GetFiles(Root), Has.Length.EqualTo(1));
    }

    [Test]
    public void PersistenceFailuresKeepScanningUsableAndDoNotPretendPresetWasSaved()
    {
        var blocked = Path.Combine(Root, "not-a-directory");
        File.WriteAllText(blocked, "keep");
        using var model = new MainViewModel(new ScanPreferencesStore(Path.Combine(blocked, "preferences.json")));
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.Dpi = 600;
        model.PresetName = "Archive";
        Assert.Throws<IOException>(model.SavePreset);
        Assert.Multiple(() =>
        {
            Assert.That(model.HasValidScanSettings, Is.True);
            Assert.That(model.SettingsExplanation, Does.Contain("could not be saved"));
            Assert.That(model.Presets, Is.Empty);
            Assert.That(File.ReadAllText(blocked), Is.EqualTo("keep"));
        });
    }

    [Test]
    public void PreferencesNeverEnterSessionManifestOrHistory()
    {
        using var model = ReadyModel();
        model.SetSession(CreateSession());
        var manifest = File.ReadAllText(Path.Combine(model.Session!.DirectoryPath, "session.json"));
        model.Dpi = 600;
        model.PresetName = "Archive";
        model.SavePreset();
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Combine(model.Session.DirectoryPath, "session.json")), Is.EqualTo(manifest));
            Assert.That(model.CanUndo, Is.False);
            Assert.That(Directory.GetFiles(model.Session.DirectoryPath).Select(Path.GetFileName), Does.Not.Contain("preferences.json"));
        });
    }
}
