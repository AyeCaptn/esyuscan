using System.Runtime.CompilerServices;
using Avalonia.Wayland.Server.Persistent;
using NWayland.Protocols.XdgShell;
using NUnit.Framework;

namespace Scan.WaylandTests;

[TestFixture]
public sealed class SurfaceConfigureTests
{
    [Test]
    public void CurrentSurfaceDispatchesItsConfigureSerial()
    {
        var surface = NewSurface();
        var window = NewWindow(surface);
        var listener = new ConfigureListener(window);

        listener.ConfigureSurface(surface, 42);

        Assert.That(window.ConfiguredSerial, Is.EqualTo(42));
    }

    [Test]
    public void ConfigureQueuedBeforeDisconnectIsIgnored()
    {
        var oldSurface = NewSurface();
        var window = NewWindow(null);
        var listener = new ConfigureListener(window);

        listener.ConfigureSurface(oldSurface, 42);

        Assert.That(window.ConfiguredSerial, Is.Null);
    }

    [Test]
    public void ConfigureFromAnOldConnectionDoesNotConfigureTheNewSurface()
    {
        var oldSurface = NewSurface();
        var window = NewWindow(NewSurface());
        var listener = new ConfigureListener(window);

        listener.ConfigureSurface(oldSurface, 42);

        Assert.That(window.ConfiguredSerial, Is.Null);
    }

    // Only proxy identity is needed: no display, worker thread, or native handles.
    private static XdgSurface NewSurface()
    {
        var surface = (XdgSurface)RuntimeHelpers.GetUninitializedObject(typeof(XdgSurface));
        GC.SuppressFinalize(surface);
        return surface;
    }

    private static RecordingWindow NewWindow(XdgSurface? surface)
    {
        var window = (RecordingWindow)RuntimeHelpers.GetUninitializedObject(typeof(RecordingWindow));
        typeof(WXdgShellSurface).GetProperty(nameof(WXdgShellSurface.XdgSurface))!.SetValue(window, surface);
        return window;
    }

    private sealed class RecordingWindow() : WXdgShellSurface(null!, null!)
    {
        public uint? ConfiguredSerial { get; private set; }
        protected override void OnConfigureBatchComplete(uint serial) => ConfiguredSerial = serial;
    }

    private sealed class ConfigureListener(WXdgShellSurface window) : WXdgShellSurface.XdgSurfaceListener(window)
    {
        public void ConfigureSurface(XdgSurface surface, uint serial) => Configure(surface, serial);
    }
}
