using PropertyChangedEventArgs = System.ComponentModel.PropertyChangedEventArgs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NAPS2.Scan;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class StartupDiscoveryTests : TestFiles
{
    private sealed class DeferredFinder : IScannerDiscovery
    {
        public readonly TaskCompletionSource<ScannerDiscovery> Results = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<ScanDevice, CancellationToken, Task<ScanCaps>> Probe = (_, _) => Task.FromResult(ScanTestData.Caps());
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<ScannerDiscovery> Discover(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            Started.TrySetResult(Dispatcher.UIThread.CheckAccess());
            return Results.Task.WaitAsync(token);
        }
        public Task<ScanCaps> Capabilities(ScanDevice device, CancellationToken token) => Probe(device, token);
        public void Dispose() { }
    }

    private ScanPreferencesStore Preferences => new(Path.Combine(Root, "preferences.json"));
    private ScannerCacheStore Cache => new(Path.Combine(Root, "scanners.json"));
    private MainViewModel CachedModel(params ScanDevice[] devices)
    {
        Cache.Save(devices);
        return new MainViewModel(Preferences, Cache);
    }

    private static ScannerDiscovery Found(params ScanDevice[] devices) => new(devices, []);
    private static Task<OcrAvailability> PendingOcr(CancellationToken token) => new TaskCompletionSource<OcrAvailability>().Task.WaitAsync(token);

    private static async Task Until(MainViewModel model, Func<bool> ready)
    {
        if (ready()) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args) { if (ready()) completion.TrySetResult(); }
        model.PropertyChanged += Changed;
        try
        {
            if (ready()) completion.TrySetResult();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { model.PropertyChanged -= Changed; }
    }

    [Test]
    public void CachedScannersRestoreImmediatelyAndStayUnverified()
    {
        var first = ScanTestData.Device();
        var second = ScanTestData.Device("genesys:usb", "USB scanner", Driver.Sane);
        using var model = CachedModel(first, second);

        Assert.That((model.Devices.Select(device => device.ID).ToArray(), model.HasValidScanSettings),
            Is.EqualTo((new[] { first.ID, second.ID }, false)));
    }

    [Test]
    public void LastSelectedScannerRestoresEvenBeforeThereIsADiscoveryCache()
    {
        Preferences.Save(new(1, ScanTestData.Settings(), []));

        using var model = new MainViewModel(Preferences);

        Assert.That(model.SelectedDevice!.ID, Is.EqualTo(ScanTestData.Device().ID));
    }

    [Test]
    public async Task DiscoveryKeepsOfflineDevicesAcrossRestart()
    {
        var remembered = ScanTestData.Device("offline");
        var current = ScanTestData.Device("online");
        using (var model = CachedModel(remembered))
        {
            model.SetDiscovery(Found(current));
            await model.RememberScanners();
        }

        using var restarted = new MainViewModel(Preferences, Cache);

        Assert.That(restarted.Devices.Select(device => device.ID), Is.EquivalentTo(new[] { "offline", "online" }));
    }

    [Test]
    public void RediscoveryDoesNotResetTheActiveSelectionOrUserSettings()
    {
        using var model = new MainViewModel();
        var selected = ScanTestData.Device();
        model.SetDevices([selected]);
        model.SetCapabilities(selected, ScanTestData.Caps());
        model.Dpi = 600;

        model.SetDiscovery(Found(selected with { Name = "Updated discovery name" }, ScanTestData.Device("another")));

        Assert.That((model.SelectedDevice, model.Dpi, model.HasValidScanSettings), Is.EqualTo((selected, 600, true)));
    }

    [Test]
    public async Task ForgettingAManualScannerCannotBeUndoneByAnOlderCacheWrite()
    {
        var device = ManualScannerAddress.Create(Driver.Escl, "scanner.local");
        using (var model = CachedModel(device))
        {
            model.AddManualDevice(device, ScanTestData.Caps());
            model.SetDiscovery(Found(device));
            var olderWrite = model.RememberScanners();
            model.RemoveManualDevice();
            await model.RememberScanners();
            await olderWrite;
        }

        using var restarted = new MainViewModel(Preferences, Cache);

        Assert.That(restarted.Devices, Is.Empty);
    }

    [Test]
    public void BrokenCacheReportsAWarningInsteadOfPreventingStartup()
    {
        File.WriteAllText(Cache.FilePath, "{broken");

        using var model = new MainViewModel(Preferences, Cache);

        Assert.That(model.DiscoveryExplanation, Does.Contain("could not be read"));
    }

    [AvaloniaTest]
    [TestCase("NewSessionButton")]
    [TestCase("OpenSessionButton")]
    [TestCase("ImportFilesButton")]
    [TestCase("CombinedExportButton")]
    [TestCase("SettingsButton")]
    public async Task SessionActionsRemainEnabledDuringSlowDiscoveryAndOcr(string name)
    {
        var model = CachedModel(ScanTestData.Device());
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var caps = new TaskCompletionSource<ScanCaps>();
        var finder = new DeferredFinder { Probe = (_, token) => caps.Task.WaitAsync(token) };
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            await finder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(window.FindControl<Button>(name)!.IsEffectivelyEnabled, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task DiscoveryRunsOffTheUiThread()
    {
        var model = new MainViewModel();
        var finder = new DeferredFinder();
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            Assert.That(await finder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task ARememberedScannerBecomesReadyBeforeSlowDiscoveryFinishes()
    {
        var model = CachedModel(ScanTestData.Device());
        model.SetSession(CreateSession());
        var finder = new DeferredFinder();
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            await Until(model, () => !model.CheckingScanner && model.HasValidScanSettings);

            Assert.That(model.CanScan, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task RepeatedRefreshDoesNotStartDuplicateDiscoveryJobs()
    {
        var model = new MainViewModel();
        var finder = new DeferredFinder();
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            await finder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var button = window.FindControl<Button>("RefreshDevicesButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.That(finder.Calls, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task FailedCapabilityChecksDoNotDisableSessionEditing()
    {
        var model = CachedModel(ScanTestData.Device());
        var finder = new DeferredFinder { Probe = (_, _) => Task.FromException<ScanCaps>(new IOException("Fixture scanner offline")) };
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            await Until(model, () => !model.CheckingScanner && model.ScannerStatus.Contains("Fixture scanner offline"));

            Assert.That(model.CanEdit, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task BackgroundResultsDoNotOverwriteTheForegroundStatus()
    {
        var model = CachedModel(ScanTestData.Device());
        model.Status = "Foreground document work";
        var finder = new DeferredFinder();
        var ocr = new TaskCompletionSource<OcrAvailability>();
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: token => ocr.Task.WaitAsync(token));
        window.Show();
        try
        {
            finder.Results.SetResult(Found(ScanTestData.Device()));
            ocr.SetResult(new(true, ["eng"], "Fixture OCR ready"));
            await Until(model, () => !model.FindingScanners && !model.CheckingScanner && !model.CheckingOcr);

            Assert.That(model.Status, Is.EqualTo("Foreground document work"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task StartupDoesNotQueryPrinters()
    {
        var model = new MainViewModel();
        var finder = new DeferredFinder();
        var desktop = new FakeDesktopCommand();
        var window = new MainWindow(model, desktopCommand: desktop, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            await finder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(desktop.Calls, Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task TheWindowCanCloseWhileDiscoveryIsStillPending()
    {
        var model = new MainViewModel();
        var finder = new DeferredFinder();
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        await finder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        window.Close();

        Assert.That(window.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task SelectingAnotherScannerDoesNotWaitForTheFirstScannerCheck()
    {
        var first = ScanTestData.Device("first");
        var second = ScanTestData.Device("second");
        var model = CachedModel(first, second);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<ScanCaps>();
        var finder = new DeferredFinder
        {
            Probe = (device, token) =>
            {
                if (device.ID != first.ID) return Task.FromResult(ScanTestData.Caps());
                started.TrySetResult();
                return held.Task.WaitAsync(token);
            }
        };
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.FindControl<ComboBox>("DevicePicker")!.SelectedItem = model.Devices.Single(device => device.ID == second.ID);
            await Until(model, () => !model.CheckingScanner && model.HasValidScanSettings);

            Assert.That(model.CurrentScanSettings().Device.ID, Is.EqualTo(second.ID));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task PausedScannerChecksResumeAfterForegroundWorkEnds()
    {
        var model = CachedModel(ScanTestData.Device());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<ScanCaps>();
        var calls = 0;
        var finder = new DeferredFinder
        {
            Probe = (_, token) =>
            {
                if (Interlocked.Increment(ref calls) > 1) return Task.FromResult(ScanTestData.Caps());
                started.TrySetResult();
                return held.Task.WaitAsync(token);
            }
        };
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            model.Busy = true;
            model.Busy = false;
            await Until(model, () => !model.CheckingScanner && model.HasValidScanSettings);

            Assert.That(calls, Is.EqualTo(2));
        }
        finally { model.Busy = false; window.Close(); }
    }

    [AvaloniaTest]
    public async Task OcrDetectionRunsOffTheUiThread()
    {
        var model = new MainViewModel();
        var finder = new DeferredFinder();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: token =>
        {
            started.TrySetResult(Dispatcher.UIThread.CheckAccess());
            return PendingOcr(token);
        });
        window.Show();
        try
        {
            Assert.That(await started.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task ACacheWriteFailureDoesNotShowADocumentOperationError()
    {
        var blocked = Path.Combine(Root, "blocked");
        File.WriteAllText(blocked, "fixture");
        var model = new MainViewModel(Preferences, new ScannerCacheStore(Path.Combine(blocked, "scanners.json")));
        var finder = new DeferredFinder();
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr);
        window.Show();
        try
        {
            finder.Results.SetResult(Found(ScanTestData.Device()));
            await Until(model, () => !model.FindingScanners && !model.CheckingScanner && model.HasValidScanSettings);

            Assert.That(model.Error, Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    [TestCase(1280, 900)]
    [TestCase(760, 680)]
    public async Task RenderUsableWorkspaceWhileStartupDiscoveryIsPending(int width, int height)
    {
        OmarchyTheme.Apply(ThemePalette.Default);
        var model = CachedModel(ScanTestData.Device(name: "Saved AirScan scanner"));
        var session = CreateSession();
        AddPages(session, 3, detailed: true);
        model.SetSession(session);
        var held = new TaskCompletionSource<ScanCaps>();
        var finder = new DeferredFinder { Probe = (_, token) => held.Task.WaitAsync(token) };
        var window = new MainWindow(model, scannerDiscoveryFactory: () => finder, ocrProbe: PendingOcr) { Width = width, Height = height };
        window.Show();
        try
        {
            await finder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
            bitmap.Render(window);
            if (Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"startup-discovery-{width}.png"), new PngBitmapEncoderOptions());
            }

            Assert.That(window.FindControl<Button>("CombinedExportButton")!.IsEffectivelyEnabled, Is.True);
        }
        finally { window.Close(); }
    }
}
