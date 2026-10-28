using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NAPS2.Scan;

namespace Scan;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private Scanner? scanner;
    private CancellationTokenSource? operation;
    private bool changingDevice;
    private readonly bool discover;

    public MainWindow() : this(new MainViewModel(new ScanPreferencesStore())) { }

    public MainWindow(MainViewModel model, bool discover = true, IDesktopCommand? desktopCommand = null,
        Func<IScannerDiscovery>? scannerDiscoveryFactory = null, Func<CancellationToken, Task<OcrAvailability>>? ocrProbe = null)
    {
        this.model = model;
        desktopCommand ??= new DesktopCommand();
        printing = new CupsPrinting(desktopCommand);
        mail = new DesktopMail(desktopCommand);
        this.discover = discover;
        this.scannerDiscoveryFactory = scannerDiscoveryFactory ?? (() => new Scanner());
        this.ocrProbe = ocrProbe ?? (token => new OcrRuntime().Detect(token));
        DataContext = model;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ComboBox>("OcrLanguageSelector")!.ItemTemplate = new FuncDataTemplate<string>((code, _) =>
            new TextBlock { Text = code is null ? "" : OcrLanguageCatalog.DisplayName(code), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        // Handle edit shortcuts before the page tray consumes arrow/delete navigation keys.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        InitializeNavigation();
        InitializeShortcuts();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Busy)) ForegroundBusyChanged();
        };
        SizeChanged += (_, _) =>
        {
            var compact = ClientSize.Width < 1100;
            Classes.Set("compact", compact);
            var shortWindow = ClientSize.Height < 780;
            Classes.Set("short", shortWindow);
            this.FindControl<Grid>("WorkspaceGrid")!.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(compact ? 228 : 252);
        };
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            closed = true;
            var cancellation = shutdown.CancelAsync();
            operation?.Dispose();
            scanner?.Dispose();
            model.Dispose();
            try { await cancellation; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { shutdown.Dispose(); }
        };
    }

    private Scanner Engine => scanner ??= new Scanner();

    private void OnOpened(object? sender, EventArgs args)
    {
        opened = true;
        if (!discover) return;
        try
        {
            if (Program.SessionArgument is not null) model.SetSession(Session.Open(Program.SessionArgument));
        }
        catch (Exception error) { model.Error = error.Message; }
        _ = CheckSelectedScannerAsync();
        _ = RefreshScannersAsync();
        _ = RefreshOcrAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (!model.Busy) return;
        args.Cancel = true;
        operation?.Cancel();
        model.Status = "Stopping safely. Completed pages are saved; close again when the operation ends.";
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (model.Busy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        model.Busy = true;
        model.Error = "";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { model.Status = "Stopped. Completed pages are safely saved."; }
        catch (Exception error)
        {
            model.Error = error.Message;
            model.Status = "Couldn't finish. Your saved pages are safe.";
            Console.Error.WriteLine(error);
        }
        finally { operation = null; model.Busy = false; model.Progress = 0; model.ExportProgress = false; }
    }

    private async void ShowOcrLanguages(object? sender, RoutedEventArgs args) => await HandleUi(async () =>
    {
        if (!model.CanConfigure) return;
        this.FindControl<Button>("SettingsButton")!.Flyout!.Hide();
        var dialog = new OcrLanguagesWindow(model.OcrLanguages, model.OcrLanguage)
        {
            Width = Math.Min(620, ClientSize.Width - 40),
            Height = Math.Min(680, ClientSize.Height - 40)
        };
        var language = await dialog.ShowDialog<string?>(this);
        if (language is not null && model.OcrLanguages.Contains(language)) model.OcrLanguage = language;
    });

    private async void AddManualDevice(object? sender, RoutedEventArgs args) => await Run(async token =>
    {
        CancelCapabilityCheck();
        model.Status = "Validating scanner address and reading capabilities… No scan will be started.";
        var result = await Engine.Probe(model.ManualDriver.Value, model.ManualAddress, model.ManualName, token);
        token.ThrowIfCancellationRequested();
        changingDevice = true;
        try { model.AddManualDevice(result.Device, result.Caps); }
        finally { changingDevice = false; }
        capabilityPending = pendingRemember = false;
        model.Status = "Manual scanner saved. It remains available after restart; review its settings before scanning.";
    });
    private async void RemoveManualDevice(object? sender, RoutedEventArgs args) => await Run(async token =>
    {
        CancelCapabilityCheck();
        changingDevice = true;
        try { model.RemoveManualDevice(); }
        finally { changingDevice = false; }
        await model.RememberScanners(token);
        capabilityPending = model.SelectedDevice is not null;
        model.Status = "Manual scanner forgotten. Refresh to discover available devices.";
    });
    private async void DeviceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (opened && discover && !changingDevice)
            await CheckSelectedScannerAsync(remember: true);
    }

    private async void ApplyPreset(object? sender, RoutedEventArgs args)
    {
        if (!model.CanApplyPreset) return;
        CancelCapabilityCheck();
        changingDevice = true;
        try { model.PreparePreset(); }
        finally { changingDevice = false; }
        await CheckSelectedScannerAsync(remember: true);
        if (model.SelectedDevice is null) model.Status = "Preset scanner unavailable. Refresh when connected, or explicitly choose a different scanner.";
    }

    private void EditPreset(Action edit)
    {
        try { edit(); model.Error = ""; }
        catch (Exception error) { model.Error = error.Message; }
    }
    private void SavePreset(object? sender, RoutedEventArgs args) => EditPreset(model.SavePreset);
    private void ManagePresets(object? sender, RoutedEventArgs args)
    {
        OpenSettings("PresetEditor");
        var name = this.FindControl<TextBox>("PresetNameBox")!;
        UpdateLayout();
        name.BringIntoView();
        name.Focus();
        name.SelectAll();
    }
    private void RenamePreset(object? sender, RoutedEventArgs args) => EditPreset(model.RenamePreset);
    private void UpdatePreset(object? sender, RoutedEventArgs args) => EditPreset(model.UpdatePreset);
    private void DeletePreset(object? sender, RoutedEventArgs args) => EditPreset(model.DeletePreset);

    private async void ImportNaps2Profiles(object? sender, RoutedEventArgs args) => await HandleUi(async () =>
    {
        if (!model.CanConfigure) return;
        this.FindControl<Button>("SettingsButton")!.Flyout!.Hide();
        var count = await new Naps2ProfileImportWindow(model).ShowDialog<int>(this);
        if (count > 0) model.Status = $"Imported {count} NAPS2 profiles as saved presets. Select a preset and apply it when ready.";
    });

    private async void NewSession(object? sender, RoutedEventArgs args) => await HandleUi(NewSessionAsync);

    private async Task NewSessionAsync()
    {
        if (!model.CanEdit) return;
        var name = await Prompt("New scan session", "Session name", "Documents");
        if (name is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to keep the new session folder",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        await Run(_ =>
        {
            var parent = folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder.");
            var slug = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
            if (slug.Length == 0) throw new ArgumentException("Use at least one letter or number in the session name.");
            var path = Path.Combine(parent, $"{DateTime.Now:yyyy-MM-dd-HHmmss}-{slug}");
            model.SetSession(Session.Create(path, name));
            return Task.CompletedTask;
        });
    }

    private async void OpenSession(object? sender, RoutedEventArgs args) => await HandleUi(OpenSessionAsync);

    private async Task OpenSessionAsync()
    {
        if (!model.CanEdit) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open an esyuscan session", AllowMultiple = false });
        if (folders.Count == 0) return;
        await Run(_ =>
        {
            model.SetSession(Session.Open(folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder.")));
            return Task.CompletedTask;
        });
    }

    private async void ImportFiles(object? sender, RoutedEventArgs args) => await HandleUi(ImportFilesAsync);

    private async Task ImportFilesAsync()
    {
        if (!model.CanEdit) return;
        if (!model.HasSession) { model.Error = "Create or open a session before importing PDFs or images."; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PDFs or images",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PDFs and images") { Patterns = ["*.pdf", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp"] }, FilePickerFileTypes.Pdf]
        });
        if (files.Count == 0) return;
        await Run(async token =>
        {
            var paths = files.Select(file => file.TryGetLocalPath() ?? throw new IOException("Choose local PDF or image files.")).ToArray();
            model.Status = "Importing PDFs and images… Completed pages are saved as they arrive.";
            await Task.Run(() => Engine.Import(model.Session!, paths, RefreshPages, token), token);
            model.SyncPages();
            model.Status = $"Import complete. {model.PageCount} saved in this session.";
        });
    }

    private async void ScanPages(object? sender, RoutedEventArgs args)
    {
        if (!model.CanScan) return;
        await Run(async token =>
        {
            if (model.SelectedColor is null || model.Dpi == 0) throw new InvalidOperationException("The scanner did not provide supported color or resolution settings.");
            model.RememberCurrentSettings();
            model.Status = "Scanning… Completed pages are saved as they arrive.";
            await Engine.Scan(model.Session!, model.CurrentScanSettings(), RefreshPages,
                progress => Dispatcher.UIThread.Post(() => model.Progress = progress), token);
            model.Status = $"Scan complete. {model.PageCount} saved. Scan again or choose an export below.";
        });
    }

    private async void OpenBatch(object? sender, RoutedEventArgs args) => await HandleUi(async () =>
    {
        if (!model.CanOpenBatch) return;
        var settings = model.HasValidScanSettings ? model.CurrentScanSettings() : null;
        PdfOcrOptions? ocr = model.SearchablePdf ? new(model.OcrLanguage ?? "") : null;
        var window = new BatchWindow(model.Session!, Engine, settings is null ? null
            : (pageAdded, token) => Engine.Scan(model.Session!, settings, pageAdded, _ => { }, token),
            () => model.SyncPages(), ocr, settings is null ? "No valid scanner selected. Review and export remain available."
                : $"{settings.Device.Name} · {settings.Source} · {settings.Color} · {settings.Dpi} DPI · {settings.Size} · auto-deskew {(settings.AutoDeskew ? "on" : "off")}");
        // The modal child owns cancellation; also prevent owner-close from disposing its session mid-job.
        model.Busy = true;
        void CancelBatch(object? source, WindowClosingEventArgs closing) => window.Close();
        Closing += CancelBatch;
        try { await window.ShowDialog(this); }
        finally
        {
            Closing -= CancelBatch;
            model.Busy = false;
            model.SyncPages();
        }
    });

    private void BeginDuplex(object? sender, RoutedEventArgs args)
    {
        if (!model.CanStartDuplex) return;
        try { model.BeginDuplex(); }
        catch (Exception error) { model.Error = error.Message; }
    }

    private async void ScanFronts(object? sender, RoutedEventArgs args)
    {
        if (model.CanScanFronts) await ScanDuplexPass(backs: false);
    }

    private async void ScanBacks(object? sender, RoutedEventArgs args)
    {
        if (model.CanScanBacks) await ScanDuplexPass(backs: true);
    }

    private async Task ScanDuplexPass(bool backs)
    {
        await Run(async token =>
        {
            if (model.SelectedColor is null || model.Dpi == 0) throw new InvalidOperationException("Select a supported color and resolution.");
            model.Status = backs ? "Scanning backs. Completed sides are saved immediately." : "Scanning fronts. Completed sides are saved immediately.";
            try
            {
                await model.Session!.ScanDuplexPass(backs, scanToken => Engine.Scan(model.Session,
                    model.CurrentScanSettings(),
                    RefreshPages, progress => Dispatcher.UIThread.Post(() => model.Progress = progress), scanToken), token);
                model.Status = backs ? "Review every pair and orientation, then apply." : "Fronts saved. Reload the stack following the instructions, then scan backs.";
            }
            finally { model.RefreshDuplex(); }
        });
    }

    private void ApplyDuplex(object? sender, RoutedEventArgs args)
    {
        try { model.ApplyDuplex(); }
        catch (Exception error) { model.Error = error.Message; }
    }

    private void KeepDuplexScanOrder(object? sender, RoutedEventArgs args)
    {
        if (model.Busy) return;
        try { model.KeepDuplexScanOrder(); }
        catch (Exception error) { model.Error = error.Message; }
    }

    private void RefreshPages() => Dispatcher.UIThread.Post(() => model.SyncPages());
    private void CancelOperation(object? sender, RoutedEventArgs args) => operation?.Cancel();

    private void Edit(Action<Session, int> action)
    {
        if (!model.CanChangePage) return;
        try
        {
            var index = model.Pages.IndexOf(model.SelectedPage!);
            action(model.Session!, index);
            model.SyncPages();
            model.Error = "";
            model.Status = "Changes saved. Undo is available; originals are preserved.";
        }
        catch (Exception error) { model.Error = error.Message; }
    }

    private void RotatePage(object? sender, RoutedEventArgs args) => EditSelection(model.RotateSelection);
    private async void DeskewPage(object? sender, RoutedEventArgs args)
    {
        if (!model.CanChangePage) return;
        var index = model.Pages.IndexOf(model.SelectedPage!);
        await Run(async token =>
        {
            model.Status = "Finding the page angle…";
            var angle = await Task.Run(() => PageRenderer.DetectDeskew(model.Session!, model.Session!.Pages[index], token), token);
            token.ThrowIfCancellationRequested();
            model.Session!.SetDeskew(index, angle);
            model.SyncPages();
            model.Status = $"Deskew saved ({angle:0.0}°). Undo restores the previous edit.";
        });
    }

    private async void CropPage(object? sender, RoutedEventArgs args) => await HandleUi(async () =>
    {
        if (!model.CanChangePage) return;
        var index = model.Pages.IndexOf(model.SelectedPage!);
        var editor = new CropWindow(model.Session!, model.Session!.Pages[index]);
        if (await editor.ShowDialog<bool>(this))
            Edit((session, pageIndex) => session.SetCrop(pageIndex, editor.Result));
    });

    private async void AdjustPage(object? sender, RoutedEventArgs args) => await HandleUi(async () =>
    {
        if (!model.CanChangePage) return;
        var index = model.Pages.IndexOf(model.SelectedPage!);
        var editor = new AdjustmentsWindow(model.Session!, model.Session!.Pages[index]);
        if (await editor.ShowDialog<bool>(this))
            Edit((session, pageIndex) => session.SetEdits(pageIndex, editor.Result));
    });

    private void EditSelection(Action action)
    {
        try { action(); model.Error = ""; }
        catch (Exception error) { model.Error = error.Message; }
    }
    private void RemovePage(object? sender, RoutedEventArgs args) => EditSelection(model.RemoveSelection);
    private void MoveEarlier(object? sender, RoutedEventArgs args) => EditSelection(() => model.MoveSelection(-1));
    private void MoveLater(object? sender, RoutedEventArgs args) => EditSelection(() => model.MoveSelection(1));
    private void Undo(object? sender, RoutedEventArgs args) => EditSelection(model.UndoEdit);
    private void Redo(object? sender, RoutedEventArgs args) => EditSelection(model.RedoEdit);

    private async void ExportImages(object? sender, RoutedEventArgs args) => await HandleUi(() => Export(ExportFormat.Images));
    private async void ExportPdfs(object? sender, RoutedEventArgs args) => await HandleUi(() => Export(ExportFormat.SeparatePdfs));
    private async void ExportDocument(object? sender, RoutedEventArgs args) => await HandleUi(() => Export(ExportFormat.CombinedPdf));

    private async Task HandleUi(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            model.Error = error.Message;
            model.Status = "Couldn't open the dialog. Your session is unchanged.";
            Console.Error.WriteLine(error);
        }
    }

    private async Task Export(ExportFormat format)
    {
        if (!model.CanExport) return;
        var selected = model.ExportSelection;
        var dialog = new ExportWindow(format, model.Session!.Pages.Any(page => page.PreservePdf && (selected is null || selected.Contains(page.Identity))),
            model.SearchablePdf, model.OcrLanguages, model.OcrLanguage)
        {
            Width = Math.Min(560, ClientSize.Width - 40),
            Height = Math.Min(670, ClientSize.Height - 40)
        };
        if (!await dialog.ShowDialog<bool>(this)) return;
        var selection = dialog.TakeResult()!;
        format = selection.Format;
        var ocr = selection.Ocr;
        string? destination;
        if (format is ExportFormat.CombinedPdf or ExportFormat.CombinedTiff)
        {
            var isTiff = format == ExportFormat.CombinedTiff;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save one combined document",
                SuggestedFileName = isTiff ? "document.tiff" : "document.pdf",
                DefaultExtension = isTiff ? "tiff" : "pdf",
                FileTypeChoices = isTiff ? [new FilePickerFileType("Multipage TIFF") { Patterns = ["*.tiff", "*.tif"] }] : [FilePickerFileTypes.Pdf]
            });
            destination = file?.TryGetLocalPath();
        }
        else
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a parent folder for the new export folder",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var name = await Prompt("Export pages", "New folder name", format == ExportFormat.Images ? "images" : "pdfs");
            if (name is null) return;
            if (name != Path.GetFileName(name) || name is "." or "..") { model.Error = "Enter a folder name, not a path."; return; }
            destination = Path.Combine(folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder."), name);
        }
        if (destination is null) return;
        await Run(async token =>
        {
            model.ExportProgress = true;
            model.Status = "Exporting your document…";
            await Task.Run(() => Engine.Export(model.Session!, destination, format, token, ocr,
                progress => Dispatcher.UIThread.Post(() =>
                {
                    model.Progress = progress.Fraction;
                    model.Status = progress.Message;
                }), selection.Options, selected), token);
            model.Status = $"Export saved: {destination}";
        });
    }

    private async Task<string?> Prompt(string title, string label, string initial)
    {
        var text = new TextBox { Text = initial, MinWidth = 340 };
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var accept = new Button { Content = "Continue · Enter", Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel · Esc", IsCancel = true };
        accept.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(text.Text)) dialog.Close(text.Text.Trim()); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28),
            Spacing = 18,
            Children = { new TextBlock { Text = label }, text, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { accept, cancel } } }
        };
        dialog.Opened += (_, _) => { text.Focus(); text.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    private void PrinterSettings(object? sender, RoutedEventArgs args)
    {
        this.FindControl<Button>("SettingsButton")!.Flyout!.Hide();
        try { Process.Start(new ProcessStartInfo("system-config-printer") { UseShellExecute = false }); }
        catch (Exception error) { model.Error = error.Message; }
    }

}
