using System.Text;
using NAPS2.Scan;
using NUnit.Framework;

namespace Scan.Tests;

internal static class Naps2TestData
{
    // Representative output of NAPS2's ScanProfile/ScanResolution serializers, using fictional devices.
    public const string FourProfiles = """
        <?xml version="1.0" encoding="utf-8"?>
        <ArrayOfScanProfile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <ScanProfile>
            <Version>2</Version><DisplayName>Letters</DisplayName><DriverName>escl</DriverName>
            <Device><ID>urn:uuid:scanner-1</ID><Name>Office scanner</Name></Device>
            <PaperSource>Feeder</PaperSource><BitDepth>C24Bit</BitDepth><Resolution>Dpi600</Resolution><PageSize>Letter</PageSize>
            <AutoDeskew>true</AutoDeskew><PageAlign>Right</PageAlign><IsDefault>true</IsDefault><IconID>1</IconID>
          </ScanProfile>
          <ScanProfile>
            <Version>2</Version><DisplayName>Receipts</DisplayName><DriverName>escl</DriverName>
            <Device><ID>urn:uuid:scanner-1</ID><Name>Office scanner</Name></Device>
            <PaperSource>Glass</PaperSource><BitDepth>Grayscale</BitDepth><Resolution>Dpi300</Resolution><PageSize>A4</PageSize>
          </ScanProfile>
          <ScanProfile>
            <Version>2</Version><DisplayName>Duplex archive</DisplayName><DriverName>escl</DriverName>
            <Device><ID>urn:uuid:scanner-1</ID><Name>Office scanner</Name></Device>
            <PaperSource>Duplex</PaperSource><BitDepth>BlackWhite</BitDepth><Resolution>Dpi200</Resolution><PageSize>A4</PageSize>
            <ExcludeBlankPages>true</ExcludeBlankPages><EnableAutoSave>true</EnableAutoSave>
            <AutoSaveSettings><FilePath>/example/archive.pdf</FilePath></AutoSaveSettings>
          </ScanProfile>
          <ScanProfile>
            <Version>2</Version><DisplayName>Old USB scanner</DisplayName><DriverName>wia</DriverName>
            <Device><ID>usb-device-id</ID><Name>Office scanner</Name></Device>
            <PaperSource>Glass</PaperSource><BitDepth>C24Bit</BitDepth><Resolution>Dpi150</Resolution><PageSize>Legal</PageSize>
          </ScanProfile>
        </ArrayOfScanProfile>
        """;

    public static IReadOnlyList<Naps2Profile> Read(string xml = FourProfiles)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return Naps2Profiles.Read(stream);
    }
}

[TestFixture]
public sealed class Naps2ProfileTests : TestFiles
{
    private ScanPreferencesStore Store => new(Path.Combine(Root, "preferences.json"));

    [Test]
    public void CurrentFormatMapsFourProfilesAndReportsUnmappedOptions()
    {
        var profiles = Naps2TestData.Read();
        Assert.Multiple(() =>
        {
            Assert.That(profiles.Count, Is.EqualTo(4));
            Assert.That(profiles.All(p => p.CanImport), Is.True);
            Assert.That(profiles[0].Settings, Is.EqualTo(ScanTestData.Settings() with { RequireExactDevice = true }));
            Assert.That(profiles[1].Settings, Is.EqualTo(new RememberedScanSettings(ScannerIdentity.From(ScanTestData.Device()), "Flatbed", "Grayscale", 300, "A4", RequireExactDevice: true)));
            Assert.That(profiles[2].Settings!.Source, Is.EqualTo("Duplex"));
            Assert.That(profiles[2].Settings!.Color, Is.EqualTo("BlackAndWhite"));
            Assert.That(profiles[0].Origin.Notices, Has.Some.Contains("AutoDeskew"));
            Assert.That(profiles[2].Origin.Notices, Has.Some.Contains("AutoSaveSettings"));
            Assert.That(profiles[3].Settings!.Device.Driver, Is.EqualTo("Wia"));
            Assert.That(profiles[3].Settings!.Size, Is.EqualTo("Legal"));
            Assert.That(profiles[3].Origin.Notices, Has.Some.Contains("A4 and Letter"));
            Assert.That(profiles[3].Origin.Notices, Has.Some.Contains("AirScan"));
        });
    }

    [TestCase("ArrayOfExtendedScanSettings", "ExtendedScanSettings", "")]
    [TestCase("ArrayOfScanSettings", "ScanSettings", "xsi:type=\"ExtendedScanSettings\"")]
    public void LegacyFormatsUseDeviceDriverAndNaps2Defaults(string root, string element, string attribute)
    {
        var xml = $"""
            <{root} xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <{element} {attribute}>
                <Version>1</Version><DisplayName>Legacy</DisplayName>
                <Device><ID>old-device</ID><Name>USB</Name><DriverName>twain</DriverName></Device>
              </{element}>
            </{root}>
            """;
        var profile = Naps2TestData.Read(xml).Single();
        Assert.Multiple(() =>
        {
            Assert.That(profile.CanImport, Is.True, profile.Error);
            Assert.That(profile.Settings, Is.EqualTo(new RememberedScanSettings(new("Twain", "old-device", "USB"), "Flatbed", "Color", 200, "Letter", RequireExactDevice: true)));
            Assert.That(profile.Origin.Notices, Has.Some.Contains("native scanner UI"));
            Assert.That(profile.Origin.Notices.Count(n => n.Contains("omitted")), Is.EqualTo(4));
        });
    }

    [Test]
    public void VeryOldWiaProfileExplainsImplicitNativeUi()
    {
        var profile = Naps2TestData.Read("""
            <ArrayOfScanSettings><ScanSettings><DisplayName>Native</DisplayName>
            <Device><ID>old</ID><Name>USB</Name><DriverName>wia</DriverName></Device>
            </ScanSettings></ArrayOfScanSettings>
            """).Single();
        Assert.That(profile.CanImport, Is.True);
        Assert.That(profile.Origin.Notices, Has.Some.Contains("native scanner UI"));
    }

    [TestCase("sane", "Sane")]
    [TestCase("apple", "Apple")]
    [TestCase("TWAIN", "Twain")]
    public void DriverIdentityIsPreservedWithoutPlatformSubstitution(string input, string expected)
    {
        var profile = Naps2TestData.Read(Naps2TestData.FourProfiles.Replace("<DriverName>escl</DriverName>", $"<DriverName>{input}</DriverName>"))[0];
        Assert.That(profile.Settings!.Device.Driver, Is.EqualTo(expected));
        Assert.That(profile.Settings.Device.Id, Is.EqualTo("urn:uuid:scanner-1"));
    }

    [Test]
    public void CustomGeometryAndUnknownOptionsAreRetainedAsInertXml()
    {
        var profiles = Naps2TestData.Read(Naps2TestData.FourProfiles.Replace("<PageSize>Letter</PageSize>", """
            <PageSize>Custom</PageSize><CustomPageSizeName>Card</CustomPageSizeName>
            <CustomPageSize><Width>105</Width><Height>148</Height><Unit>Millimetre</Unit></CustomPageSize>
            <FutureSetting mode="special">value</FutureSetting>
            """));
        var profile = profiles[0];
        Assert.Multiple(() =>
        {
            Assert.That(profile.Settings!.Size, Is.EqualTo("Custom"));
            Assert.That(profile.Origin.UnmappedOptions.Single(o => o.Name == "CustomPageSize").Xml, Does.Contain("<Width>105</Width>").And.Contain("Millimetre"));
            Assert.That(profile.Origin.UnmappedOptions.Single(o => o.Name == "FutureSetting").Xml, Does.Contain("mode=\"special\""));
            Assert.That(profile.Origin.Notices, Has.Some.Contains("FutureSetting"));
        });
    }

    [TestCase("<Version>2</Version>", "<Version>3</Version>", "version")]
    [TestCase("<Resolution>Dpi600</Resolution>", "<Resolution>Dpi0</Resolution>", "resolution")]
    [TestCase("<Resolution>Dpi600</Resolution>", "<Resolution>600</Resolution>", "resolution")]
    [TestCase("<Resolution>Dpi600</Resolution>", "<Resolution><Dpi>600</Dpi></Resolution>", "single value")]
    [TestCase("<PaperSource>Feeder</PaperSource>", "<PaperSource>Unknown</PaperSource>", "paper source")]
    [TestCase("<BitDepth>C24Bit</BitDepth>", "<BitDepth>Unknown</BitDepth>", "bit depth")]
    [TestCase("<PageSize>Letter</PageSize>", "<PageSize>Unknown</PageSize>", "page size")]
    [TestCase("<DriverName>escl</DriverName>", "<DriverName>default</DriverName>", "driver")]
    [TestCase("<ID>urn:uuid:scanner-1</ID>", "<ID />", "device ID")]
    [TestCase("<DisplayName>Letters</DisplayName>", "<DisplayName> </DisplayName>", "names")]
    [TestCase("<DisplayName>Letters</DisplayName>", "<DisplayName><Name>Letters</Name></DisplayName>", "single values")]
    [TestCase("<Version>2</Version>", "<Version>2</Version><Version>1</Version>", "Duplicate")]
    public void BadProfilesAreUnselectableAndExplainTheProblem(string from, string to, string reason)
    {
        var profile = Naps2TestData.Read(Naps2TestData.FourProfiles.Replace(from, to))[0];
        Assert.That(profile.CanImport, Is.False);
        Assert.That(profile.Error, Does.Contain(reason));
    }

    [Test]
    public void OneMalformedProfileDoesNotHideUsableSiblings()
    {
        var profiles = Naps2TestData.Read(Naps2TestData.FourProfiles.Replace("<Resolution>Dpi600</Resolution>", "<Resolution>invalid</Resolution>"));
        Assert.That(profiles.Count(p => p.CanImport), Is.EqualTo(3));
    }

    [TestCase("<ArrayOfScanProfile>")]
    [TestCase("<WrongRoot />")]
    [TestCase("<ArrayOfScanProfile><Unexpected /></ArrayOfScanProfile>")]
    [TestCase("<ArrayOfScanProfile xmlns=\"urn:unrecognized\" />")]
    [TestCase("<!DOCTYPE x [<!ENTITY a 'injected'>]><ArrayOfScanProfile>&a;</ArrayOfScanProfile>")]
    [TestCase("<!DOCTYPE x SYSTEM 'file:///etc/passwd'><ArrayOfScanProfile />")]
    [TestCase("<!DOCTYPE x [<!ENTITY a SYSTEM 'https://127.0.0.1:1/private'>]><ArrayOfScanProfile>&a;</ArrayOfScanProfile>")]
    public void UnsafeMalformedAndForeignXmlIsRejected(string xml)
    {
        Assert.Throws<InvalidDataException>(() => Naps2TestData.Read(xml));
    }

    [Test]
    public void InputSizeDepthAndProfileCountAreBounded()
    {
        Assert.Throws<InvalidDataException>(() => Naps2TestData.Read("<ArrayOfScanProfile>" + new string(' ', Naps2Profiles.MaximumCharacters) + "</ArrayOfScanProfile>"));
        Assert.Throws<InvalidDataException>(() => Naps2TestData.Read(string.Concat(Enumerable.Repeat("<a>", 40)) + string.Concat(Enumerable.Repeat("</a>", 40))));
        Assert.Throws<InvalidDataException>(() => Naps2TestData.Read("<ArrayOfScanProfile>" + string.Concat(Enumerable.Repeat("<ScanProfile />", 257)) + "</ArrayOfScanProfile>"));
        var path = Path.Combine(Root, "large.xml");
        using (var stream = File.Create(path)) stream.SetLength(Naps2Profiles.MaximumCharacters + 1);
        Assert.Throws<InvalidDataException>(() => Naps2Profiles.ReadFile(path));
        Assert.That(Naps2TestData.Read("<ArrayOfScanProfile />"), Is.Empty);
    }

    [Test]
    public void StandardLocationDiscoveryIsDeterministicAndHonorsXdgAndPortablePaths()
    {
        var home = Path.Combine(Root, "home");
        var config = Path.Combine(Root, "config");
        var executable = Path.Combine(Root, "portable", "App");
        var paths = Naps2Profiles.StandardLocations(home, config, config, executable);
        Assert.That(paths, Is.EquivalentTo(new[]
        {
            Path.Combine(config, "naps2", "profiles.xml"), Path.Combine(config, "NAPS2", "profiles.xml"),
            Path.Combine(home, ".config", "naps2", "profiles.xml"), Path.Combine(Root, "portable", "Data", "profiles.xml")
        }));
        Assert.That(Naps2Profiles.StandardLocations("", "", "relative", ""), Is.Empty);
    }

    [Test]
    public void ImportFourProfilesPersistsAtomicallyWithoutApplyingOrChangingSourcesAndSession()
    {
        Store.Save(new(1, ScanTestData.Settings(), []));
        using var model = new MainViewModel(Store);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SetSession(CreateSession());
        var manifestPath = Path.Combine(model.Session!.DirectoryPath, "session.json");
        var manifest = File.ReadAllBytes(manifestPath);
        var path = Path.Combine(Root, "profiles.xml");
        File.WriteAllText(path, Naps2TestData.FourProfiles);
        var before = File.ReadAllBytes(path);
        Assert.That(model.ImportProfiles(Naps2Profiles.ReadFile(path), ProfileDuplicatePolicy.Skip), Is.EqualTo(4));
        Assert.Multiple(() =>
        {
            Assert.That(Store.Load().Preferences.Presets.Count, Is.EqualTo(4));
            Assert.That(Store.Load().Preferences.LastScan, Is.EqualTo(ScanTestData.Settings()));
            Assert.That(model.SelectedPreset, Is.Null);
            Assert.That(model.Dpi, Is.EqualTo(600));
            Assert.That(model.SelectedSource!.Value, Is.EqualTo(PaperSource.Feeder));
            Assert.That(model.SettingsExplanation, Is.Empty);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(manifest));
            Assert.That(model.CanUndo, Is.False);
            Assert.That(Directory.GetFiles(Root, ".preferences-*.tmp"), Is.Empty);
        });
        using var restarted = new MainViewModel(Store);
        Assert.That(restarted.Presets[0].ImportedProfile!.Notices, Has.Some.Contains("AutoDeskew"));
        Assert.That(restarted.Presets[0].Settings.RequireExactDevice, Is.True);
    }

    [Test]
    public void DuplicatePoliciesHandleReimportsCaseCollisionsAndStableReplacementIds()
    {
        using var model = new MainViewModel(Store);
        var profile = Naps2TestData.Read()[0];
        model.ImportProfiles([profile], ProfileDuplicatePolicy.Skip);
        var original = model.Presets.Single();
        model.SelectedPreset = original;
        var changed = profile with { Name = "letters", Settings = profile.Settings! with { Dpi = 300 } };
        Assert.That(model.ImportProfiles([changed], ProfileDuplicatePolicy.Skip), Is.Zero);
        Assert.That(model.Presets.Single(), Is.SameAs(original));
        Assert.That(model.ImportProfiles([changed], ProfileDuplicatePolicy.KeepBoth), Is.EqualTo(1));
        Assert.That(model.Presets[1].Name, Is.EqualTo("letters (2)"));
        model.ImportProfiles([changed], ProfileDuplicatePolicy.KeepBoth);
        Assert.That(model.Presets[2].Name, Is.EqualTo("letters (3)"));
        model.ImportProfiles([changed], ProfileDuplicatePolicy.Replace);
        Assert.Multiple(() =>
        {
            Assert.That(model.Presets[0].Id, Is.EqualTo(original.Id));
            Assert.That(model.Presets[0].Settings.Dpi, Is.EqualTo(300));
            Assert.That(model.SelectedPreset, Is.SameAs(model.Presets[0]));
            Assert.That(Store.Load().Preferences.LastScan, Is.Null);
        });
        var plan = Naps2Profiles.Plan([profile, changed], [], ProfileDuplicatePolicy.Replace);
        Assert.That(plan[0].Preset!.Id, Is.EqualTo(plan[1].Preset!.Id));
        using var batch = new MainViewModel();
        batch.ImportProfiles([profile, changed], ProfileDuplicatePolicy.Replace);
        Assert.That(batch.Presets.Single().Settings.Dpi, Is.EqualTo(300));
        Assert.That(Naps2Profiles.Plan([profile, changed], [], ProfileDuplicatePolicy.Skip).Count(p => p.Preset is not null), Is.EqualTo(1));
        Assert.That(Naps2Profiles.Plan([profile, changed], [], ProfileDuplicatePolicy.KeepBoth)[1].Preset!.Name, Is.EqualTo("letters (2)"));
    }

    [Test]
    public void KeepBothSuffixRespectsNameLimitAndInvalidOrUnselectedProfilesDoNotWrite()
    {
        using var model = new MainViewModel(Store);
        var profile = Naps2TestData.Read()[0] with { Name = new string('A', 80) };
        model.ImportProfiles([profile, profile], ProfileDuplicatePolicy.KeepBoth);
        Assert.That(model.Presets.All(p => p.Name.Length <= 80), Is.True);
        var bytes = File.ReadAllBytes(Store.FilePath);
        Assert.That(model.ImportProfiles([profile with { Settings = null, Error = "bad profile" }], ProfileDuplicatePolicy.Skip), Is.Zero);
        Assert.That(model.ImportProfiles([], ProfileDuplicatePolicy.Skip), Is.Zero);
        Assert.That(File.ReadAllBytes(Store.FilePath), Is.EqualTo(bytes));
    }

    [Test]
    public void FailedBatchDoesNotAddAnyPresetsOrChangeSelectionAndCleansStagingFile()
    {
        using var model = new MainViewModel(Store);
        Directory.CreateDirectory(Store.FilePath);
        Assert.Throws<IOException>(() => model.ImportProfiles(Naps2TestData.Read(), ProfileDuplicatePolicy.Skip));
        Assert.That(model.Presets, Is.Empty);
        Assert.That(model.SelectedPreset, Is.Null);
        Assert.That(Directory.GetFiles(Root, ".preferences-*.tmp"), Is.Empty);
        model.Busy = true;
        Assert.Throws<InvalidOperationException>(() => model.ImportProfiles(Naps2TestData.Read(), ProfileDuplicatePolicy.Skip));
    }

    [Test]
    public void ApplyingImportedPresetUsesExactIdentityAndExistingCapabilityValidation()
    {
        using var model = new MainViewModel(Store);
        model.ImportProfiles(Naps2TestData.Read().Take(1), ProfileDuplicatePolicy.Skip);
        var matching = ScanTestData.Device(name: "New display name");
        var different = ScanTestData.Device("other-id");
        model.SetDevices([different, matching]);
        model.SetCapabilities(different, ScanTestData.Caps());
        model.SelectedPreset = model.Presets.Single();
        model.PreparePreset();
        Assert.That(model.SelectedDevice, Is.SameAs(matching));
        Assert.That(model.HasValidScanSettings, Is.False);
        model.SetCapabilities(matching, new ScanCaps
        {
            PaperSourceCaps = new() { SupportsFeeder = true },
            FeederCaps = ScanTestData.Source(false, 150, 300)
        });
        Assert.Multiple(() =>
        {
            Assert.That(model.Dpi, Is.EqualTo(300));
            Assert.That(model.SelectedColor!.Value, Is.EqualTo(NAPS2.Images.BitDepth.Grayscale));
            Assert.That(model.SettingsExplanation, Does.Contain("600 DPI").And.Contain("Color").And.Contain("AutoDeskew"));
            Assert.That(model.Presets[0].Settings.Dpi, Is.EqualTo(600));
        });
        model.RememberCurrentSettings();
        Assert.That(Store.Load().Preferences.LastScan!.RequireExactDevice, Is.True);
    }

    [Test]
    public void MissingImportedScannerNeverSelectsUnrelatedDeviceEvenAfterRefreshOrRestart()
    {
        Store.Save(new(1, ScanTestData.Settings(), []));
        using var model = new MainViewModel(Store);
        model.ImportProfiles(Naps2TestData.Read().Take(1), ProfileDuplicatePolicy.Skip);
        var impostors = new[] { ScanTestData.Device("different-id"), ScanTestData.Device(driver: Driver.Sane) };
        model.SetDevices(impostors);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        model.SelectedPreset = model.Presets.Single();
        model.PreparePreset();
        model.RememberCurrentSettings();
        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedDevice, Is.Null);
            Assert.That(model.CanScan, Is.False);
            Assert.That(model.SettingsExplanation, Does.Contain("unavailable").And.Contain("explicitly"));
            Assert.That(Store.Load().Preferences.LastScan, Is.EqualTo(ScanTestData.Settings()));
        });
        model.SetDevices(impostors);
        Assert.That(model.SelectedDevice, Is.Null);
        var matching = ScanTestData.Device();
        model.SetDevices([.. impostors, matching]);
        Assert.That(model.SelectedDevice, Is.SameAs(matching));
        model.SetCapabilities(matching, ScanTestData.Caps());
        model.RememberCurrentSettings();
        using var restarted = new MainViewModel(Store);
        restarted.SetDevices(impostors);
        Assert.That(restarted.SelectedDevice, Is.Null);
        restarted.SelectedDevice = impostors[0];
        restarted.SetCapabilities(impostors[0], ScanTestData.Caps());
        restarted.RememberCurrentSettings();
        Assert.That(Store.Load().Preferences.LastScan!.Device.Id, Is.EqualTo(impostors[0].ID));
        Assert.That(Store.Load().Preferences.LastScan!.RequireExactDevice, Is.False);
        Assert.That(restarted.Presets.Single().Settings.Device.Id, Is.EqualTo(matching.ID));
    }

    [Test]
    public void DifferentDriverAndUnavailableProfileCanBeAppliedWithNoDevicesWithoutGuessing()
    {
        using var model = new MainViewModel();
        model.ImportProfiles(Naps2TestData.Read().Skip(3), ProfileDuplicatePolicy.Skip);
        model.SelectedPreset = model.Presets.Single();
        Assert.That(model.CanApplyPreset, Is.True);
        model.PreparePreset();
        Assert.That(model.SelectedDevice, Is.Null);
        model.SetDevices([ScanTestData.Device("usb-device-id", driver: Driver.Escl)]);
        Assert.That(model.SelectedDevice, Is.Null);
        Assert.That(model.SettingsExplanation, Does.Contain("Wia").And.Contain("Legal"));
    }

    [Test]
    public void PreImportPreferencesLoadWithOptionalPropertiesAbsentAndAreNotRewritten()
    {
        const string json = """
            {"Version":1,"LastScan":{"Device":{"Driver":"Escl","Id":"old-id","Name":"Old"},"Source":"Feeder","Color":"Color","Dpi":300,"Size":"Letter"},
            "Presets":[{"Id":"saved","Name":"Existing","Settings":{"Device":{"Driver":"Escl","Id":"old-id","Name":"Old"},"Source":"Flatbed","Color":"Grayscale","Dpi":150,"Size":"A4"}}]}
            """;
        File.WriteAllText(Store.FilePath, json);
        using var model = new MainViewModel(Store);
        Assert.That(model.Presets.Single().ImportedProfile, Is.Null);
        Assert.That(model.Presets.Single().Settings.RequireExactDevice, Is.False);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        Assert.That(model.SelectedDevice, Is.Not.Null);
        Assert.That(File.ReadAllText(Store.FilePath), Is.EqualTo(json));
    }

    [Test]
    public void InvalidImportMetadataCannotReplaceValidPreferences()
    {
        Store.Save(new(1, ScanTestData.Settings(), []));
        var original = File.ReadAllBytes(Store.FilePath);
        var invalid = new ScanPreset("bad", "Bad", ScanTestData.Settings(), new("Bad", null!, []));
        Assert.Throws<InvalidDataException>(() => Store.Save(new(1, null, [invalid])));
        Assert.That(File.ReadAllBytes(Store.FilePath), Is.EqualTo(original));
    }
}
