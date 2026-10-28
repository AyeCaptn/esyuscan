using NAPS2.Images;
using NAPS2.Scan;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class ScanGeometryPreferencesTests : TestFiles
{
    private ScanPreferencesStore Store => new(Path.Combine(Root, "preferences.json"));
    private MainViewModel Ready()
    {
        var model = new MainViewModel(Store);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        return model;
    }

    [Test]
    public void ManualDevicesSurviveEmptyDiscoveryAndRestartUsingDriverAndId()
    {
        var manual = ManualScannerAddress.Create(Driver.Sane, "genesys:libusb:001:004", "Same name");
        using (var model = Ready())
        {
            model.AddManualDevice(manual, ScanTestData.Caps());
            Assert.That(model.SelectedDevice, Is.EqualTo(manual));
            Assert.That(model.CanRemoveManualDevice, Is.True);
        }
        using var restarted = new MainViewModel(Store);
        restarted.SetDevices([ScanTestData.Device("another", "Same name")]);
        Assert.Multiple(() =>
        {
            Assert.That(restarted.SelectedDevice!.Driver, Is.EqualTo(Driver.Sane));
            Assert.That(restarted.SelectedDevice.ID, Is.EqualTo(manual.ID));
            Assert.That(restarted.HasValidScanSettings, Is.False, "Manual devices still need fresh capabilities");
        });
        restarted.SetCapabilities(manual, ScanTestData.Caps());
        restarted.SetDevices([]);
        Assert.That(restarted.Devices.Single().ID, Is.EqualTo(manual.ID));
        restarted.RemoveManualDevice();
        Assert.That(restarted.Devices, Is.Empty);
        Assert.That(Store.Load().Preferences.ManualDevices, Is.Empty);
    }

    [Test]
    public void ManualDeviceDeduplicatesByDriverIdAndFailedSaveLeavesListUntouched()
    {
        using var model = Ready();
        var manual = ManualScannerAddress.Create(Driver.Escl, "scanner.local", "One");
        model.AddManualDevice(manual, ScanTestData.Caps());
        model.AddManualDevice(manual with { Name = "Renamed" }, ScanTestData.Caps());
        model.SetDevices([manual, ScanTestData.Device(manual.ID, "One", Driver.Sane)]);
        Assert.That(model.Devices.Count, Is.EqualTo(2));
        Assert.That(Store.Load().Preferences.ManualDevices!.Single().Name, Is.EqualTo("Renamed"));
        var blocked = Path.Combine(Root, "blocked");
        File.WriteAllText(blocked, "keep");
        using var failed = new MainViewModel(new ScanPreferencesStore(Path.Combine(blocked, "prefs.json")));
        Assert.Throws<IOException>(() => failed.AddManualDevice(manual, ScanTestData.Caps()));
        Assert.That(failed.Devices, Is.Empty);
    }

    [Test]
    public void CustomDimensionsAreaAlignmentAndUnitsRoundTripThroughPresetsAndRestart()
    {
        using (var model = Ready())
        {
            model.SelectedSize = model.Sizes.Single(s => s.Label == "Custom");
            model.PaperWidth = "101.6"; model.PaperHeight = "152.4";
            model.SelectedAlignment = MainViewModel.AlignmentChoices.Single(c => c.Value == HorizontalAlign.Center);
            model.SelectScanArea = true;
            model.AreaX = "25.4"; model.AreaY = "12.7"; model.AreaWidth = "50.8"; model.AreaHeight = "76.2";
            model.GeometryUnit = "in";
            Assert.That(model.PaperWidth, Is.EqualTo("4"));
            Assert.That(model.AreaX, Is.EqualTo("1"));
            model.PresetName = "Small cards";
            model.SavePreset();
        }
        using var restored = Ready();
        var scan = restored.CurrentScanSettings();
        Assert.Multiple(() =>
        {
            Assert.That(scan.CustomSize, Is.True);
            Assert.That(scan.Size.WidthInMm, Is.EqualTo(101.6m));
            Assert.That(scan.Size.HeightInMm, Is.EqualTo(152.4m));
            Assert.That(scan.Alignment, Is.EqualTo(HorizontalAlign.Center));
            Assert.That(scan.Area, Is.EqualTo(new ScanRegion(25.4m, 12.7m, 50.8m, 76.2m)));
            Assert.That(restored.GeometryUnit, Is.EqualTo("in"));
            Assert.That(restored.HasGeometryError, Is.False);
        });
        restored.SelectScanArea = false;
        restored.SelectedPreset = restored.Presets.Single();
        restored.PreparePreset();
        restored.SetCapabilities(restored.SelectedDevice!, ScanTestData.Caps());
        Assert.That(restored.CurrentScanSettings().Area, Is.EqualTo(scan.Area));
    }

    [Test]
    public void InvalidGeometryDisablesJobsAndKeepsLastValidPreferences()
    {
        using var model = Ready();
        model.SelectedSize = model.Sizes.Single(s => s.Label == "Custom");
        model.PaperWidth = "100";
        var before = File.ReadAllText(Store.FilePath);
        foreach (var invalid in new[] { "0", "-1", "9999", "not a number", "79228162514264337593543950335" })
        {
            model.PaperWidth = invalid;
            Assert.That(model.HasValidScanSettings, Is.False, invalid);
            Assert.That(model.HasGeometryError, Is.True, invalid);
            Assert.Throws<InvalidOperationException>(() => model.CurrentScanSettings());
            Assert.That(File.ReadAllText(Store.FilePath), Is.EqualTo(before));
        }
        model.PaperWidth = "100";
        model.SelectScanArea = true;
        model.AreaWidth = "50";
        model.AreaX = "60";
        Assert.That(model.GeometryError, Does.Contain("inside"));
        model.AreaX = "10";
        Assert.That(model.HasValidScanSettings, Is.True);
        model.AreaY = "-1";
        Assert.That(model.HasValidScanSettings, Is.False);
        model.SelectScanArea = false;
        Assert.That(model.HasValidScanSettings, Is.True, "Turning off the area uses the whole valid paper");
    }

    [Test]
    public void UnitSwitchKeepsPhysicalBoundsValidAndSupportsLocalDecimalSeparator()
    {
        using var model = Ready();
        model.SelectedSize = model.Sizes.Single(s => s.Label == "Custom");
        model.PaperWidth = "216";
        for (var i = 0; i < 5; i++) { model.GeometryUnit = "in"; model.GeometryUnit = "mm"; }
        Assert.That(model.CurrentScanSettings().Size.WidthInMm, Is.EqualTo(216m).Within(.000001m));
        using var culture = new CultureScope("nl-BE");
        model.PaperWidth = "101,6";
        Assert.That(model.CurrentScanSettings().Size.WidthInMm, Is.EqualTo(101.6m));
    }

    [Test]
    public void UnsupportedCustomPaperFallsBackExplicitlyAndAreaOutsideNewPaperBlocksScanning()
    {
        Store.Save(new(1, ScanTestData.Settings() with { Size = "Custom", Geometry = new(215, 340, HorizontalAlign.Center) }, []));
        using var model = Ready();
        model.SetCapabilities(model.SelectedDevice!, new()
        {
            PaperSourceCaps = new() { SupportsFeeder = true },
            FeederCaps = new() { BitDepthCaps = new() { SupportsColor = true }, DpiCaps = new() { Values = [300] }, PageSizeCaps = new() { ScanArea = PageSize.A4, SupportsCustomPageSize = false } }
        });
        Assert.That(model.SelectedSize!.Label, Is.EqualTo("A4"));
        Assert.That(model.SettingsExplanation, Does.Contain("Custom").And.Contain("unsupported"));
        Assert.That(model.HasValidScanSettings, Is.True);
        model.SelectScanArea = true;
        model.AreaWidth = "200"; model.AreaX = "10";
        model.SelectedSize = model.Sizes.Single(s => s.Label == "A4");
        model.AreaX = "11";
        Assert.That(model.HasValidScanSettings, Is.False);
    }

    [Test]
    public void UnknownOrIncompleteCapabilitiesNeverOfferCustomAndNoCustomFlagIsRespected()
    {
        using var model = Ready();
        model.SetCapabilities(model.SelectedDevice!, new()
        {
            PaperSourceCaps = new() { SupportsFlatbed = true },
            FlatbedCaps = new() { BitDepthCaps = new() { SupportsColor = true }, DpiCaps = new() { Values = [300] } }
        });
        Assert.That(model.Sizes.Any(s => s.Label == "Custom"), Is.False);
        Assert.That(model.HasValidScanSettings, Is.True, "Existing standard-size fallback remains available with a warning");
        model.SetCapabilities(model.SelectedDevice!, new()
        {
            PaperSourceCaps = new() { SupportsFlatbed = true },
            FlatbedCaps = new() { BitDepthCaps = new() { SupportsColor = true }, DpiCaps = new() { Values = [300] }, PageSizeCaps = new() { ScanArea = new(100, 100, PageSizeUnit.Millimetre), SupportsCustomPageSize = false } }
        });
        Assert.That(model.HasValidScanSettings, Is.False);
        Assert.That(model.SettingsExplanation, Does.Contain("Scanning is disabled"));
    }

    [Test]
    public void SmallerCustomBoundsFallBackWithAnExplanationEvenWhenSizeLabelStaysCustom()
    {
        Store.Save(new(1, ScanTestData.Settings() with { Size = "Custom", Geometry = new(215, 340) }, []));
        using var model = Ready();
        model.SetCapabilities(model.SelectedDevice!, new()
        {
            PaperSourceCaps = new() { SupportsFeeder = true },
            FeederCaps = new() { BitDepthCaps = new() { SupportsColor = true }, DpiCaps = new() { Values = [300] }, PageSizeCaps = new() { ScanArea = new(100, 100, PageSizeUnit.Millimetre) } }
        });
        Assert.That(model.SelectedSize!.Label, Is.EqualTo("Custom"));
        Assert.That(model.CurrentScanSettings().Size.WidthInMm, Is.EqualTo(100));
        Assert.That(model.SettingsExplanation, Does.Contain("dimensions exceed").And.Contain("Review"));
    }

    [Test]
    public void UnknownBoundsRejectAlignmentThatCannotBeMapped()
    {
        using var model = Ready();
        model.SetCapabilities(model.SelectedDevice!, new()
        {
            PaperSourceCaps = new() { SupportsFlatbed = true },
            FlatbedCaps = new() { BitDepthCaps = new() { SupportsColor = true }, DpiCaps = new() { Values = [300] } }
        });
        model.SelectedAlignment = MainViewModel.AlignmentChoices[1];
        Assert.That(model.HasValidScanSettings, Is.False);
        Assert.That(model.GeometryError, Does.Contain("reported source bounds"));
        model.SelectedAlignment = MainViewModel.AlignmentChoices[0];
        Assert.That(model.HasValidScanSettings, Is.True);
    }

    [TestCase("{\"Version\":1,\"Presets\":[],\"ManualDevices\":[null]}")]
    [TestCase("{\"Version\":1,\"Presets\":[],\"ManualDevices\":[{\"Driver\":\"Sane\",\"Name\":\"USB\",\"Id\":null}]}")]
    public void MalformedManualPreferencesFallBackWithoutCrashingOrOverwriting(string json)
    {
        File.WriteAllText(Store.FilePath, json);
        var loaded = Store.Load();
        Assert.That(loaded.Warning, Does.Contain("could not be read"));
        Assert.That(File.ReadAllText(Store.FilePath), Is.EqualTo(json));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new(name);
        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = original;
    }
}
