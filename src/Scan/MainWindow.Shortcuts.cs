using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Scan;

public sealed partial class MainWindow
{
    private sealed record Shortcut(string Keys, string Label, string Group, Action Execute, Control? Target = null,
        bool Hint = false, string Note = "")
    {
        // Numeric enum parsing would interpret "1" as Key.Cancel, not the number-row key.
        public KeyGesture Gesture { get; } = KeyGesture.Parse(
            Keys.Length > 1 && Keys[^2] == '+' && char.IsAsciiDigit(Keys[^1]) ? Keys[..^1] + "D" + Keys[^1] : Keys);
        public string DisplayKeys => Keys switch { "Escape" => "Esc", "Shift+OemQuestion" => "?", _ => Keys };
    }

    private readonly List<Shortcut> shortcuts = [];

    private void InitializeShortcuts()
    {
        void Add(string keys, string label, string group, Action action, Control? target = null, bool hint = false) =>
            shortcuts.Add(new(keys, label, group, action, target, hint, target is null ? "" : ToolTip.GetTip(target) as string ?? ""));

        void Button(string keys, string name, string label, string group, bool hint = false)
        {
            var button = this.FindControl<Button>(name)!;
            Add(keys, label, group, () =>
            {
                if (button.Flyout is { } flyout)
                {
                    if (flyout.IsOpen) flyout.Hide();
                    else flyout.ShowAt(button);
                }
                else button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            }, button, hint);
        }

        void Focus(string keys, string name, string label, string? section = null)
        {
            var control = this.FindControl<Control>(name)!;
            Add(keys, label, "Scan setup", () =>
            {
                if (section is not null) OpenSettings(section);
                else if (name != "OcrLanguageSelector") model.ScanSettingsExpanded = true;
                UpdateLayout();
                control.BringIntoView();
                control.Focus();
                if (control is ComboBox picker) picker.IsDropDownOpen = true;
            }, control);
        }

        void Toggle(string keys, string name, string label, bool inSettings = false)
        {
            var check = this.FindControl<CheckBox>(name)!;
            Add(keys, label, "Output and processing", () =>
            {
                if (inSettings) OpenSettings();
                if (check.IsEffectivelyEnabled) check.IsChecked = check.IsChecked != true;
            }, check);
        }

        Button("Ctrl+N", "NewSessionButton", "New session", "Session", true);
        Button("Ctrl+O", "OpenSessionButton", "Open session", "Session", true);
        Button("Ctrl+I", "ImportFilesButton", "Import PDFs / images", "Session", true);
        Add("Ctrl+Q", "Quit (saved pages stay on disk)", "Session", Close);
        Add("Q", "Quit (outside text fields)", "Session", Close);

        Button("Space", "ScanButton", "Scan pages", "Scanning", true);
        Button("F6", "ScanButton", "Scan pages from any control", "Scanning");
        Button("F5", "RefreshDevicesButton", "Refresh scanners", "Scanning");
        Button("Ctrl+D", "ManualDuplexButton", "Guided double-sided scan", "Scanning", true);
        Button("Ctrl+B", "BatchButton", "Batch setup / review", "Scanning", true);
        Button("Alt+F", "ScanFrontsButton", "Scan fronts in the duplex guide", "Scanning", true);
        Button("Alt+B", "ScanBacksButton", "Scan backs in the duplex guide", "Scanning", true);
        Button("Ctrl+Enter", "ApplyDuplexButton", "Apply confirmed duplex order", "Scanning", true);
        Button("Escape", "CancelButton", "Cancel current operation", "Scanning", true);

        Focus("Alt+1", "DevicePicker", "Choose scanner");
        Focus("Alt+2", "PresetPicker", "Choose preset");
        Focus("Alt+3", "SourcePicker", "Choose paper source");
        Focus("Alt+4", "ColorPicker", "Choose color mode");
        Focus("Alt+5", "DpiPicker", "Choose resolution");
        Focus("Alt+6", "SizePicker", "Choose page size");
        Button("Alt+Enter", "ApplyPresetButton", "Apply selected preset", "Scan setup");
        Button("Ctrl+Alt+P", "ManagePresetsButton", "Save / manage presets", "Scan setup", true);

        Button("Ctrl+Z", "UndoButton", "Undo page edit", "Pages");
        Button("Ctrl+Shift+Z", "RedoButton", "Redo page edit", "Pages");
        Button("R", "RotateButton", "Rotate selected pages", "Pages", true);
        Button("C", "CropButton", "Crop page", "Pages", true);
        Button("A", "AdjustButton", "Adjust page", "Pages", true);
        Button("K", "DeskewButton", "Deskew page", "Pages", true);
        Button("Delete", "RemoveButton", "Remove selected pages (undoable)", "Pages", true);
        Button("Alt+Left", "MoveEarlierButton", "Move selected pages earlier", "Pages");
        Button("Alt+Right", "MoveLaterButton", "Move selected pages later", "Pages");
        Add("Ctrl+A", "Select all pages", "Pages", model.SelectAllPages);
        Button("Ctrl++", "ZoomInButton", "Zoom in", "Preview");
        Button("Ctrl+Shift++", "ZoomInButton", "Zoom in (+ on keyboards that need Shift)", "Preview");
        Button("Ctrl+-", "ZoomOutButton", "Zoom out", "Preview");
        Button("Ctrl+0", "FitPageButton", "Fit page", "Preview", true);
        Button("Ctrl+NumPad0", "FitPageButton", "Fit page (number pad)", "Preview");

        Button("Ctrl+S", "CombinedExportButton", "Export one document", "Output and processing", true);
        Button("Ctrl+Shift+S", "SeparatePdfsButton", "Export separate PDFs", "Output and processing", true);
        Button("Ctrl+Shift+I", "ImagesExportButton", "Export separate images", "Output and processing", true);
        Button("Ctrl+P", "PrintButton", "Print document", "Output and processing", true);
        Button("Ctrl+M", "EmailButton", "Prepare email PDF", "Output and processing", true);
        Button("Ctrl+Shift+P", "CancelPrintButton", "Cancel last print job", "Output and processing");
        Toggle("Alt+S", "ExportSelectedCheckBox", "Toggle selected pages only");
        Toggle("Alt+O", "SearchablePdfCheckBox", "Toggle searchable PDF (OCR)");
        Toggle("Alt+D", "AutoDeskewCheckBox", "Toggle automatic scan deskew", true);
        Focus("Alt+L", "OcrLanguageSelector", "Choose OCR language");
        Button("Ctrl+Alt+L", "OcrLanguagesButton", "OCR language models and setup", "Output and processing");
        Button("Shift+F5", "RefreshOcrButton", "Refresh installed OCR languages", "Output and processing");
        Button("Ctrl+,", "SettingsButton", "Open / close Settings", "Help and settings", true);
        Button("F1", "HelpButton", "Keyboard shortcut reference", "Help and settings", true);
        Button("Shift+OemQuestion", "HelpButton", "Keyboard shortcut reference (?)", "Help and settings");

        foreach (var shortcut in shortcuts.Where(shortcut => shortcut.Hint))
        {
            var button = (Button)shortcut.Target!;
            AutomationProperties.SetName(button, shortcut.Label);
            var label = button.Content;
            button.Content = null;
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new ContentControl { Content = label, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = shortcut.DisplayKeys, Classes = { "shortcut-hint" } }
                }
            };
        }
        UpdateShortcutTooltips();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.OcrStatus) or nameof(MainViewModel.LastPrintJob)) UpdateShortcutTooltips();
        };
        // A flyout has its own input root; its content must handle app shortcuts too.
        this.FindControl<ScrollViewer>("SettingsMenu")!.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void UpdateShortcutTooltips()
    {
        foreach (var group in shortcuts.Where(shortcut => shortcut.Target is not null).GroupBy(shortcut => shortcut.Target!))
        {
            var first = group.First();
            var note = group.Key.Name switch
            {
                "OcrLanguageSelector" or "RefreshOcrButton" => model.OcrStatus,
                "CancelPrintButton" => model.LastPrintJob ?? "No print job submitted in this window.",
                _ => first.Note
            };
            ToolTip.SetTip(group.Key, $"{first.Label} · {string.Join(" / ", group.Select(shortcut => shortcut.DisplayKeys))}"
                + (note.Length == 0 ? "" : "\n" + note));
        }
    }

    private void OpenSettings(string? section = null)
    {
        var button = this.FindControl<Button>("SettingsButton")!;
        if (!button.Flyout!.IsOpen) button.Flyout.ShowAt(button);
        if (section is not null) this.FindControl<Expander>(section)!.IsExpanded = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled) return;
        var ancestors = (args.Source as Visual)?.GetSelfAndVisualAncestors().ToArray() ?? [];
        // Let an open picker consume Escape before cancelling an operation or closing Settings.
        if (args.Key == Key.Escape && ancestors.OfType<ComboBox>().Any(picker => picker.IsDropDownOpen)) return;
        if (args.Key == Key.Escape && dragStart is not null) { ClearDrag(); args.Handled = true; return; }
        if (args.Key == Key.Escape && this.FindControl<Button>("SettingsButton")!.Flyout!.IsOpen)
        {
            this.FindControl<Button>("SettingsButton")!.Flyout!.Hide();
            args.Handled = true;
            return;
        }
        var input = IsInputControl(args.Source);
        if (input && (args.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) && args.Key is not (Key.F1 or Key.F5 or Key.F6 or Key.Escape)) return;
        if (input && args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y) return;
        if (args.Key == Key.Space && args.KeyModifiers == KeyModifiers.None && ancestors.Any(control => control is Button or CheckBox)) return;
        if (!input && TryNavigationKey(args)) { args.Handled = true; return; }
        var shortcut = shortcuts.FirstOrDefault(shortcut => shortcut.Gesture.Matches(args));
        if (shortcut is null) return;
        if (model.Busy && shortcut.Target?.Name is not ("CancelButton" or "HelpButton" or "SettingsButton")) return;
        args.Handled = true;
        if (shortcut.Target?.IsEffectivelyEnabled == false) return;
        if (shortcut.Target is Button { Name: not ("SettingsButton" or "CancelButton") })
            this.FindControl<Button>("SettingsButton")!.Flyout!.Hide();
        shortcut.Execute();
    }

    private async void ShowHelp(object? sender, RoutedEventArgs args) => await HandleUi(async () =>
    {
        this.FindControl<Button>("SettingsButton")!.Flyout!.Hide();
        var dialog = new Window
        {
            Title = "esyuscan · Keyboard shortcuts",
            Width = Math.Min(620, ClientSize.Width - 40),
            Height = Math.Min(740, ClientSize.Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 20, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "Tab / Shift+Tab moves between controls. Space toggles checkboxes and activates focused buttons. Text fields keep their normal editing keys. Enter confirms dialogs; Esc cancels or closes them.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        });
        foreach (var group in shortcuts.GroupBy(shortcut => shortcut.Group))
        {
            body.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 2) });
            foreach (var shortcut in group)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), ColumnSpacing = 12 };
                row.Children.Add(new TextBlock { Text = shortcut.DisplayKeys, FontSize = 11 });
                var description = new TextBlock { Text = shortcut.Label, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                Grid.SetColumn(description, 1);
                row.Children.Add(description);
                body.Children.Add(row);
            }
        }
        body.Children.Add(new TextBlock
        {
            Text = "Arrows / Page Up / Page Down browse pages; Home / End goes to the first / last page. Shift extends selection; Ctrl+arrow moves focus only. Text fields and pickers keep their own keys. Ctrl+click toggles thumbnails. Pinch (where supported) or Ctrl+wheel zooms; drag to pan. Destructive actions remain disabled during scan, export, batch, or duplex review.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0)
        });
        var close = new Button { Content = "Back to documents · Esc", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16 };
        layout.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(close, 1);
        layout.Children.Add(close);
        dialog.Content = layout;
        await dialog.ShowDialog(this);
    });
}
