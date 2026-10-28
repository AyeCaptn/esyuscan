using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class ExportUiTests
{
    [AvaloniaTest]
    public void SeparateImagesOffersAllFormatsAndOnlyRelevantQualityControls()
    {
        var window = new ExportWindow(ExportFormat.Images, hasPreservedPdf: true);
        window.Show();
        try
        {
            var format = window.FindControl<ComboBox>("ExportFileFormat")!;
            Assert.That(format.ItemCount, Is.EqualTo(3));
            format.SelectedIndex = 1;
            window.FindControl<NumericUpDown>("JpegQuality")!.Value = 75;
            Accept(window);
            var result = window.TakeResult()!;
            Assert.That(result.Format, Is.EqualTo(ExportFormat.Images));
            Assert.That(result.Options.ImageFormat, Is.EqualTo(ImageOutput.Jpeg));
            Assert.That(result.Options.JpegQuality, Is.EqualTo(75));
            Assert.That(result.Ocr, Is.Null);
            Assert.That(window.TakeResult(), Is.Null, "The dialog must release its runtime result after handoff.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void CombinedTiffIsOneDocumentAndExcludesPdfOnlyOptions()
    {
        var window = new ExportWindow(ExportFormat.CombinedPdf, hasPreservedPdf: true, searchablePdf: true, languages: ["eng"], selectedLanguage: "eng");
        window.Show();
        try
        {
            window.FindControl<ComboBox>("ExportFileFormat")!.SelectedIndex = 1;
            window.FindControl<ComboBox>("TiffCompression")!.SelectedIndex = 1;
            Accept(window);
            var result = window.TakeResult()!;
            Assert.That(result.Format, Is.EqualTo(ExportFormat.CombinedTiff));
            Assert.That(result.Options.TiffCompression, Is.EqualTo(NAPS2.Images.TiffCompressionType.Lzw));
            Assert.That(result.Ocr, Is.Null);
            Assert.That(result.Options.Passwords, Is.Null);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void PdfPasswordsAreMaskedAndClearedOnCancel()
    {
        var window = new ExportWindow(ExportFormat.SeparatePdfs, hasPreservedPdf: false);
        window.Show();
        var open = window.FindControl<TextBox>("PdfOpenPassword")!;
        var owner = window.FindControl<TextBox>("PdfOwnerPassword")!;
        window.FindControl<CheckBox>("EncryptPdf")!.IsChecked = true;
        open.Text = Guid.NewGuid().ToString("N");
        owner.Text = Guid.NewGuid().ToString("N");
        Assert.That(open.PasswordChar, Is.Not.EqualTo('\0'));
        Assert.That(owner.PasswordChar, Is.Not.EqualTo('\0'));
        window.FindControl<Button>("CancelExportOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(open.Text, Is.Empty);
        Assert.That(owner.Text, Is.Empty);
        Assert.That(window.TakeResult(), Is.Null);
    }

    [AvaloniaTest]
    public void InvalidPdfAEncryptionStaysInDialogAndExplainsConflictWithoutSecrets()
    {
        var window = new ExportWindow(ExportFormat.CombinedPdf, hasPreservedPdf: false);
        window.Show();
        try
        {
            window.FindControl<ComboBox>("PdfCompatibility")!.SelectedIndex = 1;
            window.FindControl<CheckBox>("EncryptPdf")!.IsChecked = true;
            var password = Guid.NewGuid().ToString("N");
            window.FindControl<TextBox>("PdfOwnerPassword")!.Text = password;
            Accept(window);
            Assert.That(window.IsVisible, Is.True);
            Assert.That(window.TakeResult(), Is.Null);
            Assert.That(window.FindControl<TextBlock>("ExportValidationError")!.Text, Does.Contain("PDF/A cannot be encrypted").And.Not.Contain(password));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void PdfAPassthroughRequiresExplicitRasterizationAndSupportsOcr()
    {
        var window = new ExportWindow(ExportFormat.SeparatePdfs, true, true, ["eng", "nld"], "nld");
        window.Show();
        try
        {
            window.FindControl<ComboBox>("PdfCompatibility")!.SelectedIndex = 2;
            Accept(window);
            Assert.That(window.FindControl<TextBlock>("ExportValidationError")!.Text, Does.Contain("rasterize"));
            window.FindControl<CheckBox>("RasterizePdfSources")!.IsChecked = true;
            Accept(window);
            var result = window.TakeResult()!;
            Assert.That(result.Options.RasterizePdfSources, Is.True);
            Assert.That(result.Ocr!.Language, Is.EqualTo("nld"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void NarrowExportDialogKeepsActionsVisibleAndOptionsScrollable()
    {
        var window = new ExportWindow(ExportFormat.CombinedPdf, true) { Width = 440, Height = 500 };
        window.Show();
        try
        {
            window.FindControl<CheckBox>("EncryptPdf")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var action = window.FindControl<Button>("AcceptExportOptions")!;
            var corner = action.TranslatePoint(new Point(action.Bounds.Width, action.Bounds.Height), window)!.Value;
            Assert.That(corner.X, Is.LessThanOrEqualTo(window.ClientSize.Width));
            Assert.That(corner.Y, Is.LessThanOrEqualTo(window.ClientSize.Height));
            var scroll = window.FindControl<ScrollViewer>("ExportOptionsScroll")!;
            Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height));
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            var last = window.FindControl<CheckBox>("PdfPermission7")!;
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), window)!.Value.Y;
            Assert.That(bottom, Is.LessThan(action.TranslatePoint(new Point(), window)!.Value.Y));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest, Category("Integration")]
    public void RenderExportOptionsAtTiledSize()
    {
        OmarchyTheme.Apply(ThemePalette.Default);
        var window = new ExportWindow(ExportFormat.CombinedPdf, true, true, ["eng"], "eng") { Width = 480, Height = 600 };
        window.Show();
        try
        {
            window.FindControl<ComboBox>("PdfCompression")!.SelectedIndex = 1;
            window.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(480, 600), new Vector(96, 96));
            bitmap.Render(window);
            if (Environment.GetEnvironmentVariable("ESYUSCAN_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, "export-options.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            var quality = window.FindControl<NumericUpDown>("JpegQuality")!;
            var bottom = quality.TranslatePoint(new Point(0, quality.Bounds.Height), window)!.Value.Y;
            Assert.That(bottom, Is.LessThan(500), "JPEG quality should be visible next to the compression choice without scrolling.");
        }
        finally { window.Close(); }
    }

    private static void Accept(ExportWindow window) => window.FindControl<Button>("AcceptExportOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
