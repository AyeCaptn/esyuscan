using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using NAPS2.Images;
using NAPS2.Scan;

namespace Scan;

public record ScanChoice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed class PagePreview : IDisposable
{
    public PagePreview(Session session, SessionPage page, int number, string? caption = null)
    {
        Number = number;
        Caption = caption ?? $"Page {number}";
        Page = page;
        using var stream = PageRenderer.Png(session, page);
        IsPdf = page.PdfSource is not null;
        Thumbnail = Bitmap.DecodeToWidth(stream, 240);
    }

    public bool IsPdf { get; }
    public int Number { get; }
    public string Caption { get; }
    public SessionPage Page { get; }
    public Bitmap Thumbnail { get; }
    public void Dispose() => Thumbnail.Dispose();
}

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private string status = "Create a session to start scanning.";
    private string error = "";
    private bool busy;
    private double progress;
    private bool exportProgress;
    private bool searchablePdf;
    private string? ocrLanguage;
    private string ocrStatus = "OCR is optional. Refresh languages to check installed components.";
    private PagePreview? selectedPage;
    public PageSelection Selection { get; } = new();
    public PreviewZoom Zoom { get; } = new();
    private readonly ConditionalWeakTable<object, PageSelectionSnapshot> navigationHistory = new();
    private object? displayedHistory;
    private bool syncingPages;
    private bool exportSelected;
    public bool ExportSelected { get => exportSelected; set { Set(ref exportSelected, value); NotifyState(); } }
    public string SelectionCount => $"{Selection.Selected.Count} of {Pages.Count} selected";
    public IReadOnlyCollection<string>? ExportSelection => ExportSelected ? Selection.Selected.ToArray() : null;
    public bool CanEditSelection => CanEdit && Selection.Selected.Count > 0;
    public void SelectPage(int index, bool toggle = false, bool range = false, bool focusOnly = false)
    {
        Selection.Select(index, toggle, range, focusOnly);
        RefreshSelection();
    }
    public void SelectAllPages() { Selection.SelectAll(); RefreshSelection(); }
    private void RefreshSelection()
    {
        SelectedPage = Pages.FirstOrDefault(page => page.Page.Identity == Selection.Focus);
        Notify(nameof(SelectionCount));
        Notify(nameof(Selection));
        NotifyState();
    }
    public void RotateSelection() { if (CanEditSelection) { Session!.RotatePages(Selection.Selected); SyncPages(); } }
    public void RemoveSelection() { if (CanEditSelection) { Session!.RemovePages(Selection.Selected); SyncPages(); } }
    public void ReorderSelection(int boundary) { if (CanEditSelection) { Session!.MovePages(Selection.Selected, boundary); SyncPages(); } }
    public void MoveSelection(int direction)
    {
        if (!CanEditSelection) return;
        var indices = Pages.Select((page, index) => (page, index)).Where(pair => Selection.Selected.Contains(pair.page.Page.Identity)).Select(pair => pair.index).ToArray();
        var boundary = direction < 0 ? indices.Min() - 1 : indices.Max() + 2;
        if (boundary >= 0 && boundary <= Pages.Count) ReorderSelection(boundary);
    }
    public void UndoEdit() { if (CanUndo) { Session!.Undo(); SyncPages(); } }
    public void RedoEdit() { if (CanRedo) { Session!.Redo(); SyncPages(); } }
    private Bitmap? preview;
    private bool duplexReviewed;
    private bool scanSettingsExpanded = true;
    private string themeStatus = "Following Omarchy";

    public ObservableCollection<string> OcrLanguages { get; } = [];
    public bool SearchablePdf { get => searchablePdf; set { Set(ref searchablePdf, value); Notify(nameof(CanChooseOcrLanguage)); } }
    public string? OcrLanguage { get => ocrLanguage; set => Set(ref ocrLanguage, value); }
    public string OcrStatus { get => ocrStatus; private set => Set(ref ocrStatus, value); }
    public bool CanChooseOcrLanguage => !Busy && SearchablePdf;
    public bool ExportProgress { get => exportProgress; set { Set(ref exportProgress, value); Notify(nameof(IndeterminateProgress)); } }
    public bool IndeterminateProgress => Busy && !ExportProgress;

    public void SetOcrAvailability(OcrAvailability available)
    {
        var previous = OcrLanguage;
        OcrLanguages.Clear();
        foreach (var language in available.Languages.OrderBy(OcrLanguageCatalog.Name, StringComparer.CurrentCultureIgnoreCase)) OcrLanguages.Add(language);
        OcrLanguage = previous is not null && OcrLanguages.Contains(previous) ? previous
            : OcrLanguages.Contains("eng") ? "eng" : OcrLanguages.FirstOrDefault();
        OcrStatus = available.Message;
    }

    public Session? Session { get; private set; }
    public ObservableCollection<PagePreview> Pages { get; } = [];
    public ObservableCollection<ScanDevice> Devices { get; } = [];
    public ObservableCollection<ScanChoice<PaperSource>> Sources { get; } = [];
    public ObservableCollection<ScanChoice<BitDepth>> Colors { get; } = [];
    public ObservableCollection<int> Resolutions { get; } = [];
    public bool AutoDeskew { get; set; }
    public string SessionName => Session?.Name ?? "A fresh start.";
    public string SessionPath => Session?.DirectoryPath ?? "Every scan session gets its own folder.";
    public string PageCount => $"{Pages.Count} {(Pages.Count == 1 ? "page" : "pages")}";
    public string PreviewCaption => SelectedPage is null ? "Your documents, together." : $"{SelectedPage.Caption} · {SelectedPage.Number} of {Pages.Count}" + (SelectedPage?.IsPdf == true ? (SelectedPage.Page.PreservePdf ? " · PDF source preserved" : " · PDF raster edit; source retained") : "");
    public bool HasPages => Pages.Count > 0;
    public bool IsEmpty => !HasPages;
    public bool HasSession => Session is not null;
    public bool CanEdit => !Busy && !HasDuplex && !HasBatch;
    public bool CanConfigure => !Busy;
    public bool CanScan => CanEdit && HasSession && HasValidScanSettings && !CheckingScanner;
    public bool CanExport => !Busy && !HasDuplex && HasPages && (!ExportSelected || Selection.Selected.Count > 0) && (Session?.Batch is null || Session.Batch is { Phase: BatchPhase.Review, Reviewed: true });
    public bool HasBatch => Session?.Batch is not null;
    public bool CanOpenBatch => !Busy && HasSession && !HasDuplex;
    public bool CanChangePage => CanEdit && SelectedPage is not null;
    public bool HasError => Error.Length > 0;
    public bool CanUndo => CanEdit && Session?.CanUndo == true;
    public bool CanRedo => CanEdit && Session?.CanRedo == true;
    public bool ScanSettingsExpanded { get => scanSettingsExpanded; set => Set(ref scanSettingsExpanded, value); }
    public bool HasDuplex => Session?.ManualDuplex is not null;
    public bool CanStartDuplex => CanScan && SelectedSource?.Value == PaperSource.Feeder;
    public bool IsDuplexReview => Session?.ManualDuplex?.Phase is DuplexPhase.Backs or DuplexPhase.Review;
    public bool CanScanDuplex => !Busy && !CheckingScanner && HasDuplex && HasValidScanSettings && SelectedSource?.Value == PaperSource.Feeder;
    public bool ShowScanFronts => !IsDuplexReview;
    public bool ShowScanBacks => !IsDuplexReview || Session?.ManualDuplex is { } state && state.Backs.Length < state.Fronts.Length;
    public bool CanScanFronts => CanScanDuplex && Session!.ManualDuplex!.Backs.Length == 0;
    public bool CanScanBacks => CanScanDuplex && Session!.ManualDuplex!.Fronts.Length > 0;
    public bool CanApplyDuplex => !Busy && DuplexReviewed && Session?.CanApplyDuplex == true;
    public bool DuplexReviewed { get => duplexReviewed; set { Set(ref duplexReviewed, value); Notify(nameof(CanApplyDuplex)); } }
    public bool ReverseBacks
    {
        get => Session?.ManualDuplex?.ReverseBacks ?? false;
        set => UpdateDuplexOptions(value, BackRotation);
    }
    public int BackRotation
    {
        get => Session?.ManualDuplex?.BackRotation ?? 0;
        set => UpdateDuplexOptions(ReverseBacks, value);
    }
    public int[] BackRotations { get; } = [0, 90, 180, 270];
    private bool ReadyToReload => Session?.ManualDuplex is { Phase: DuplexPhase.Reload, Fronts.Length: > 0 };
    public string DuplexTitle => IsDuplexReview ? "3 · Review both sides" :
        ReadyToReload ? "2 · Reload the stack for backs" : "1 · Scan the fronts";
    public string DuplexInstructions => IsDuplexReview
        ? "Preview only. Select each Front / Back to check pairing and orientation. Reverse backs if the last sheet came through first. Existing pages stay first."
        : ReadyToReload
            ? "If interrupted, scan remaining fronts first. Keep sheets together; reload unscanned sides toward the feeder’s scan side using its loading marks. Scan backs when ready; review stack order afterward."
            : "Select Document feeder. Load fronts in reading order, facing the feeder’s scan side (see its loading marks). Start with two numbered sheets to check reload direction and orientation.";
    public string DuplexCounts => Session?.ManualDuplex is not { } state ? "" :
        $"{state.Fronts.Length} fronts · {state.Backs.Length} backs. " +
        (state.Fronts.Length == state.Backs.Length && state.Fronts.Length > 0
            ? "Counts match; verify the actual sheet pairs, including blanks."
            : "Apply blocked until counts match. Scan remaining sides, or keep scan order for manual correction. Never rescan completed sides.");

    public void BeginDuplex()
    {
        Session!.BeginManualDuplex();
        ScanSettingsExpanded = false;
        RefreshDuplex();
    }

    public void ApplyDuplex()
    {
        if (!CanApplyDuplex) return;
        Session!.ApplyManualDuplex();
        ScanSettingsExpanded = true;
        RefreshDuplex();
        Status = "Double-sided order and back rotations saved. Undo restores the complete scan order in one step.";
    }

    public void KeepDuplexScanOrder()
    {
        Session!.KeepDuplexScanOrder();
        ScanSettingsExpanded = true;
        RefreshDuplex();
        Status = "Every completed page kept in scan order: fronts, then backs. You can edit or undo normally.";
    }

    public void RefreshDuplex()
    {
        DuplexReviewed = false;
        SyncPages();
        foreach (var name in new[] { nameof(ReverseBacks), nameof(BackRotation), nameof(DuplexTitle), nameof(DuplexInstructions), nameof(DuplexCounts) }) Notify(name);
        NotifyState();
    }

    private void UpdateDuplexOptions(bool reverse, int rotation)
    {
        if (!HasDuplex || Busy || (ReverseBacks == reverse && BackRotation == rotation)) return;
        DuplexReviewed = false;
        try
        {
            Session!.SetDuplexOptions(reverse, rotation);
            RefreshDuplex();
        }
        catch (Exception error)
        {
            Error = error.Message;
            Notify(nameof(ReverseBacks));
            Notify(nameof(BackRotation));
        }
    }

    public string Status { get => status; set => Set(ref status, value); }
    public string ThemeStatus { get => themeStatus; set => Set(ref themeStatus, value); }
    public string Error { get => error; set { Set(ref error, value); Notify(nameof(HasError)); } }
    public bool Busy { get => busy; set { Set(ref busy, value); NotifyState(); } }
    public double Progress { get => progress; set => Set(ref progress, value); }

    public PagePreview? SelectedPage
    {
        get => selectedPage;
        set
        {
            if (!Set(ref selectedPage, value)) return;
            if (!syncingPages && value is not null && value.Page.Identity != Selection.Focus)
                Selection.Select(Pages.IndexOf(value));
            preview?.Dispose();
            preview = null;
            if (value is not null)
            {
                using var stream = PageRenderer.Png(Session!, value.Page);
                preview = new Bitmap(stream);
            }
            Zoom.SetPage(preview?.Size.Width ?? 0, preview?.Size.Height ?? 0);
            Notify(nameof(Selection));
            Notify(nameof(SelectionCount));
            Notify(nameof(Preview));
            Notify(nameof(PreviewCaption));
            NotifyState();
        }
    }
    public Bitmap? Preview => preview;

    public void SetSession(Session next)
    {
        Session?.Dispose();
        navigationHistory.Clear();
        displayedHistory = null;
        Selection.Reconcile([]);
        Session = next;
        ScanSettingsExpanded = !HasDuplex;
        RefreshDuplex();
        Notify(nameof(SessionName));
        Notify(nameof(SessionPath));
        NotifyState();
        Status = HasBatch ? "Recovered batch. Open Batch setup / review to explicitly resume or review; physical scans never restart automatically." : HasDuplex ? "Recovered an unfinished double-sided scan. Review or resume below; all completed pages are saved." : "Session saved automatically. Originals are never overwritten.";
    }

    public void SyncPages(int? selectIndex = null)
    {
        if (displayedHistory is not null) navigationHistory.AddOrUpdate(displayedHistory, Selection.Snapshot());
        var nextHistory = Session?.HistoryState;
        var restore = nextHistory is not null && navigationHistory.TryGetValue(nextHistory, out var saved) ? saved : null;
        syncingPages = true;
        SelectedPage = null;
        foreach (var page in Pages) page.Dispose();
        Pages.Clear();
        if (Session is not null)
        {
            var display = IsDuplexReview ? Session.PreviewDuplex() : Session.Pages;
            var state = Session.ManualDuplex;
            var backs = state?.ReverseBacks == true ? state.Backs.Reverse().ToArray() : state?.Backs;
            for (var i = 0; i < display.Count; i++)
            {
                var front = state is null ? -1 : Array.IndexOf(state.Fronts, display[i].FileName);
                var back = backs is null ? -1 : Array.IndexOf(backs, display[i].FileName);
                var caption = front >= 0 ? $"Front {front + 1}" : back >= 0 ? $"Back {back + 1}" : $"Page {i + 1}";
                Pages.Add(new PagePreview(Session, display[i], i + 1, caption));
            }
        }
        Selection.Reconcile(Pages.Select(page => page.Page.Identity), restore);
        // Explicit index is reserved for callers requesting navigation, not identity recovery.
        if (selectIndex is not null && Pages.Count > 0) Selection.Select(Math.Clamp(selectIndex.Value, 0, Pages.Count - 1));
        displayedHistory = nextHistory;
        RefreshSelection();
        syncingPages = false;
        Notify(nameof(PageCount));
        Notify(nameof(HasPages));
        Notify(nameof(IsEmpty));
        NotifyState();
    }

    private void NotifyState()
    {
        Notify(nameof(CanRefreshScanners));
        Notify(nameof(CanRefreshOcr));
        foreach (var name in new[] { nameof(CanCancelPrint), nameof(HasBatch), nameof(CanOpenBatch), nameof(CanEditSelection), nameof(CanRemoveManualDevice), nameof(CanEditCustomSize), nameof(CanChooseOcrLanguage), nameof(IndeterminateProgress), nameof(CanUpdatePreset), nameof(CanSavePreset), nameof(CanManagePreset), nameof(CanApplyPreset), nameof(CanChangeScanSettings), nameof(CanEdit), nameof(CanConfigure), nameof(ShowScanFronts), nameof(ShowScanBacks), nameof(HasDuplex), nameof(IsDuplexReview), nameof(CanStartDuplex), nameof(CanScanDuplex), nameof(CanScanFronts), nameof(CanScanBacks), nameof(CanApplyDuplex), nameof(CanScan), nameof(CanExport), nameof(CanChangePage), nameof(HasSession), nameof(CanUndo), nameof(CanRedo) })
            Notify(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    public void Dispose()
    {
        preview?.Dispose();
        foreach (var page in Pages) page.Dispose();
        Session?.Dispose();
    }
}
