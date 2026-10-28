using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Scan;

public sealed partial class MainWindow
{
    private readonly CupsPrinting printing;
    private readonly DesktopMail mail;

    private DocumentDelivery Delivery => new((session, path, token, ocr) => Engine.Export(session, path, ExportFormat.CombinedPdf, token, ocr,
        progress => Dispatcher.UIThread.Post(() => { model.Progress = progress.Fraction; model.Status = progress.Message; }), selectedPages: model.ExportSelection), printing, mail);

    private async void PrintDocument(object? sender, RoutedEventArgs args) => await HandleUi(async () =>
    {
        if (!model.CanExport) return;
        string[] printers = [];
        var loaded = false;
        await Run(async token => { model.Status = "Looking for CUPS printers…"; printers = await printing.Printers(token); loaded = true; });
        if (!loaded || !model.CanExport) return;
        var options = await new PrintWindow(printers).ShowDialog<PrintOptions?>(this);
        if (options is null || !model.CanExport) return;
        await Run(async token =>
        {
            model.ExportProgress = true;
            model.Status = "Preparing document for printing…";
            var id = await Task.Run(() => Delivery.Print(model.Session!, options, token), token);
            model.LastPrintJob = id;
            model.Status = $"Submitted CUPS job {id}. This confirms queue submission, not printed pages.";
        });
    });

    private async void EmailDocument(object? sender, RoutedEventArgs args)
    {
        if (!model.CanExport) return;
        if (model.SearchablePdf && model.OcrLanguage is null)
        {
            model.Error = "Select an installed OCR language. " + model.OcrStatus;
            return;
        }
        var ocr = model.SearchablePdf ? new PdfOcrOptions(model.OcrLanguage!) : null;
        await Run(async token =>
        {
            model.ExportProgress = true;
            model.Status = "Preparing PDF attachment…";
            var path = await Task.Run(() => Delivery.Email(model.Session!, ocr, token), token);
            model.Status = "Mail composer requested. Verify the attachment; temporary PDFs are kept for 7 days.";
            model.Error = "";
            model.MailAttachment = path;
        });
    }

    private async void CancelPrint(object? sender, RoutedEventArgs args)
    {
        if (!model.CanCancelPrint) return;
        var id = model.LastPrintJob!;
        await Run(async _ =>
        {
            await printing.Cancel(id);
            model.LastPrintJob = null;
            model.Status = $"CUPS accepted cancellation for {id}. Pages already printed cannot be recalled.";
        });
    }
}
