using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class ThemeTests
{
    [Test]
    public void ThemeReadsCustomAccentAndSurfaces()
    {
        var palette = ThemePalette.Parse("""
            mode = "dark"
            background = "#24273a"
            dark_background = "#1e2030"
            lighter_background = "#363a4f"
            foreground = "#cad3f5"
            accent = "#a78bda" # A user override, not a stock theme name.
            """);

        Assert.That((palette.Background, palette.Surface, palette.Raised, palette.Accent),
            Is.EqualTo(("#24273a", "#1e2030", "#363a4f", "#a78bda")));
    }

    [TestCase("#eeeeee", "#111111", "#444444", "#eeeeee")]
    [TestCase("#111111", "#eeeeee", "#dddddd", "#111111")]
    public void AccentTextUsesTheMoreReadableThemeColor(string background, string foreground, string accent, string expected)
    {
        var palette = ThemePalette.Parse($"background = \"{background}\"\nforeground = \"{foreground}\"\naccent = \"{accent}\"");

        Assert.That(palette.AccentText, Is.EqualTo(expected));
    }

    [Test]
    public void InvalidPaletteDoesNotBecomeAValidDefault()
    {
        Assert.Throws<InvalidDataException>(() => ThemePalette.Parse("background = \"not a color\"\nforeground = \"#ffffff\"\naccent = \"#ff00ff\""));
    }
}
