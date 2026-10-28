using System.Runtime.CompilerServices;
using System.Reflection;
using NAPS2.Images;
using NAPS2.Scan;
using NUnit.Framework;

namespace Scan.Tests;

internal sealed class FakeScannerBackend : IScannerBackend
{
    public Dictionary<Driver, ScanDevice[]> Devices { get; } = [];
    public Dictionary<Driver, Exception> Errors { get; } = [];
    public List<Driver> Queried { get; } = [];
    public Action<Driver>? OnDiscover { get; set; }
    public ScanCaps Caps { get; set; } = ScanTestData.Caps();
    public ScanDevice? Probed { get; private set; }
    public ScanOptions? Scanned { get; private set; }
    public Func<CancellationToken, Task<ScanCaps>>? OnCaps { get; set; }
    public Func<CancellationToken, IAsyncEnumerable<ProcessedImage>>? Images { get; set; }
    public async IAsyncEnumerable<ScanDevice> Discover(Driver driver, [EnumeratorCancellation] CancellationToken token)
    {
        Queried.Add(driver);
        OnDiscover?.Invoke(driver);
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        foreach (var device in Devices.GetValueOrDefault(driver, [])) yield return device;
        if (Errors.TryGetValue(driver, out var error)) throw error;
    }
    public Task<ScanCaps> Capabilities(ScanDevice device, CancellationToken token)
    {
        Probed = device;
        return OnCaps?.Invoke(token) ?? Task.FromResult(Caps);
    }
    public async IAsyncEnumerable<ProcessedImage> Scan(ScanOptions options, Action<double> progress, [EnumeratorCancellation] CancellationToken token)
    {
        Scanned = options;
        if (Images is not null)
            await foreach (var image in Images(token)) { progress(1); yield return image; }
    }
}

[TestFixture]
public sealed class ScannerCoverageTests : TestFiles
{
    [Test]
    public async Task DriversAggregateByIdentityAndKeepSameNameAndCrossDriverDevices()
    {
        var fake = new FakeScannerBackend();
        var first = ScanTestData.Device("same", "Same name");
        var second = ScanTestData.Device("second", "Same name");
        var sane = ScanTestData.Device("same", "Same name", Driver.Sane);
        fake.Devices[Driver.Escl] = [first, first with { Name = "Renamed duplicate" }, second];
        fake.Devices[Driver.Sane] = [sane];
        var found = await new ScannerDevices(fake).Discover(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fake.Queried, Is.EqualTo(new[] { Driver.Escl, Driver.Sane }));
            Assert.That(found.Devices, Is.EqualTo(new[] { first, second, sane }));
            Assert.That(found.Drivers.Select(d => d.Count), Is.EqualTo(new[] { 2, 1 }));
        });
    }

    [TestCase(Driver.Sane)]
    [TestCase(Driver.Escl)]
    public async Task DriverFailureRetainsOtherDriversAndReportsAction(Driver failing)
    {
        var fake = new FakeScannerBackend();
        fake.Devices[Driver.Escl] = [ScanTestData.Device()];
        fake.Devices[Driver.Sane] = [ScanTestData.Device("genesys:usb", driver: Driver.Sane)];
        fake.Errors[failing] = new DllNotFoundException("fixture missing library");
        var found = await new ScannerDevices(fake).Discover(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(found.Devices.Count, Is.EqualTo(2), "Incrementally discovered devices survive a later driver error");
            Assert.That(found.Drivers.Single(d => d.Driver == failing).Error, Does.Contain("fixture missing library").And.Contain("docs/scanners.md"));
            Assert.That(found.Drivers.Single(d => d.Driver != failing).Error, Is.Null);
        });
    }

    [Test]
    public void CancelledDiscoveryDoesNotStartAnotherDriver()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fake = new FakeScannerBackend();
        Assert.ThrowsAsync<OperationCanceledException>(() => new ScannerDevices(fake).Discover(cancellation.Token));
        Assert.That(fake.Queried, Is.Empty);
    }

    [Test]
    public void CancellationDuringDiscoveryCannotPublishLateDevicesOrStartSane()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeScannerBackend { OnDiscover = _ => cancellation.Cancel() };
        fake.Devices[Driver.Escl] = [ScanTestData.Device()];
        Assert.ThrowsAsync<OperationCanceledException>(() => new ScannerDevices(fake).Discover(cancellation.Token));
        Assert.That(fake.Queried, Is.EqualTo(new[] { Driver.Escl }));
    }

    [TestCase("scanner.local", "http://scanner.local/eSCL/")]
    [TestCase("192.0.2.4:8080", "http://192.0.2.4:8080/eSCL/")]
    [TestCase("https://SCANNER.local:8443/custom/eSCL", "https://scanner.local:8443/custom/eSCL/")]
    [TestCase("http://[::1]:8080/eSCL/", "http://[::1]:8080/eSCL/")]
    public void AirScanAddressesAreCanonicalAndRetainCustomEndpoint(string input, string expected)
    {
        var device = ManualScannerAddress.Create(Driver.Escl, input, " Desk ");
        Assert.That(device, Is.EqualTo(new ScanDevice(Driver.Escl, expected, "Desk")));
    }

    [TestCase(Driver.Escl, "")]
    [TestCase(Driver.Escl, "ftp://scanner/eSCL")]
    [TestCase(Driver.Escl, "http://user:password@scanner/eSCL")]
    [TestCase(Driver.Escl, "http://scanner/eSCL?secret=bad")]
    [TestCase(Driver.Escl, "http://scanner/#fragment")]
    [TestCase(Driver.Escl, "http://scanner:0")]
    [TestCase(Driver.Escl, "http://scanner:99999")]
    [TestCase(Driver.Escl, "http://bad host")]
    [TestCase(Driver.Sane, "192.0.2.4")]
    [TestCase(Driver.Sane, "/dev/usb/scanner")]
    [TestCase(Driver.Sane, "genesys:")]
    [TestCase(Driver.Sane, "genesys:device\n")]
    [TestCase(Driver.Wia, "device")]
    public void InvalidManualAddressesAreRejectedBeforeSdkContact(Driver driver, string address)
    {
        Assert.Throws<ArgumentException>(() => ManualScannerAddress.Create(driver, address));
    }

    [TestCase("genesys:libusb:001:004")]
    [TestCase("net:scanner.local:genesys:libusb:001:004")]
    [TestCase("airscan:e0:Office Scanner")]
    public void SaneBackendIdsRemainExact(string id) => Assert.That(ManualScannerAddress.Create(Driver.Sane, id).ID, Is.EqualTo(id));

    [Test]
    public async Task LongAddressAndMetadataDoNotPreventSavingAValidatedManualDevice()
    {
        var address = "http://scanner/" + new string('a', 200);
        var fake = new FakeScannerBackend
        {
            Caps = new() { MetadataCaps = new() { Model = new string('M', 120) }, PaperSourceCaps = new() { SupportsFlatbed = true } }
        };
        var result = await new ScannerDevices(fake).Probe(Driver.Escl, address, "", CancellationToken.None);
        var store = new ScanPreferencesStore(Path.Combine(Root, "preferences.json"));
        store.Save(new(1, null, [], [ScannerIdentity.From(result.Device)]));
        Assert.That(store.Load().Warning, Is.Empty);
        Assert.That(store.Load().Preferences.ManualDevices!.Single().Name.Length, Is.EqualTo(80));
        Assert.That(store.Load().Preferences.ManualDevices!.Single().Id, Is.EqualTo(address + "/"));
    }

    [Test]
    public async Task ManualProbeUsesExactIdentityAndDoesNotScan()
    {
        var fake = new FakeScannerBackend();
        var result = await new ScannerDevices(fake).Probe(Driver.Sane, "genesys:libusb:001:004", "USB desk", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fake.Probed, Is.EqualTo(result.Device));
            Assert.That(result.Caps, Is.SameAs(fake.Caps));
            Assert.That(fake.Scanned, Is.Null);
        });
    }

    [Test]
    public async Task ManualProbeHonorsCancellationEvenWhenSdkReturnsEmptyCaps()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeScannerBackend { OnCaps = token => { cancellation.Cancel(); return Task.FromResult(new ScanCaps()); } };
        Assert.ThrowsAsync<OperationCanceledException>(() => new ScannerDevices(fake).Probe(Driver.Escl, "scanner", "", cancellation.Token));
        await Task.CompletedTask;
    }

    [Test]
    public void ManualProbeTimeoutReportsRetryInsteadOfUserCancellation()
    {
        var fake = new FakeScannerBackend { OnCaps = async token => { await Task.Delay(Timeout.Infinite, token); return new(); } };
        var error = Assert.ThrowsAsync<IOException>(() => new ScannerDevices(fake, TimeSpan.FromMilliseconds(20)).Probe(Driver.Escl, "scanner", "", CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("timed out").And.Contain("retry"));
    }

    [Test]
    public void ManualProbeRejectsEmptyCapabilitiesWithActionableFailure()
    {
        var fake = new FakeScannerBackend { Caps = new() };
        var error = Assert.ThrowsAsync<IOException>(() => new ScannerDevices(fake).Probe(Driver.Escl, "scanner", "", CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("No usable paper sources").And.Contain("HTTP/HTTPS"));
    }

    [Test]
    public void RegionConvertsPhysicalEdgesAndRejectsOutOfImageOrSubpixelArea()
    {
        Assert.That(new ScanRegion(25.4m, 12.7m, 50.8m, 25.4m).ToPixels(300, 1000, 1000), Is.EqualTo(new PageCrop(300, 150, 600, 300)));
        Assert.Throws<InvalidDataException>(() => new ScanRegion(25.4m, 0, 50.8m, 25.4m).ToPixels(300, 500, 1000));
        Assert.Throws<InvalidDataException>(() => new ScanRegion(0, 0, .001m, .001m).ToPixels(100, 100, 100));
        Assert.That(new ScanRegion(decimal.MaxValue, 0, decimal.MaxValue, 1).Fits(210, 297), Is.False);
    }

    [Test]
    public void RequestValidationRespectsEachSourcesColorDpiSizeAndGeometry()
    {
        var settings = new ScanSettings(ScanTestData.Device(), PaperSource.Flatbed, BitDepth.Color, 300, PageSize.A4);
        var caps = new ScanCaps
        {
            PaperSourceCaps = new() { SupportsFlatbed = true, SupportsFeeder = true },
            FlatbedCaps = ScanTestData.Source(),
            FeederCaps = new() { BitDepthCaps = new() { SupportsBlackAndWhite = true }, DpiCaps = new() { Values = [200] }, PageSizeCaps = new() { ScanArea = new(100, 100, PageSizeUnit.Millimetre), SupportsCustomPageSize = false } }
        };
        Assert.Multiple(() =>
        {
            Assert.That(ScanValidation.Error(settings, caps), Is.Null);
            Assert.That(ScanValidation.Error(settings with { Source = PaperSource.Duplex }, caps), Does.Contain("source"));
            Assert.That(ScanValidation.Error(settings with { Source = PaperSource.Feeder }, caps), Does.Contain("color"));
            Assert.That(ScanValidation.Error(settings with { Dpi = 123 }, caps), Does.Contain("DPI"));
            Assert.That(ScanValidation.Error(settings with { Size = new(500, 500, PageSizeUnit.Millimetre) }, caps), Does.Contain("bounds"));
            Assert.That(ScanValidation.Error(settings with { Area = new(200, 0, 100, 20) }, caps), Does.Contain("inside"));
            Assert.That(ScanValidation.Error(settings with { Area = new(0, 0, .00001m, .00001m) }, caps), Does.Contain("pixel"));
            Assert.That(ScanValidation.Error(settings with { Source = PaperSource.Feeder, Color = BitDepth.BlackAndWhite, Dpi = 200, Size = new(50, 50, PageSizeUnit.Millimetre), CustomSize = true }, caps), Does.Contain("custom-size support"));
        });
    }

    [Test]
    public async Task FakeScanReceivesSdkMappingAndFreshCapabilitiesBlockInvalidRequests()
    {
        var fake = new FakeScannerBackend();
        using var scanner = new Scanner(Root, backend: fake);
        using var session = CreateSession();
        var settings = new ScanSettings(ScanTestData.Device("genesys:usb", driver: Driver.Sane), PaperSource.Feeder,
            BitDepth.Color, 600, new(100, 150, PageSizeUnit.Millimetre), true, HorizontalAlign.Center, new(10, 20, 30, 40), true);
        await scanner.Scan(session, settings, () => { }, _ => { }, CancellationToken.None);
        var options = fake.Scanned!;
        Assert.Multiple(() =>
        {
            Assert.That(options.Device!.ID, Is.EqualTo(settings.Device.ID));
            Assert.That(options.Device.Driver, Is.EqualTo(Driver.Sane));
            Assert.That(options.Device.Name, Is.Empty, "Empty transport name disables upstream SANE's name fallback");
            Assert.That(options.PageSize!.WidthInMm, Is.EqualTo(100));
            Assert.That(options.PageAlign, Is.EqualTo(HorizontalAlign.Center));
            Assert.That(options.PaperSource, Is.EqualTo(PaperSource.Feeder));
            Assert.That(options.Dpi, Is.EqualTo(600));
            Assert.That(options.BitDepth, Is.EqualTo(BitDepth.Color));
            Assert.That(options.AutoDeskew, Is.True);
            Assert.That(options.MaxQuality, Is.True);
            Assert.That(session.Pages, Is.Empty);
        });
        Assert.ThrowsAsync<InvalidOperationException>(() => scanner.Scan(session, settings with { Dpi = 123 }, () => { }, _ => { }, CancellationToken.None));
        Assert.That(fake.Scanned, Is.SameAs(options));
        fake.Caps = new();
        Assert.ThrowsAsync<IOException>(() => scanner.Scan(session, settings, () => { }, _ => { }, CancellationToken.None));
        Assert.That(fake.Scanned, Is.SameAs(options), "No second job when refreshed capabilities are unusable");
    }

    [TestCase("genesys:usb", "", "")]
    [TestCase("escl:http://scanner", "", "")]
    [TestCase("airscan:e0:scanner", "", "")]
    [TestCase("genesys:usb", "Same display name", "Vendor")]
    public void ExactSaneGuardCannotMatchSdkGeneratedNamesAndSurvivesSdkClone(string id, string model, string vendor)
    {
        var assembly = typeof(ScanController).Assembly;
        var info = Activator.CreateInstance(assembly.GetType("NAPS2.Scan.Internal.Sane.Native.SaneDeviceInfo")!)!;
        info.GetType().GetField("Name")!.SetValue(info, id);
        info.GetType().GetField("Model")!.SetValue(info, model);
        info.GetType().GetField("Vendor")!.SetValue(info, vendor);
        var sdkName = assembly.GetType("NAPS2.Scan.Internal.Sane.SaneScanDriver")!
            .GetMethod("GetName", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [info]);
        var settings = new ScanSettings(ScanTestData.Device(id, model, Driver.Sane), PaperSource.Flatbed, BitDepth.Color, 300, PageSize.A4);
        var clone = Scanner.ExactDeviceOptions(settings).Clone();
        Assert.That(clone.Device!.Name, Is.Empty.And.Not.EqualTo(sdkName));
        Assert.That(ScannerIdentity.From(clone.Device).Matches(settings.Device), Is.True);
        Assert.That(settings.Device.Name, Is.EqualTo(model), "UI identity remains untouched");
    }
}
