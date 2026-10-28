using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class SessionTests : TestFiles
{
    [Test]
    public void OpeningSessionRestoresPageOrderAndRotation()
    {
        string[] names;
        using (var session = CreateSession())
        {
            AddPages(session, 2);
            names = session.Pages.Select(page => page.FileName).ToArray();
            session.Rotate(0);
            session.Move(0, 1);
        }

        using var recovered = Session.Open(Path.Combine(Root, "session"));

        Assert.That(recovered.Pages, Is.EqualTo(new[] { new SessionPage(names[1]), new SessionPage(names[0], 90) }));
    }

    [Test]
    public void RemovingAndUndoingRestoresThePage()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var page = session.Pages[0];
        session.Remove(0);

        session.Undo();

        Assert.That(session.Pages, Is.EqualTo(new[] { page }));
    }

    [Test]
    public void RedoRepeatsTheUndoneRotation()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.Rotate(0);
        session.Undo();

        session.Redo();

        Assert.That(session.Pages[0].Rotation, Is.EqualTo(90));
    }

    [Test]
    public void EditingPreservesOriginalBytes()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        var path = session.OriginalPath(session.Pages[0]);
        var originalHash = SHA256.HashData(File.ReadAllBytes(path));

        session.Rotate(0);
        session.Remove(0);

        Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(originalHash));
    }

    [Test]
    public void OpeningAnAlreadyOpenSessionIsRejected()
    {
        using var session = CreateSession();

        Assert.Throws<IOException>(() => Session.Open(session.DirectoryPath).Dispose());
    }

    [Test]
    public void NewSessionDoesNotOverwriteExistingFiles()
    {
        var folder = Path.Combine(Root, "existing");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "Keep me");

        Assert.Throws<IOException>(() => Session.Create(folder, "Documents").Dispose());
    }

    [Test]
    public void TraversalInManifestIsRejected()
    {
        var folder = Path.Combine(Root, "session");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "session.json"), """
            {"Version":1,"Name":"Invalid","Pages":[{"FileName":"../outside.png","Rotation":0}]}
            """);

        Assert.Throws<InvalidDataException>(() => Session.Open(folder).Dispose());
    }

    [Test]
    public void FourRotationsReturnToOriginalOrientation()
    {
        using var session = CreateSession();
        AddPages(session, 1);

        for (var i = 0; i < 4; i++) session.Rotate(0);

        Assert.That(session.Pages[0].Rotation, Is.EqualTo(0));
    }

    [Test]
    public void EditingAfterUndoClearsRedo()
    {
        using var session = CreateSession();
        AddPages(session, 1);
        session.Rotate(0);
        session.Undo();

        session.Rotate(0);

        Assert.That(session.CanRedo, Is.False);
    }

    [Test]
    public void LegacyManifestWithoutPdfFieldsStillOpensAndSavesAsVersionOne()
    {
        using (var session = CreateSession()) AddPages(session, 1);
        var directory = Path.Combine(Root, "session");
        var manifestPath = Path.Combine(directory, "session.json");
        var original = Directory.GetFiles(Path.Combine(directory, "originals")).Single();
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            Version = 1,
            Name = "Legacy",
            Pages = new[] { new { FileName = Path.GetFileName(original), Rotation = 90 } }
        }));

        using var reopened = Session.Open(directory);
        reopened.Rotate(0);

        Assert.That(reopened.Pages.Single().PdfSource, Is.Null);
        Assert.That(reopened.Pages.Single().Rotation, Is.EqualTo(180));
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.That(json.RootElement.GetProperty("Version").GetInt32(), Is.EqualTo(1));
    }

    [TestCase("../outside.pdf", 0)]
    [TestCase("source.pdf", -1)]
    [TestCase("source.png", 0)]
    public void InvalidPdfSourceReferencesAreRejected(string fileName, int pageIndex)
    {
        using (var session = CreateSession()) AddPages(session, 1);
        var directory = Path.Combine(Root, "session");
        var original = Directory.GetFiles(Path.Combine(directory, "originals")).Single();
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(
            new SessionManifest(2, "Invalid", [new SessionPage(Path.GetFileName(original), PdfSource: new(fileName, pageIndex))])));

        Assert.Throws<InvalidDataException>(() => Session.Open(directory).Dispose());
    }

    [Test]
    public void MissingPdfSourceFailsExplicitlyInsteadOfFallingBackToPreview()
    {
        using (var session = CreateSession())
        {
            var source = session.NextPdfSourcePath();
            File.WriteAllText(source, "Source placeholder");
            var image = session.NextOriginalPath();
            WriteImage(image, 1);
            session.Add(image, new(Path.GetFileName(source), 0));
            File.Delete(source);
        }

        var error = Assert.Throws<FileNotFoundException>(() => Session.Open(Path.Combine(Root, "session")).Dispose());

        Assert.That(error!.Message, Does.Contain("PDF source is missing"));
    }
}
