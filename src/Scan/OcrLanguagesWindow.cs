using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Scan;

/// <summary>An offline catalogue. Missing models are informational, never selectable or installed here.</summary>
public sealed class OcrLanguagesWindow : Window
{
    public OcrLanguagesWindow(IEnumerable<string> installedLanguages, string? selectedLanguage = null)
    {
        Title = "esyuscan · OCR languages";
        Width = 620;
        Height = 680;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var installed = installedLanguages.ToHashSet(StringComparer.Ordinal);
        var known = OcrLanguageCatalog.Languages.Select(language => language.Code).ToHashSet(StringComparer.Ordinal);
        var languages = OcrLanguageCatalog.Languages
            .Concat(installed.Where(code => !known.Contains(code)).Select(code => new OcrLanguageDefinition(code, OcrLanguageCatalog.Name(code))))
            .OrderBy(language => language.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var search = new TextBox { Name = "OcrLanguageSearch", PlaceholderText = "Find a language or code…" };
        var list = new StackPanel { Name = "OcrLanguageList", Spacing = 8 };
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        names.Register(search.Name!, search);
        names.Register(list.Name!, list);
        var rows = new List<(OcrLanguageDefinition Language, Border Row)>();
        foreach (var language in languages)
        {
            var available = installed.Contains(language.Code);
            var choose = new Button
            {
                Name = "ChooseOcrLanguage_" + language.Code,
                Content = language.Code == selectedLanguage ? "Selected" : "Use",
                IsEnabled = available,
                Padding = new Thickness(12, 8),
                FontSize = 11
            };
            choose.Click += (_, _) => { if (available) Close(language.Code); };
            names.Register(choose.Name, choose);
            var description = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = language.Name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{language.Code} · {(available ? "Installed" : "Not detected")}", FontSize = 11, Classes = { "muted" } }
                }
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(description);
            Grid.SetColumn(choose, 1);
            row.Children.Add(choose);
            var border = new Border { Child = row, Padding = new Thickness(12), BorderThickness = new Thickness(0, 0, 0, 1) };
            border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BorderBrush"));
            rows.Add((language, border));
            list.Children.Add(border);
        }
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? "";
            foreach (var (language, row) in rows)
                row.IsVisible = language.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || language.Code.Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        var header = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "OCR languages", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = $"{OcrLanguageCatalog.Languages.Count} Tesseract language models and variants. Choose one installed language per export. Unavailable models are listed for reference; custom installed models are included.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 11
                },
                new TextBlock
                {
                    Text = "esyuscan never downloads models. Install Tesseract and language data separately (e.g. tesseract-data-fra on Arch), then close this window and Refresh OCR. Setup: /usr/share/doc/esyuscan/searchable-pdf.md.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" }
                },
                search
            }
        };
        var close = new Button { Content = "Back · Esc", IsCancel = true };
        close.Click += (_, _) => Close();
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 16 };
        layout.Children.Add(header);
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(close, 2);
        layout.Children.Add(close);
        Content = layout;
        Opened += (_, _) => search.Focus();
    }
}
