using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Scan;

public sealed class Naps2ProfileImportWindow : Window
{
    private readonly MainViewModel model;
    private readonly ComboBox locations = new() { Name = "Naps2Locations", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "No standard profile file found — browse below" };
    private readonly Button load = new() { Name = "LoadNaps2Profiles", Content = "Preview selected file" };
    private readonly Button browse = new() { Name = "BrowseNaps2Profiles", Content = "Choose profiles.xml…" };
    private readonly ComboBox duplicates = new() { Name = "Naps2DuplicatePolicy", ItemsSource = new[] { "Skip existing names", "Keep both (add a number)", "Replace matching names" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel list = new() { Name = "Naps2ProfileList", Spacing = 12 };
    private readonly TextBlock status = Text("Choose a file to preview. Import saves presets only; it does not apply settings.");
    private readonly TextBlock source = Text("");
    private readonly CheckBox reviewed = new() { Name = "Naps2ImportReviewed", Content = Text("I reviewed the mappings and options not applied.") };
    private readonly Button import = new() { Name = "ImportSelectedNaps2Profiles", Content = "Import selected", IsDefault = true, Classes = { "primary" } };
    private readonly List<(Naps2Profile Profile, CheckBox Selected, TextBlock Outcome)> rows = [];
    private bool reading;
    private bool closed;

    public Naps2ProfileImportWindow(MainViewModel model, IReadOnlyList<string>? discovered = null)
    {
        this.model = model;
        Title = "esyuscan · Import NAPS2 profiles";
        Width = 760;
        Height = 760;
        MinWidth = 520;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        locations.ItemsSource = discovered ?? Naps2Profiles.Discover();
        locations.SelectedIndex = locations.ItemCount > 0 ? 0 : -1;
        locations.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((path, _) => Text(path ?? ""));
        locations.SelectionChanged += (_, _) => UpdatePlan();
        load.Click += async (_, _) => { if (locations.SelectedItem is string path) await LoadFileAsync(path); };
        browse.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose NAPS2 profiles.xml",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("NAPS2 profiles (XML)") { Patterns = ["*.xml"] }, FilePickerFileTypes.All]
                });
                if (closed || files.Count == 0) return;
                await LoadFileAsync(files[0].TryGetLocalPath() ?? throw new IOException("Choose a local profile file."));
            }
            catch (Exception error) { status.Text = error.Message; }
        };
        duplicates.SelectionChanged += (_, _) => UpdatePlan();
        reviewed.IsCheckedChanged += (_, _) => UpdatePlan();
        import.Click += (_, _) => ImportSelected();
        var cancel = new Button { Name = "CancelNaps2Import", Content = "Cancel · Esc", IsCancel = true };
        cancel.Click += (_, _) => Close(0);
        Closed += (_, _) => closed = true;
        var top = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text("Import NAPS2 scan profiles as esyuscan presets. Source files stay unchanged. Default-profile markers, icons and cached capabilities are not imported. Only an exact driver and device ID can select a scanner when applying an imported preset."),
                locations,
                new WrapPanel { Orientation = Orientation.Horizontal, Children = { load, browse } },
                source,
                Text("Duplicate names (case-insensitive; within this file too). Replace uses the last selected profile with that name."),
                duplicates
            }
        };
        load.Margin = new Thickness(0, 0, 8, 8);
        browse.Margin = new Thickness(0, 0, 0, 8);
        var bottom = new StackPanel
        {
            Spacing = 10,
            Children = { reviewed, status, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { import, cancel } } }
        };
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 16 };
        layout.Children.Add(top);
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(bottom, 2);
        layout.Children.Add(bottom);
        Content = layout;
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        foreach (var control in new Control[] { locations, load, browse, duplicates, list, reviewed, import, cancel }) names.Register(control.Name!, control);
        UpdatePlan();
    }

    public async Task LoadFileAsync(string path)
    {
        if (reading || closed) return;
        reading = true;
        reviewed.IsChecked = false;
        rows.Clear();
        list.Children.Clear();
        source.Text = path;
        status.Text = "Reading profile preview…";
        UpdatePlan();
        try
        {
            var profiles = await Task.Run(() => Naps2Profiles.ReadFile(path));
            if (closed) return;
            foreach (var profile in profiles)
            {
                var selected = new CheckBox { Content = Text(profile.Name), IsEnabled = profile.CanImport, IsChecked = profile.CanImport };
                var outcome = Text("");
                var details = new StackPanel { Spacing = 8, Children = { selected, Text(profile.Summary), outcome } };
                if (profile.Origin.Notices.Count > 0)
                    details.Children.Add(new Expander
                    {
                        Header = $"{profile.Origin.Notices.Count} notes / options not applied — review",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = Text(string.Join("\n", profile.Origin.Notices))
                    });
                rows.Add((profile, selected, outcome));
                selected.IsCheckedChanged += (_, _) => UpdatePlan();
                list.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12),
                    Child = details,
                    BorderBrush = this.FindResource("BorderBrush") as IBrush
                });
            }
            status.Text = profiles.Count == 0 ? "This file contains no profiles." : $"{profiles.Count} profiles found. Choose which to import and review the notes. Scanner capabilities are checked when you apply a preset.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            status.Text = $"Could not preview profiles: {error.Message}";
        }
        finally { reading = false; UpdatePlan(); }
    }

    private ProfileDuplicatePolicy Policy => (ProfileDuplicatePolicy)Math.Max(0, duplicates.SelectedIndex);
    private IEnumerable<Naps2Profile> SelectedProfiles => rows.Where(r => r.Selected.IsChecked == true).Select(r => r.Profile);

    private void UpdatePlan()
    {
        var plan = Naps2Profiles.Plan(SelectedProfiles, model.Presets, Policy);
        var index = 0;
        foreach (var row in rows)
            row.Outcome.Text = row.Selected.IsChecked == true ? plan[index++].Outcome : row.Profile.CanImport ? "Not selected" : "Cannot import: " + row.Profile.Error;
        var count = plan.Count(item => item.Preset is not null);
        import.Content = $"Import selected ({count}) · Enter";
        import.IsEnabled = !reading && !model.Busy && reviewed.IsChecked == true && count > 0;
        load.IsEnabled = !reading && locations.SelectedItem is string;
        browse.IsEnabled = !reading;
        duplicates.IsEnabled = !reading;
        reviewed.IsEnabled = !reading && count > 0;
    }

    private void ImportSelected()
    {
        if (!import.IsEnabled) return;
        try { Close(model.ImportProfiles(SelectedProfiles, Policy)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            status.Text = $"Presets were not imported: {error.Message} You can retry or cancel.";
        }
    }

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
}
