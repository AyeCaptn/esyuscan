using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Scan;

public sealed partial class MainWindow
{
    private async void CopyPage(object? sender, RoutedEventArgs args)
    {
        if (editingPage || !model.CanChangePage) return;
        var session = model.Session!;
        var page = model.SelectedPage!.Page;
        await Run(async token =>
        {
            var clipboard = Clipboard ?? throw new InvalidOperationException("The clipboard is unavailable.");
            model.Status = "Copying page as an image…";
            using var png = await Task.Run(() => PageRenderer.Png(session, page, token), token);
            token.ThrowIfCancellationRequested();
            var image = new ClipboardImage(new Bitmap(png));
            try { await clipboard.SetDataAsync(image); }
            catch { image.Dispose(); throw; }
            model.Status = "Page copied as an image. Paste it into another app with Ctrl+V.";
        }, "Copy cancelled. Your scans are unchanged.");
    }

    // Clipboard ownership can outlive the preview and temporary session. Release the
    // independent bitmap only when the platform releases this transfer.
    private sealed class ClipboardImage : IAsyncDataTransfer
    {
        private readonly Bitmap bitmap;
        private readonly DataTransfer data = new();

        public ClipboardImage(Bitmap bitmap)
        {
            this.bitmap = bitmap;
            data.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
        }

        public IReadOnlyList<DataFormat> Formats => data.Formats;
        public IReadOnlyList<IAsyncDataTransferItem> Items => data.Items;
        public void Dispose() => bitmap.Dispose();
    }
}
