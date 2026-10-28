using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Scan;

public sealed class PrintWindow : Window
{
    public PrintWindow(string[] printers)
    {
        Title = "esyuscan · Print document";
        Width = 440;
        MinWidth = 360;
        MaxHeight = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var printer = new ComboBox { Name = "PrintPrinterPicker", ItemsSource = printers, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var copies = new NumericUpDown { Name = "PrintCopies", Minimum = 1, Maximum = 999, Value = 1, Increment = 1, FormatString = "0", ParsingNumberStyle = NumberStyles.Integer, ClipValueToMinMax = true };
        var sides = new ComboBox
        {
            Name = "PrintSidesPicker",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new ScanChoice<PrintSides>[] { new("One-sided", PrintSides.OneSided), new("Two-sided · long edge", PrintSides.LongEdge), new("Two-sided · short edge", PrintSides.ShortEdge) },
            SelectedIndex = 0
        };
        var media = new ComboBox { Name = "PrintMediaPicker", ItemsSource = new[] { "Default", "A4", "Letter" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var fit = new CheckBox { Name = "PrintFitCheck", Content = "Fit pages to printable area", IsChecked = true };
        var print = new Button { Name = "SubmitPrintButton", Content = "Print document · Enter", Classes = { "primary" }, IsDefault = true, IsEnabled = printers.Length > 0 };
        var cancel = new Button { Content = "Cancel · Esc", IsCancel = true };
        var validation = new TextBlock { Text = "Enter a whole number of copies between 1 and 999.", IsVisible = false, TextWrapping = TextWrapping.Wrap };
        print.Click += (_, _) =>
        {
            if (printer.SelectedItem is string selected && sides.SelectedItem is ScanChoice<PrintSides> selectedSides && copies.Value is { } count && count is >= 1 and <= 999 && count == decimal.Truncate(count))
                Close(new PrintOptions(selected, (int)count, selectedSides.Value, (string)media.SelectedItem!, fit.IsChecked == true));
            else validation.IsVisible = true;
        };
        cancel.Click += (_, _) => Close();
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        foreach (var control in new Control[] { printer, copies, sides, media, fit, print }) names.Register(control.Name!, control);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Print the current document", FontSize = 19 },
                    new TextBlock { Text = printers.Length == 0 ? "No CUPS printers are available. Configure a printer in System printer settings, then reopen this dialog." : "All pages in their current order, with saved edits. Options depend on your printer; other defaults come from CUPS.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Printer" }, printer,
                    new TextBlock { Text = "Copies (collated)" }, copies, validation,
                    new TextBlock { Text = "Sides" }, sides,
                    new TextBlock { Text = "Paper size" }, media, fit,
                    new TextBlock { Text = "Print submits to CUPS. You can request cancellation of the last submitted job in esyuscan; pages already printed cannot be recalled.", FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    new WrapPanel { Orientation = Orientation.Horizontal, Children = { print, cancel } }
                }
            }
        };
    }
}
