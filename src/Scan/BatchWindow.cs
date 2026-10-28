using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Scan;

// A separate setup/run/review surface leaves the ordinary one-click Scan workflow unchanged.
public sealed class BatchWindow : Window
{
    private readonly Session session;
    private readonly Scanner engine;
    private readonly Func<Action, CancellationToken, Task>? scan;
    private readonly Action changed;
    private readonly PdfOcrOptions? ocr;
    private readonly StackPanel setup = new() { Spacing = 8 };
    private readonly NumericUpDown count = new() { Name = "BatchCount", Minimum = 1, Maximum = 1000, Value = 2 };
    private readonly NumericUpDown seconds = new() { Name = "BatchDelay", Minimum = 0, Maximum = 86400, Value = 0 };
    private readonly CheckBox prompt = new() { Content = "Prompt between scans", IsChecked = true };
    private readonly ComboBox grouping = new() { ItemsSource = new[] { "One document per scan", "Separate at matching barcode" }, SelectedIndex = 0 };
    private readonly TextBox separatorText = new() { Text = "ESYUSCAN-SEPARATOR", MaxLength = 200 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox list = new() { Name = "BatchPages", MinHeight = 120, MaxHeight = 190 };
    private readonly Image image = new() { Height = 190, Stretch = Stretch.Uniform };
    private readonly CheckBox boundary = new() { Content = "Start a document before selected page" };
    private readonly CheckBox separator = new() { Content = "Selected page is a separator sheet" };
    private readonly CheckBox include = new() { Content = "Include separator sheets in documents", IsChecked = true };
    private readonly CheckBox reviewed = new() { Name = "BatchReviewed", Content = "I checked every page, boundary and separator" };
    private readonly Button start = new() { Name = "RunBatch", Content = "Start batch", Classes = { "primary" } };
    private readonly Button continuePartial = new() { Content = "Scan only remaining sheets of interrupted scan" };
    private readonly Button acceptPartial = new() { Content = "Accept partial scan; advance to next scan" };
    private readonly Button reviewNow = new() { Content = "Stop batch and review completed pages" };
    private readonly Button detect = new() { Content = "Detect pending barcodes" };
    private readonly Button export = new() { Name = "ExportBatch", Content = "Export reviewed documents… · Ctrl+S", Classes = { "primary" } };
    private readonly Button exit = new() { Content = "Exit batch: keep all pages; discard grouping" };
    private readonly Button cancel = new() { Name = "CancelBatch", Content = "Cancel operation · Esc" };
    private readonly Button close = new() { Content = "Close (keep batch for later) · Esc" };
    private CancellationTokenSource? operation;
    private bool syncing;
    private bool isClosed;
    private Bitmap? bitmap;
    private sealed record Row(BatchPage Page, string Caption)
    {
        public override string ToString() => Caption;
    }

    public BatchWindow(Session session, Scanner engine, Func<Action, CancellationToken, Task>? scan, Action changed,
        PdfOcrOptions? ocr = null, string scannerDescription = "No valid scanner selected. Close this window to select scanner settings.")
    {
        this.session = session;
        this.engine = engine;
        this.scan = scan;
        this.changed = changed;
        this.ocr = ocr;
        Title = "esyuscan · Batch scanning";
        Width = 760;
        Height = 860;
        MinWidth = 520;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        setup.Children.Add(Label("1 · Batch setup", 18));
        setup.Children.Add(Label("Scan count (one flatbed page or feeder stack per scan)"));
        setup.Children.Add(count);
        setup.Children.Add(Label("Delay between scans, in seconds (cancellable)"));
        setup.Children.Add(seconds);
        setup.Children.Add(prompt);
        setup.Children.Add(grouping);
        setup.Children.Add(Label("Exact separator text · QR or Code 128 (case-sensitive)"));
        setup.Children.Add(separatorText);
        setup.Children.Add(Label("Separator sheets are kept by default. Review can exclude them from document exports; originals always stay saved."));
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        body.Children.Add(Label("Batch scans use your currently selected scanner and settings. Check those settings before continuing. Guided manual duplex is a separate workflow."));
        body.Children.Add(Label(scannerDescription));
        body.Children.Add(Label(ocr is null ? "Document export: standard PDFs. Choose searchable PDFs in the main window before opening batch review to add OCR."
            : ocr.Language.Length > 0 ? $"Document export: searchable PDFs · {ocr.Language}."
            : "Choose an installed OCR language in the main window before exporting searchable documents."));
        body.Children.Add(setup);
        body.Children.Add(status);
        body.Children.Add(summary);
        body.Children.Add(start);
        body.Children.Add(continuePartial);
        body.Children.Add(acceptPartial);
        body.Children.Add(reviewNow);
        body.Children.Add(detect);
        body.Children.Add(list);
        body.Children.Add(image);
        body.Children.Add(boundary);
        body.Children.Add(separator);
        body.Children.Add(include);
        body.Children.Add(reviewed);
        body.Children.Add(export);
        body.Children.Add(Label("Documents export only pages captured by this batch. The main window’s three export buttons include ALL session pages, including separator sheets, after review is confirmed. Exit batch before ordinary page editing."));
        body.Children.Add(exit);
        body.Children.Add(close);
        var shell = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        shell.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        cancel.Margin = new Thickness(24, 8);
        Grid.SetRow(cancel, 1);
        shell.Children.Add(cancel);
        Content = shell;
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        foreach (var control in new Control[] { count, seconds, list, reviewed, start, export, cancel })
            names.Register(control.Name!, control);
        start.Click += async (_, _) => await Run(async token =>
        {
            if (session.Batch is null)
                session.BeginBatch(new((int)count.Value!, (int)seconds.Value!, prompt.IsChecked == true,
                    (BatchGrouping)grouping.SelectedIndex, separatorText.Text ?? ""));
            Refresh();
            await session.RunBatch(scanToken => (scan ?? throw new InvalidOperationException("Select valid scanner settings in the main window first."))(
                () => Dispatcher.UIThread.Post(Refresh), scanToken), token,
                () => Dispatcher.UIThread.Post(Refresh));
            if (session.Batch?.Phase == BatchPhase.Review) await Task.Run(() => engine.DetectBatchBarcodes(session, token), token);
            status.Text = "Saved. Continue when ready, or review the completed pages.";
        });
        continuePartial.Click += (_, _) => Resolve(true);
        acceptPartial.Click += (_, _) => Resolve(false);
        reviewNow.Click += (_, _) => Edit(session.ReviewBatchNow);
        detect.Click += async (_, _) => await Run(async token =>
        {
            await Task.Run(() => engine.DetectBatchBarcodes(session, token), token);
            status.Text = "Barcode suggestions saved. Review every page and correct missed or unexpected matches.";
        });
        list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => Label(row?.Caption ?? ""));
        list.SelectionChanged += (_, _) => { if (!syncing) Select(); };
        boundary.IsCheckedChanged += (_, _) => UpdateSelected();
        separator.IsCheckedChanged += (_, _) => UpdateSelected();
        include.IsCheckedChanged += (_, _) => { if (!syncing) Edit(() => session.SetBatchIncludeSeparators(include.IsChecked == true)); };
        reviewed.IsCheckedChanged += (_, _) => { if (!syncing) Edit(() => session.ConfirmBatchReview(reviewed.IsChecked == true)); };
        export.Click += async (_, _) => await Export();
        exit.Click += (_, _) =>
        {
            if (operation is not null) return;
            Edit(session.ExitBatchKeepingAllPages);
            if (session.Batch is null) Close();
        };
        cancel.Click += (_, _) => operation?.Cancel();
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Handled) return;
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.Control)
            {
                if (start.IsVisible && start.IsEnabled) start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (args.Key == Key.S && args.KeyModifiers == KeyModifiers.Control)
            {
                if (export.IsVisible && export.IsEnabled) export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None)
            {
                if (args.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<ComboBox>().Any(picker => picker.IsDropDownOpen)) return;
                if (operation is not null) operation.Cancel();
                else Close();
            }
            else return;
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        close.Click += (_, _) => Close();
        Closing += (_, args) =>
        {
            if (operation is null) return;
            args.Cancel = true;
            operation.Cancel();
            status.Text = "Stopping safely. Close again once the operation ends.";
        };
        Closed += (_, _) => { isClosed = true; image.Source = null; bitmap?.Dispose(); changed(); };
        Refresh();
    }

    private static TextBlock Label(string text, double size = 12) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size };

    private void Resolve(bool same) => Edit(() => session.ResolveInterruptedBatchScan(same));
    private void Edit(Action action)
    {
        try { action(); status.Text = "Saved. Check the document order below."; }
        catch (Exception error) { status.Text = error.Message; }
        Refresh();
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        status.Text = "Batch running. Every completed page is saved immediately.";
        Refresh();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { status.Text = "Stopped. Saved pages remain; resolve any interrupted scan before continuing."; }
        catch (Exception error) { status.Text = $"Stopped: {error.Message}. All saved pages remain available for review."; }
        finally { operation = null; Refresh(); changed(); }
    }

    private void Refresh()
    {
        if (isClosed) return;
        syncing = true;
        try
        {
            var state = session.Batch;
            var busy = operation is not null;
            var review = state?.Phase == BatchPhase.Review;
            var selectedId = (list.SelectedItem as Row)?.Page.FileName;
            var rows = new List<Row>();
            var documents = state is null ? [] : session.BatchDocuments();
            var assignments = documents.SelectMany(document => document.PageIds.Select(id => (id, document.Number)))
                .ToDictionary(pair => pair.id, pair => pair.Number);
            if (state is not null)
                foreach (var attempt in state.Scans)
                    foreach (var page in attempt.Pages)
                        rows.Add(new(page, $"Scan {attempt.Number} · page {Array.IndexOf(attempt.Pages, page) + 1} · "
                            + (assignments.TryGetValue(page.FileName, out var number) ? $"document {number}" : "excluded separator")
                            + (page.Separator ? " · SEPARATOR" : "")
                            + (state.Options.Grouping == BatchGrouping.Barcode ? page.DetectionDone
                                ? $" · barcode: {page.DetectedText ?? "none"}" : " · detection pending" : "")));
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(row => row.Page.FileName == selectedId) ?? rows.FirstOrDefault();
            list.IsVisible = state is not null;
            image.IsVisible = rows.Count > 0;
            setup.IsVisible = state is null;
            setup.IsEnabled = !busy;
            start.IsVisible = !review;
            start.IsEnabled = !busy && !session.BatchNeedsDecision && scan is not null;
            start.Content = (state is null ? "Start batch" : state.Phase == BatchPhase.Prompt ? "Ready: continue to next scan" : "Resume batch explicitly") + " · Ctrl+Enter";
            continuePartial.IsVisible = acceptPartial.IsVisible = session.BatchNeedsDecision;
            continuePartial.IsEnabled = acceptPartial.IsEnabled = !busy;
            reviewNow.IsVisible = state is not null && !review;
            reviewNow.IsEnabled = !busy;
            detect.IsVisible = review && state!.Options.Grouping == BatchGrouping.Barcode;
            detect.IsEnabled = !busy;
            boundary.IsVisible = separator.IsVisible = include.IsVisible = reviewed.IsVisible = review;
            boundary.IsEnabled = separator.IsEnabled = review && !busy && rows.Count > 0;
            include.IsEnabled = reviewed.IsEnabled = review && !busy;
            include.IsChecked = state?.Options.IncludeSeparators ?? true;
            reviewed.IsChecked = state?.Reviewed == true;
            export.IsVisible = review;
            export.IsEnabled = !busy && state?.Reviewed == true && documents.Count > 0 && (ocr is null || ocr.Language.Length > 0);
            list.IsEnabled = !busy;
            exit.IsVisible = state is not null;
            exit.IsEnabled = close.IsEnabled = !busy;
            cancel.IsVisible = busy;
            summary.Text = state is null ? "No scans start until you choose Start batch."
                : $"{state.Phase} · {state.Scans.Count(item => item.Status == BatchScanStatus.Complete)} of {state.Options.ScanCount} scans accepted · {rows.Count} saved pages · {documents.Count} documents · {rows.Count - assignments.Count} excluded sheets. "
                    + $"{session.Pages.Count - rows.Count} pre-existing pages are outside this batch.\n"
                    + $"{state.Options.Grouping} · delay {state.Options.DelaySeconds}s · prompts {(state.Options.PromptBetweenScans ? "on" : "off")}"
                    + (state.Options.Grouping == BatchGrouping.Barcode ? $" · exact text: {state.Options.SeparatorText}" : "") + "\n"
                    + string.Join("; ", documents.Select(document => $"Document {document.Number}: {document.PageIds.Length} pages"))
                    + (state.Phase == BatchPhase.Delay ? $"\nWaiting {state.Options.DelaySeconds} seconds before next scan…" : "")
                    + (session.BatchNeedsDecision ? "\nInterrupted scan: remove completed sheets before continuing the same scan, or accept its partial result. No completed scan is replayed." : "");
        }
        finally { syncing = false; }
        Select();
    }

    private void Select()
    {
        syncing = true;
        try
        {
            var page = (list.SelectedItem as Row)?.Page;
            boundary.IsEnabled = separator.IsEnabled = operation is null && session.Batch?.Phase == BatchPhase.Review
                && page is not null && (session.Batch.Options.Grouping != BatchGrouping.Barcode || page.DetectionDone);
            boundary.IsChecked = page?.StartsDocument == true;
            separator.IsChecked = page?.Separator == true;
            image.Source = null;
            bitmap?.Dispose();
            bitmap = null;
            if (page is not null)
            {
                using var png = PageRenderer.Png(session, session.Pages.Single(item => item.FileName == page.FileName));
                bitmap = Bitmap.DecodeToHeight(png, 400);
                image.Source = bitmap;
            }
        }
        finally { syncing = false; }
    }
    private void UpdateSelected()
    {
        if (syncing || operation is not null || list.SelectedItem is not Row row) return;
        Edit(() => session.SetBatchPage(row.Page.FileName, boundary.IsChecked == true, separator.IsChecked == true));
    }

    private async Task Export()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose parent folder for a new batch export", AllowMultiple = false });
            if (folders.Count == 0) return;
            var parent = folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder.");
            // Unique default plus exporter no-overwrite check also protects rapid repeated exports.
            var destination = Path.Combine(parent, $"batch-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            await Run(async token =>
            {
                await Task.Run(() => engine.ExportBatchDocuments(session, destination, token, ocr,
                    update => Dispatcher.UIThread.Post(() => status.Text = update.Message)), token);
                status.Text = $"Export saved: {destination}";
            });
        }
        catch (Exception error) { status.Text = error.Message; }
    }
}
