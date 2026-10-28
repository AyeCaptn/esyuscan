using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NAPS2.Images;
using NAPS2.Pdf;

namespace Scan;

public sealed class ExportSelection
{
    public required ExportFormat Format { get; init; }
    public required ExportOptions Options { get; init; }
    public PdfOcrOptions? Ocr { get; init; }
}

/// <summary>Per-export UI; credentials never enter MainViewModel, preferences or session state.</summary>
public sealed class ExportWindow : Window
{
    private readonly ExportFormat workflow;
    private readonly bool hasPreservedPdf;
    private readonly ComboBox format;
    private readonly ComboBox tiff = Picker("TiffCompression", "Auto · lossless", "LZW · lossless", "Group 4 · 1-bit only", "None · uncompressed");
    private readonly ComboBox compression = Picker("PdfCompression", "Lossless", "JPEG · smaller, lossy");
    private readonly ComboBox compatibility = Picker("PdfCompatibility", "Standard PDF", "PDF/A-1b", "PDF/A-2b", "PDF/A-3b", "PDF/A-3u");
    private readonly NumericUpDown quality = new() { Name = "JpegQuality", Minimum = 1, Maximum = 100, Value = 90, Increment = 1, FormatString = "0" };
    private readonly CheckBox jbig2 = Check("AllowJbig2", "Allow lossless JBIG2 for 1-bit pages", true);
    private readonly CheckBox rasterize = Check("RasterizePdfSources", "Rasterize PDF sources for this export", false);
    private readonly CheckBox encrypt = Check("EncryptPdf", "Encrypt PDF / set permissions", false);
    private readonly TextBox openPassword = new() { Name = "PdfOpenPassword", PasswordChar = '●' };
    private readonly TextBox ownerPassword = new() { Name = "PdfOwnerPassword", PasswordChar = '●' };
    private readonly TextBox title = new() { Name = "PdfTitle" };
    private readonly TextBox author = new() { Name = "PdfAuthor" };
    private readonly TextBox subject = new() { Name = "PdfSubject" };
    private readonly TextBox keywords = new() { Name = "PdfKeywords" };
    private readonly TextBox creator = new() { Name = "PdfCreator", Text = "esyuscan" };
    private readonly CheckBox[] permissions = new[] { "Printing", "Full-quality printing", "Copy text and images", "Extract for accessibility", "Modify document", "Annotations", "Fill forms", "Assemble pages" }
        .Select((label, index) => Check("PdfPermission" + index, label, true)).ToArray();
    private readonly CheckBox searchable;
    private readonly ComboBox language;
    private readonly TextBlock error = Note("", "ExportValidationError");
    private ExportSelection? result;
    private readonly StackPanel pdfFormatPanel = new() { Spacing = 12 };
    private readonly StackPanel pdfPanel = new() { Spacing = 12 };
    private readonly StackPanel tiffPanel = new() { Spacing = 8 };
    private readonly StackPanel qualityPanel = new() { Spacing = 8 };
    private readonly StackPanel securityPanel = new() { Spacing = 10 };

    public ExportWindow(ExportFormat workflow, bool hasPreservedPdf, bool searchablePdf = false,
        IEnumerable<string>? languages = null, string? selectedLanguage = null)
    {
        this.workflow = workflow;
        this.hasPreservedPdf = hasPreservedPdf;
        Title = workflow switch { ExportFormat.Images => "esyuscan · Separate images", ExportFormat.SeparatePdfs => "esyuscan · Separate PDFs", _ => "esyuscan · One document" };
        Width = 560;
        Height = 670;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        format = workflow switch
        {
            ExportFormat.Images => Picker("ExportFileFormat", "PNG · lossless", "JPEG · lossy", "TIFF · one file per page"),
            ExportFormat.SeparatePdfs => Picker("ExportFileFormat", "PDF · one file per page"),
            _ => Picker("ExportFileFormat", "PDF · combined", "TIFF · multipage")
        };
        if (workflow == ExportFormat.CombinedTiff) format.SelectedIndex = 1;
        searchable = Check("ExportSearchable", "Searchable PDF (OCR)", searchablePdf);
        var installedLanguages = languages?.OrderBy(OcrLanguageCatalog.Name, StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [];
        language = new ComboBox
        {
            Name = "ExportOcrLanguage",
            ItemsSource = installedLanguages,
            SelectedItem = selectedLanguage,
            ItemTemplate = new FuncDataTemplate<string>((code, _) => new TextBlock
            {
                Text = code is null ? "" : OcrLanguageCatalog.DisplayName(code),
                TextTrimming = TextTrimming.CharacterEllipsis
            })
        };
        var languageSetup = new Button { Name = "OcrLanguageSetup", Content = "Language models and setup…", Classes = { "quiet" } };
        languageSetup.Click += async (_, _) =>
        {
            var selected = await new OcrLanguagesWindow(installedLanguages, language.SelectedItem as string)
            { Width = Width, Height = Height }.ShowDialog<string?>(this);
            if (selected is not null) language.SelectedItem = selected;
        };
        tiffPanel.Children.Add(Field("TIFF compression", tiff));
        tiffPanel.Children.Add(Note("Auto uses Group 4 for 1-bit pages and LZW for color/grayscale. Forced Group 4 requires every page to be 1-bit; esyuscan refuses color conversion. JPEG-in-TIFF is unavailable in this SDK."));
        qualityPanel.Children.Add(Field("JPEG quality · 1–100", quality));
        qualityPanel.Children.Add(Note("Higher quality keeps more detail and usually makes larger files. Default 90. Lossless PNG/TIFF have no quality slider."));
        pdfFormatPanel.Children.Add(Field("PDF compatibility", compatibility));
        pdfFormatPanel.Children.Add(Field("Compression for rendered pages", compression));
        pdfPanel.Children.Add(jbig2);
        pdfPanel.Children.Add(rasterize);
        pdfPanel.Children.Add(Note("Standard PDF retains unedited PDF text/vectors. Compression affects rendered pages only. PDF/A with PDF sources requires explicit rasterization: existing text/vectors are lost at the stored preview resolution (normally 300 DPI). OCR can add new text. Originals stay intact. SDK PDF/A output is not independently certified."));
        pdfPanel.Children.Add(searchable);
        pdfPanel.Children.Add(Field("Installed OCR language", language));
        pdfPanel.Children.Add(languageSetup);
        var metadata = new StackPanel { Spacing = 10 };
        foreach (var pair in new[] { ("Title", title), ("Author", author), ("Subject", subject), ("Keywords", keywords), ("Creator", creator) })
            metadata.Children.Add(Field(pair.Item1, pair.Item2));
        pdfPanel.Children.Add(new Expander { Name = "PdfMetadataPanel", Header = "PDF metadata", HorizontalAlignment = HorizontalAlignment.Stretch, Content = metadata });
        pdfPanel.Children.Add(encrypt);
        securityPanel.Children.Add(Note("NAPS2 1.6.0 supports legacy 128-bit RC4, not AES. Use 1–32 printable ASCII characters. PDF/A cannot be encrypted. Reader permissions are advisory. Passwords are used for this export only; re-enter them next time."));
        securityPanel.Children.Add(Field("Open password · optional", openPassword));
        securityPanel.Children.Add(Field("Owner password · required, different from open password", ownerPassword));
        securityPanel.Children.Add(Note("Allowed actions for someone using the open password (or opening without a password when it is empty):"));
        foreach (var permission in permissions) securityPanel.Children.Add(permission);
        pdfPanel.Children.Add(securityPanel);
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Note(workflow == ExportFormat.Images ? "One numbered file per page in a new folder. All image formats render PDF text/vectors as pixels."
            : workflow == ExportFormat.SeparatePdfs ? "One numbered PDF per page in a new folder. Settings and metadata apply to every file."
            : "One file in the current page order. Multipage TIFF contains pixels only; OCR and PDF controls are available with PDF."));
        body.Children.Add(Field("File format", format));
        body.Children.Add(pdfFormatPanel);
        body.Children.Add(qualityPanel);
        body.Children.Add(tiffPanel);
        body.Children.Add(pdfPanel);
        var scroll = new ScrollViewer { Name = "ExportOptionsScroll", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var accept = new Button { Name = "AcceptExportOptions", Content = "Choose destination · Enter", IsDefault = true, Classes = { "primary" } };
        accept.Click += (_, _) => Accept();
        var cancel = new Button { Name = "CancelExportOptions", Content = "Cancel · Esc", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { accept, cancel } };
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 12 };
        layout.Children.Add(scroll);
        Grid.SetRow(error, 1);
        layout.Children.Add(error);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        Content = layout;
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        foreach (var control in new Control[] { format, tiff, compression, compatibility, quality, jbig2, rasterize, encrypt, openPassword,
            ownerPassword, title, author, subject, keywords, creator, searchable, language, languageSetup, error, scroll, accept, cancel }.Concat(permissions))
            names.Register(control.Name!, control);
        format.SelectionChanged += (_, _) => UpdateControls();
        compression.SelectionChanged += (_, _) => UpdateControls();
        encrypt.IsCheckedChanged += (_, _) => UpdateControls();
        searchable.IsCheckedChanged += (_, _) => UpdateControls();
        permissions[0].IsCheckedChanged += (_, _) =>
        {
            if (permissions[0].IsChecked != true) permissions[1].IsChecked = false;
            permissions[1].IsEnabled = permissions[0].IsChecked == true;
        };
        Closed += (_, _) => { openPassword.Text = ""; ownerPassword.Text = ""; };
        UpdateControls();
    }

    public ExportSelection? TakeResult()
    {
        var selection = result;
        result = null;
        return selection;
    }

    private ExportFormat SelectedFormat => workflow is ExportFormat.CombinedPdf or ExportFormat.CombinedTiff
        ? format.SelectedIndex == 1 ? ExportFormat.CombinedTiff : ExportFormat.CombinedPdf : workflow;

    private void UpdateControls()
    {
        var pdf = ExportOptions.IsPdf(SelectedFormat);
        pdfPanel.IsVisible = pdf;
        pdfFormatPanel.IsVisible = pdf;
        tiffPanel.IsVisible = SelectedFormat == ExportFormat.CombinedTiff || workflow == ExportFormat.Images && format.SelectedIndex == 2;
        qualityPanel.IsVisible = pdf && compression.SelectedIndex == 1 || workflow == ExportFormat.Images && format.SelectedIndex == 1;
        securityPanel.IsVisible = encrypt.IsChecked == true;
        language.IsEnabled = searchable.IsChecked == true;
        if (!pdf || encrypt.IsChecked != true) { openPassword.Text = ""; ownerPassword.Text = ""; }
    }

    private void Accept()
    {
        try
        {
            var pdf = ExportOptions.IsPdf(SelectedFormat);
            if (qualityPanel.IsVisible && (quality.Value is null || quality.Value != decimal.Truncate(quality.Value.Value)))
                throw new ArgumentException("Enter a whole-number JPEG quality from 1 to 100.");
            var ocr = pdf && searchable.IsChecked == true
                ? new PdfOcrOptions(language.SelectedItem as string ?? throw new ArgumentException("Select an installed OCR language or disable searchable PDF.")) : null;
            var options = new ExportOptions
            {
                ImageFormat = workflow == ExportFormat.Images ? (ImageOutput)format.SelectedIndex : ImageOutput.Png,
                TiffCompression = tiffPanel.IsVisible ? (TiffCompressionType)tiff.SelectedIndex : TiffCompressionType.Auto,
                JpegQuality = qualityPanel.IsVisible ? (int?)quality.Value : null,
                PdfCompression = pdf ? (PdfImageCompression)compression.SelectedIndex : PdfImageCompression.Lossless,
                AllowJbig2 = !pdf || jbig2.IsChecked == true,
                Compatibility = pdf ? (PdfCompat)compatibility.SelectedIndex : PdfCompat.Default,
                RasterizePdfSources = pdf && rasterize.IsChecked == true,
                Metadata = pdf ? new PdfMetadata { Title = title.Text ?? "", Author = author.Text ?? "", Subject = subject.Text ?? "", Keywords = keywords.Text ?? "", Creator = creator.Text ?? "" } : new() { Creator = "esyuscan" },
                Passwords = pdf && encrypt.IsChecked == true ? new PdfPasswords { Open = openPassword.Text, Owner = ownerPassword.Text } : null,
                Permissions = pdf && encrypt.IsChecked == true ? new PdfPermissions
                {
                    Printing = permissions[0].IsChecked == true,
                    FullQualityPrinting = permissions[1].IsChecked == true,
                    Copying = permissions[2].IsChecked == true,
                    Accessibility = permissions[3].IsChecked == true,
                    Modification = permissions[4].IsChecked == true,
                    Annotations = permissions[5].IsChecked == true,
                    FormFilling = permissions[6].IsChecked == true,
                    Assembly = permissions[7].IsChecked == true
                } : new()
            };
            options.Validate(SelectedFormat, ocr, hasPreservedPdf);
            result = new() { Format = SelectedFormat, Options = options, Ocr = ocr };
            Close(true);
        }
        catch (ArgumentException validation) { error.Text = validation.Message; }
    }

    private static ComboBox Picker(string name, params string[] items) => new() { Name = name, ItemsSource = items, SelectedIndex = 0 };
    private static CheckBox Check(string name, string label, bool value) => new() { Name = name, Content = Note(label), IsChecked = value };
    private static TextBlock Note(string text, string? name = null) => new() { Name = name, Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private static StackPanel Field(string label, Control control) => new() { Spacing = 6, Children = { Note(label), control } };
}
