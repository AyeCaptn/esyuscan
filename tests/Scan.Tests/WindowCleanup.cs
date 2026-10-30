using Avalonia.Controls;

namespace Scan.Tests;

internal static class WindowCleanup
{
    // Tests of other features explicitly discard their fixture pages during cleanup.
    public static void CloseForTest(this Window window)
    {
        if (window.DataContext is MainViewModel model) model.MarkSaved();
        window.Close();
    }
}
