using SkiaSharp;

namespace Scan.Tests;

public abstract class TestFiles
{
    protected string Root = null!;

    [NUnit.Framework.SetUp]
    public void CreateTemporaryDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "opencode", $"esyuscan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    [NUnit.Framework.TearDown]
    public void RemoveTemporaryDirectory() => Directory.Delete(Root, recursive: true);

    protected Session CreateSession() => Session.Create(Path.Combine(Root, "session"), "Example documents");

    protected static void AddPages(Session session, int count, bool detailed = false)
    {
        for (var i = 0; i < count; i++)
        {
            var path = session.NextOriginalPath();
            WriteImage(path, i + 1, detailed);
            session.Add(path);
        }
    }

    public static void WriteImage(string path, int number, bool detailed = false)
    {
        using var image = new SKBitmap(detailed ? 620 : 62, detailed ? 877 : 88);
        using var canvas = new SKCanvas(image);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColor.Parse("#45475a"), IsAntialias = true };
        if (detailed)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("DejaVu Sans"), 18);
            using var title = new SKFont(SKTypeface.FromFamilyName("DejaVu Sans"), 28);
            canvas.DrawText("PAPERWORK / STUDIO", 54, 72, SKTextAlign.Left, title, paint);
            paint.Color = SKColor.Parse("#a78bda");
            canvas.DrawRect(54, 101, 512, 3, paint);
            paint.Color = SKColor.Parse("#45475a");
            canvas.DrawText($"Example document {number:000}", 54, 158, SKTextAlign.Left, font, paint);
            canvas.DrawText("A clearer home for your documents.", 54, 201, SKTextAlign.Left, font, paint);
            paint.Color = SKColor.Parse("#d4d4dc");
            for (var row = 0; row < 12; row++) canvas.DrawRect(54, 256 + row * 30, row % 3 == 2 ? 350 : 512, 4, paint);
            canvas.DrawRect(54, 690, 512, 1, paint);
            paint.Color = SKColor.Parse("#77788b");
            canvas.DrawText("FICTIONAL SAMPLE · NO PERSONAL DATA", 54, 742, SKTextAlign.Left, font, paint);
        }
        else canvas.DrawRect(4, 5, number + 5, 12, paint);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        png.SaveTo(output);
    }
}
