using System.Text.Json;
using NAPS2.Images;
using NAPS2.Pdf;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class ExportValidationTests
{
    [Test]
    public void SecretsHaveNoGeneratedStringOrJsonRepresentation()
    {
        var secret = Guid.NewGuid().ToString("N");
        var options = new ExportOptions { Passwords = new() { Open = secret, Owner = Guid.NewGuid().ToString("N") } };
        Assert.That(options.ToString(), Does.Not.Contain(secret));
        Assert.That(options.Passwords.ToString(), Does.Not.Contain(secret));
        Assert.That(JsonSerializer.Serialize(options), Does.Not.Contain(secret).And.Not.Contain("Passwords"));
        Assert.That(JsonSerializer.Serialize(options.Passwords), Is.EqualTo("{}"));
    }

    [TestCase("invalid-format")]
    [TestCase("invalid-quality")]
    [TestCase("irrelevant-quality")]
    [TestCase("irrelevant-tiff")]
    [TestCase("image-metadata")]
    [TestCase("image-ocr")]
    [TestCase("pdfa-password")]
    [TestCase("pdfa-source")]
    [TestCase("pdfa-xmp")]
    [TestCase("no-owner")]
    [TestCase("same-password")]
    [TestCase("long-password")]
    [TestCase("unicode-password")]
    [TestCase("permissions-without-encryption")]
    [TestCase("contradictory-printing")]
    public void ConflictingSettingsFailBeforeExport(string scenario)
    {
        var secret = Guid.NewGuid().ToString("N");
        var options = scenario switch
        {
            "invalid-format" => new ExportOptions { ImageFormat = (ImageOutput)99 },
            "invalid-quality" => new() { PdfCompression = PdfImageCompression.Jpeg, JpegQuality = 101 },
            "irrelevant-quality" => new() { JpegQuality = 75 },
            "irrelevant-tiff" => new() { TiffCompression = TiffCompressionType.Lzw },
            "image-metadata" => new() { Metadata = new() { Title = "Title" } },
            "pdfa-password" => new() { Compatibility = PdfCompat.PdfA1B, Passwords = new() { Owner = secret } },
            "pdfa-xmp" => new() { Compatibility = PdfCompat.PdfA2B, Metadata = new() { Author = "A & B" } },
            "pdfa-source" => new() { Compatibility = PdfCompat.PdfA3B },
            "no-owner" => new() { Passwords = new() { Open = secret } },
            "same-password" => new() { Passwords = new() { Open = secret, Owner = secret } },
            "long-password" => new() { Passwords = new() { Owner = secret + "extra" } },
            "unicode-password" => new() { Passwords = new() { Owner = secret[..20] + "é" } },
            "permissions-without-encryption" => new() { Permissions = new() { Copying = false } },
            "contradictory-printing" => new() { Passwords = new() { Owner = secret }, Permissions = new() { Printing = false, FullQualityPrinting = true } },
            _ => new()
        };
        var error = Assert.Throws<ArgumentException>(() => options.Validate(
            scenario is "image-metadata" or "image-ocr" ? ExportFormat.Images : ExportFormat.CombinedPdf,
            scenario == "image-ocr" ? new("eng") : null, hasPreservedPdf: scenario == "pdfa-source"));
        Assert.That(error!.ToString(), Does.Not.Contain(secret));
    }

    [TestCase("--quality", "wrong")]
    [TestCase("--pdfa", "4b")]
    [TestCase("--tiff-compression", "jpeg")]
    [TestCase("--unknown", "value")]
    [TestCase("--deny", "unknown")]
    public void CliRejectsInvalidOrUnsupportedSettingsWithoutEchoingValues(string option, string value)
    {
        var error = Assert.Throws<ArgumentException>(() => ExportCommandOptions.Parse(["export", "session", "--format", "pdf", "--output", "out", option, value]));
        Assert.That(error!.Message, Does.Not.Contain(value));
    }

    [TestCase("--pdfa", "none")]
    [TestCase("--title", "hello")]
    [TestCase("--pdf-compression", "lossless")]
    public void CliRefusesExplicitPdfOptionsEvenWhenDefaultForImages(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => ExportCommandOptions.Parse(["export", "session", "--format", "images", "--output", "out", name, value]));
    }

    [Test]
    public void CliReadsCredentialsOnlyFromSelectedEnvironmentVariablesAndMapsEveryPermission()
    {
        var owner = Guid.NewGuid().ToString("N");
        var user = Guid.NewGuid().ToString("N");
        var export = ExportCommandOptions.Parse(["export", "session", "--format", "pdfs", "--output", "out", "--pdf-owner-password-env", "TEST_OWNER",
            "--pdf-open-password-env", "TEST_OPEN", "--deny", "print,copy,accessibility,modify,annotate,forms,assemble", "--title", "Receipt"],
            key => key switch { "TEST_OWNER" => owner, "TEST_OPEN" => user, _ => null });
        Assert.That(export.Options.Passwords!.Owner, Is.EqualTo(owner));
        Assert.That(export.Options.Passwords.Open, Is.EqualTo(user));
        Assert.That(export.Options.Permissions, Is.EqualTo(new PdfPermissions
        {
            Printing = false,
            FullQualityPrinting = false,
            Copying = false,
            Accessibility = false,
            Modification = false,
            Annotations = false,
            FormFilling = false,
            Assembly = false
        }));
        Assert.That(export.Options.Metadata.Title, Is.EqualTo("Receipt"));
    }

    [Test]
    public void CliMissingDuplicateAndUnpopulatedPasswordOptionsAreActionable()
    {
        Assert.That(Assert.Throws<ArgumentException>(() => ExportCommandOptions.Parse(["export", "session", "--format", "pdf", "--output"]))!.Message, Does.Contain("Missing value"));
        Assert.That(Assert.Throws<ArgumentException>(() => ExportCommandOptions.Parse(["export", "session", "--format", "pdf", "--format", "pdf", "--output", "out"]))!.Message, Does.Contain("Duplicate"));
        Assert.That(Assert.Throws<ArgumentException>(() => ExportCommandOptions.Parse(["export", "session", "--format", "pdf", "--output", "out", "--pdf-owner-password-env", "TEST_OWNER"], _ => null))!.Message, Does.Contain("unset or empty"));
    }
}
