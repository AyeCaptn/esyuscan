using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Scan;

internal static class NativeText
{
    [ModuleInitializer]
    internal static void Configure()
    {
        if (!OperatingSystem.IsLinux()) return;
        // GTK can load the system HarfBuzz before Avalonia. Mixing it with the bundled
        // implementation causes native font objects to cross incompatible allocators.
        // Share the distro library instead, independent of initialization order.
        NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Buffer).Assembly,
            (name, _, _) => name == "libHarfBuzzSharp" ? NativeLibrary.Load("libharfbuzz.so.0") : IntPtr.Zero);
    }
}
