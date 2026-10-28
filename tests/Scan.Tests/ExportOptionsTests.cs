using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Pdf;
using NUnit.Framework;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Scan.Tests;

[TestFixture, Category("Integration")]
public sealed class ExportOptionsTests : TestFiles
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task EncryptedMixedPdfRetainsMetadataPermissionsAndVectorContent(bool rotate)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var input = Path.Combine(Root, "native.pdf");
        PdfImportTests.WritePdfFile(input, "PRESERVED");
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        if (rotate) session.Rotate(1);
        var owner = Guid.NewGuid().ToString("N");
        var user = Guid.NewGuid().ToString("N");
        var target = Path.Combine(Root, "protected.pdf");
        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None, options: new()
        {
            Passwords = new() { Owner = owner, Open = user },
            Metadata = new() { Title = "Invoice & notes", Author = "esyuscan test", Creator = "esyuscan", Subject = "Subject", Keywords = "one, two" },
            Permissions = new() { Printing = false, FullQualityPrinting = false, Copying = false, Modification = false }
        });
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(target, PdfDocumentOpenMode.Import));
        using var document = PdfReader.Open(target, user, PdfDocumentOpenMode.Import);
        Assert.Multiple(() =>
        {
            Assert.That(document.PageCount, Is.EqualTo(2));
            Assert.That(document.Info.Title, Is.EqualTo("Invoice & notes"));
            Assert.That(document.Info.Author, Is.EqualTo("esyuscan test"));
            Assert.That(document.SecuritySettings.PermitPrint, Is.False);
            Assert.That(document.SecuritySettings.PermitExtractContent, Is.False);
            Assert.That(document.SecuritySettings.PermitModifyDocument, Is.False);
            Assert.That(document.Pages[1].Rotate, Is.EqualTo(rotate ? 90 : 0));
        });
        var operators = PdfSharpCore.Pdf.Content.ContentReader.ReadContent(document.Pages[1]).OfType<PdfSharpCore.Pdf.Content.Objects.COperator>();
        Assert.That(operators.Any(op => op.OpCode.Name == "re"), Is.True);
    }

    [TestCase(PdfCompat.PdfA1B, "1", "B")]
    [TestCase(PdfCompat.PdfA2B, "2", "B")]
    [TestCase(PdfCompat.PdfA3B, "3", "B")]
    [TestCase(PdfCompat.PdfA3U, "3", "U")]
    public async Task PdfAHasRequestedXmpAndColorProfile(PdfCompat compat, string part, string conformance)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "archival.pdf");
        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { Compatibility = compat, Metadata = new() { Creator = "esyuscan", Title = "Archival résumé" } });
        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var metadata = document.Internals.Catalog.Elements.GetDictionary("/Metadata");
        var xml = XDocument.Parse(Encoding.UTF8.GetString(metadata.Stream.UnfilteredValue));
        XNamespace id = "http://www.aiim.org/pdfa/ns/id/";
        Assert.That(xml.Descendants().Attributes(id + "part").Single().Value, Is.EqualTo(part));
        Assert.That(xml.Descendants().Attributes(id + "conformance").Single().Value, Is.EqualTo(conformance));
        Assert.That(document.Internals.Catalog.Elements.GetArray("/OutputIntents").Elements.Count, Is.EqualTo(1));
        Assert.That(document.Version, Is.EqualTo(compat == PdfCompat.PdfA1B ? 14 : 17));
    }
    [Test]
    public async Task PdfA1RejectsTransparencyRatherThanClaimingUnsupportedConformance()
    {
        using var session = CreateSession();
        var path = session.NextOriginalPath();
        using (var bitmap = new SKBitmap(80, 100))
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(100, 120, 140, 128));
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            png.SaveTo(stream);
        }
        session.Add(path);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "alpha.pdf");
        Assert.ThrowsAsync<NotSupportedException>(() => scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { Compatibility = PdfCompat.PdfA1B }));
        Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { Compatibility = PdfCompat.PdfA2B });
        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.That(document.PageCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PdfJpegCompressionUsesActualJpegBytesAtRequestedQuality()
    {
        using var session = CreateSession();
        ImageOutputTests.AddTexture(session);
        using var scanner = new Scanner(Root);
        var lossless = Path.Combine(Root, "lossless.pdf");
        var low = Path.Combine(Root, "low.pdf");
        var high = Path.Combine(Root, "high.pdf");
        await scanner.Export(session, lossless, ExportFormat.CombinedPdf, CancellationToken.None);
        await scanner.Export(session, low, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { PdfCompression = PdfImageCompression.Jpeg, JpegQuality = 15 });
        await scanner.Export(session, high, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { PdfCompression = PdfImageCompression.Jpeg, JpegQuality = 95 });
        Assert.That(new FileInfo(high).Length, Is.GreaterThan(new FileInfo(low).Length * 2));
        using var losslessDoc = PdfReader.Open(lossless, PdfDocumentOpenMode.Import);
        using var lowDoc = PdfReader.Open(low, PdfDocumentOpenMode.Import);
        using var highDoc = PdfReader.Open(high, PdfDocumentOpenMode.Import);
        static string Filter(PdfSharpCore.Pdf.PdfDocument doc)
        {
            var images = doc.Internals.GetAllObjects().OfType<PdfSharpCore.Pdf.PdfDictionary>()
                .Where(dict => dict.Elements.GetName("/Subtype") == "/Image");
            return string.Join(" ", images.Select(dict => dict.Elements["/Filter"].ToString()));
        }
        Assert.That(Filter(losslessDoc), Does.Contain("FlateDecode").And.Not.Contain("DCTDecode"));
        Assert.That(Filter(lowDoc), Does.Contain("DCTDecode"));
        Assert.That(Filter(highDoc), Does.Contain("DCTDecode"));
    }

    [TestCase(ExportFormat.SeparatePdfs)]
    [TestCase(ExportFormat.CombinedPdf)]
    public async Task MetadataAndAllDeniedPermissionsApplyToEveryDocument(ExportFormat format)
    {
        using var session = CreateSession();
        AddPages(session, 2);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "restricted");
        var owner = Guid.NewGuid().ToString("N");
        var metadata = new PdfMetadata { Title = "Résumé <2026>", Author = "A & B", Subject = "Quotes: \"'", Keywords = "archive; invoice", Creator = "esyuscan test" };
        await scanner.Export(session, target, format, CancellationToken.None, options: new()
        {
            Metadata = metadata,
            Passwords = new() { Owner = owner },
            Permissions = new()
            {
                Printing = false,
                FullQualityPrinting = false,
                Copying = false,
                Accessibility = false,
                Modification = false,
                Annotations = false,
                FormFilling = false,
                Assembly = false
            }
        });
        var files = format == ExportFormat.CombinedPdf ? [target] : Directory.GetFiles(target).Order().ToArray();
        Assert.That(files.Length, Is.EqualTo(format == ExportFormat.CombinedPdf ? 1 : 2));
        foreach (var file in files)
        {
            // Empty open password deliberately permits opening, while the generated owner password controls permissions.
            using var document = PdfReader.Open(file, PdfDocumentOpenMode.Import);
            Assert.That(document.Info.Title, Is.EqualTo(metadata.Title));
            Assert.That(document.Info.Author, Is.EqualTo(metadata.Author));
            Assert.That(document.Info.Subject, Is.EqualTo(metadata.Subject));
            Assert.That(document.Info.Keywords, Is.EqualTo(metadata.Keywords));
            Assert.That(document.Info.Creator, Is.EqualTo(metadata.Creator));
            var security = document.SecuritySettings;
            Assert.That(new[] { security.PermitPrint, security.PermitFullQualityPrint, security.PermitExtractContent,
                security.PermitAccessibilityExtractContent, security.PermitModifyDocument, security.PermitAnnotations,
                security.PermitFormsFill, security.PermitAssembleDocument }, Is.All.False);
            Assert.That(document.SecurityHandler.ValidatePassword(owner), Is.EqualTo(PasswordValidity.OwnerPassword));
        }
    }

    [Test]
    public async Task PdfAPassthroughIsBlockedUntilRasterizationIsExplicitAndOriginalsStayIntact()
    {
        using var session = CreateSession();
        using var scanner = new Scanner(Root);
        var input = Path.Combine(Root, "source.pdf");
        PdfImportTests.WritePdfFile(input, "PRESERVED");
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        session.Rotate(0);
        var manifest = File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json"));
        var source = session.PdfSourcePath(session.Pages[0].PdfSource!);
        var original = File.ReadAllBytes(source);
        var target = Path.Combine(Root, "archival.pdf");
        Assert.ThrowsAsync<ArgumentException>(() => scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { Compatibility = PdfCompat.PdfA2B }));
        Assert.That(Path.Exists(target), Is.False);
        await scanner.Export(session, target, ExportFormat.CombinedPdf, CancellationToken.None,
            options: new() { Compatibility = PdfCompat.PdfA2B, RasterizePdfSources = true });
        using var document = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var operators = PdfSharpCore.Pdf.Content.ContentReader.ReadContent(document.Pages[0]).OfType<PdfSharpCore.Pdf.Content.Objects.COperator>().ToArray();
        Assert.That(operators.Any(op => op.OpCode.Name == "Do"), Is.True);
        Assert.That(operators.Any(op => op.OpCode.Name == "re"), Is.False);
        Assert.That(document.Pages[0].Width.Point, Is.GreaterThan(document.Pages[0].Height.Point));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));
        Assert.That(File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(manifest));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EncryptedExportCancellationAndDestinationRaceDoNotLeakPasswords(bool race)
    {
        using var session = CreateSession();
        AddPages(session, 2);
        using var scanner = new Scanner(Root);
        using var cancellation = new CancellationTokenSource();
        var owner = Guid.NewGuid().ToString("N");
        var target = Path.Combine(Root, "secret.pdf");
        var error = Assert.CatchAsync(() => scanner.Export(session, target, ExportFormat.CombinedPdf, cancellation.Token,
            progress: value =>
            {
                if (!value.Message.StartsWith("Publishing")) return;
                if (race) File.WriteAllText(target, "keep");
                else cancellation.Cancel();
            }, options: new() { Passwords = new() { Owner = owner } }));
        Assert.That(error, Is.InstanceOf(race ? typeof(IOException) : typeof(OperationCanceledException)));
        Assert.That(error!.ToString(), Does.Not.Contain(owner));
        if (race) Assert.That(File.ReadAllText(target), Is.EqualTo("keep"));
        else Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

}

[TestFixture, Category("Integration")]
public sealed class ImageOutputTests : TestFiles
{
    [TestCase(ImageOutput.Png, "png")]
    [TestCase(ImageOutput.Jpeg, "jpg")]
    [TestCase(ImageOutput.Tiff, "tiff")]
    public async Task MixedEditedPagesExportWithRequestedImageFormat(ImageOutput output, string extension)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var input = Path.Combine(Root, "native.pdf");
        PdfImportTests.WritePdfFile(input, "VECTOR");
        using var scanner = new Scanner(Root);
        await scanner.Import(session, [input], () => { }, CancellationToken.None);
        session.SetCrop(0, new(2, 3, 40, 60));
        session.Rotate(0);
        session.Move(1, 0);
        var manifest = File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json"));
        var target = Path.Combine(Root, "images");
        await scanner.Export(session, target, ExportFormat.Images, CancellationToken.None, options: new() { ImageFormat = output });
        Assert.That(Directory.GetFiles(target).Select(Path.GetFileName).Order(), Is.EqualTo(new[] { $"page-001.{extension}", $"page-002.{extension}" }));
        var context = new GtkImageContext();
        for (var i = 0; i < session.Pages.Count; i++)
        {
            using var expected = PageRenderer.Render(session, session.Pages[i]);
            using var actual = context.Load(Path.Combine(target, $"page-{i + 1:000}.{extension}"));
            Assert.That(actual.OriginalFileFormat, Is.EqualTo(output switch { ImageOutput.Jpeg => ImageFileFormat.Jpeg, ImageOutput.Tiff => ImageFileFormat.Tiff, _ => ImageFileFormat.Png }));
            Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
            if (output != ImageOutput.Jpeg) AssertPixelsEqual(expected, actual);
        }
        Assert.That(File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(manifest));
    }

    [Test]
    public async Task JpegQualityChangesQuantizationSizeAndDecodedError()
    {
        using var session = CreateSession();
        AddTexture(session);
        using var scanner = new Scanner(Root);
        var low = Path.Combine(Root, "low");
        var high = Path.Combine(Root, "high");
        await scanner.Export(session, low, ExportFormat.Images, CancellationToken.None, options: new() { ImageFormat = ImageOutput.Jpeg, JpegQuality = 15 });
        await scanner.Export(session, high, ExportFormat.Images, CancellationToken.None, options: new() { ImageFormat = ImageOutput.Jpeg, JpegQuality = 95 });
        var lowPath = Path.Combine(low, "page-001.jpg");
        var highPath = Path.Combine(high, "page-001.jpg");
        Assert.That(File.ReadAllBytes(lowPath)[..2], Is.EqualTo(new byte[] { 0xff, 0xd8 }));
        Assert.That(new FileInfo(highPath).Length, Is.GreaterThan(new FileInfo(lowPath).Length * 2));
        using var expected = SKBitmap.Decode(session.OriginalPath(session.Pages[0]));
        using var lowImage = SKBitmap.Decode(lowPath);
        using var highImage = SKBitmap.Decode(highPath);
        Assert.That(Error(expected, highImage), Is.LessThan(Error(expected, lowImage)));
    }

    [TestCase(TiffCompressionType.Auto, 5)]
    [TestCase(TiffCompressionType.Lzw, 5)]
    [TestCase(TiffCompressionType.None, 1)]
    public async Task MultipageTiffRetainsPageOrderEditsAndColor(TiffCompressionType compression, int tag)
    {
        using var session = CreateSession();
        AddPages(session, 3);
        session.Move(2, 0);
        session.Rotate(1);
        session.SetCrop(2, new(1, 2, 40, 60));
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "stack.tiff");
        await scanner.Export(session, target, ExportFormat.CombinedTiff, CancellationToken.None, options: new() { TiffCompression = compression });
        var directories = ReadTiffDirectories(target);
        Assert.That(directories.Select(tags => tags[259]), Is.EqualTo(new[] { tag, tag, tag }));
        var frames = new List<IMemoryImage>();
        try
        {
            await foreach (var frame in new GtkImageContext().LoadFrames(target)) frames.Add(frame);
            Assert.That(frames, Has.Count.EqualTo(3));
            for (var i = 0; i < frames.Count; i++)
            {
                using var expected = PageRenderer.Render(session, session.Pages[i]);
                AssertPixelsEqual(expected, frames[i]);
            }
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    [TestCase(TiffCompressionType.Auto)]
    [TestCase(TiffCompressionType.Ccitt4)]
    public async Task BilevelTiffUsesGroup4WithoutChangingPixels(TiffCompressionType compression)
    {
        using var session = CreateSession();
        using (var image = new SKBitmap(80, 100))
        {
            using var canvas = new SKCanvas(image);
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            canvas.DrawRect(8, 12, 30, 24, paint);
            var path = session.NextOriginalPath();
            using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(path)) encoded.SaveTo(file);
            session.Add(path);
        }
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "bilevel");
        await scanner.Export(session, target, ExportFormat.Images, CancellationToken.None,
            options: new() { ImageFormat = ImageOutput.Tiff, TiffCompression = compression });
        var pathTiff = Path.Combine(target, "page-001.tiff");
        var tags = ReadTiffDirectories(pathTiff).Single();
        Assert.That(tags[259], Is.EqualTo(4));
        Assert.That(tags[258], Is.EqualTo(1));
        using var actual = new GtkImageContext().Load(pathTiff);
        using var expected = PageRenderer.Render(session, session.Pages[0]);
        AssertPixelsEqual(expected, actual);
    }

    [Test]
    public void ForcedGroup4NeverSilentlyDiscardsColor()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "invalid.tiff");
        var error = Assert.ThrowsAsync<ArgumentException>(() => scanner.Export(session, target, ExportFormat.CombinedTiff, CancellationToken.None,
            options: new() { TiffCompression = TiffCompressionType.Ccitt4 }));
        Assert.That(error!.Message, Does.Contain("1-bit"));
        Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [TestCase(ExportFormat.Images, ImageOutput.Jpeg, false)]
    [TestCase(ExportFormat.Images, ImageOutput.Tiff, false)]
    [TestCase(ExportFormat.CombinedTiff, ImageOutput.Png, false)]
    [TestCase(ExportFormat.CombinedTiff, ImageOutput.Png, true)]
    public void CancellingDuringEncodingOrPublicationDiscardsAllOutput(ExportFormat format, ImageOutput imageFormat, bool atPublication)
    {
        using var session = CreateSession();
        AddPages(session, 3);
        using var scanner = new Scanner(Root);
        using var cancellation = new CancellationTokenSource();
        var target = Path.Combine(Root, "cancelled");
        var sawEncoding = false;
        Assert.ThrowsAsync<OperationCanceledException>(() => scanner.Export(session, target, format, cancellation.Token,
            progress: value =>
            {
                if (atPublication ? value.Message.StartsWith("Publishing") : value.Fraction is > 0.2 and < 0.95)
                {
                    sawEncoding = true;
                    cancellation.Cancel();
                }
            }, options: new() { ImageFormat = imageFormat }));
        Assert.That(sawEncoding, Is.True);
        Assert.That(Path.Exists(target), Is.False);
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    [TestCase(ExportFormat.Images, ImageOutput.Jpeg)]
    [TestCase(ExportFormat.Images, ImageOutput.Tiff)]
    [TestCase(ExportFormat.CombinedTiff, ImageOutput.Png)]
    public void DestinationRaceNeverOverwritesAndCleansStaging(ExportFormat format, ImageOutput imageFormat)
    {
        using var session = CreateSession();
        AddPages(session, 1);
        using var scanner = new Scanner(Root);
        var target = Path.Combine(Root, "race");
        Assert.ThrowsAsync<IOException>(() => scanner.Export(session, target, format, CancellationToken.None,
            progress: value => { if (value.Message.StartsWith("Publishing")) File.WriteAllText(target, "keep"); },
            options: new() { ImageFormat = imageFormat }));
        Assert.That(File.ReadAllText(target), Is.EqualTo("keep"));
        Assert.That(Directory.GetDirectories(Root, ".esyuscan-export-*"), Is.Empty);
    }

    internal static void AddTexture(Session session)
    {
        using var image = new SKBitmap(320, 400);
        var random = new Random(42);
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var gray = (byte)(random.Next(80) + (x + y) % 160);
                image.SetPixel(x, y, new SKColor(gray, (byte)(gray / 2), (byte)(255 - gray)));
            }
        var path = session.NextOriginalPath();
        using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(path)) encoded.SaveTo(file);
        session.Add(path);
    }

    private static double Error(SKBitmap expected, SKBitmap actual) => expected.Pixels.Zip(actual.Pixels,
        (a, b) => Math.Pow(a.Red - b.Red, 2) + Math.Pow(a.Green - b.Green, 2) + Math.Pow(a.Blue - b.Blue, 2)).Average();

    private static void AssertPixelsEqual(IMemoryImage expected, IMemoryImage actual)
    {
        using var expectedPng = expected.SaveToMemoryStream(ImageFileFormat.Png);
        using var actualPng = actual.SaveToMemoryStream(ImageFileFormat.Png);
        using var a = SKBitmap.Decode(expectedPng.ToArray());
        using var b = SKBitmap.Decode(actualPng.ToArray());
        Assert.That((b.Width, b.Height), Is.EqualTo((a.Width, a.Height)));
        Assert.That(Error(a, b), Is.Zero);
    }

    // Inspect the generated classic TIFF IFD chain, independent of the SDK decoder.
    private static List<Dictionary<ushort, uint>> ReadTiffDirectories(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        Assert.That(reader.ReadUInt16(), Is.EqualTo(0x4949), "Test host must write little-endian TIFF.");
        Assert.That(reader.ReadUInt16(), Is.EqualTo(42));
        var offset = reader.ReadUInt32();
        var pages = new List<Dictionary<ushort, uint>>();
        while (offset != 0)
        {
            Assert.That(pages.Count, Is.LessThan(100));
            reader.BaseStream.Position = offset;
            var count = reader.ReadUInt16();
            var tags = new Dictionary<ushort, uint>();
            for (var i = 0; i < count; i++)
            {
                var tag = reader.ReadUInt16();
                var type = reader.ReadUInt16();
                var length = reader.ReadUInt32();
                var value = reader.ReadUInt32();
                if (length == 1) tags[tag] = type == 3 ? value & 0xffff : value;
            }
            offset = reader.ReadUInt32();
            pages.Add(tags);
        }
        return pages;
    }
}
