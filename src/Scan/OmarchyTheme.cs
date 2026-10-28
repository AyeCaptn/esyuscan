using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Scan;

public sealed record ThemePalette(string Background, string Surface, string Raised, string Foreground,
    string Secondary, string Border, string Accent, string Selection, string Error, bool Light)
{
    public static ThemePalette Default { get; } = new("#24273a", "#1e2030", "#363a4f", "#cad3f5",
        "#b8c0e0", "#494d64", "#a78bfa", "#494d64", "#ed8796", false);

    public static ThemePalette Parse(string toml)
    {
        var values = Regex.Matches(toml, "(?m)^\\s*([a-z_]+)\\s*=\\s*\"([^\"]+)\"")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
        string Get(string key, string fallback)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            return Color.TryParse(value, out _) ? value : throw new InvalidDataException($"Invalid theme color: {key}");
        }
        if (!values.ContainsKey("background") || !values.ContainsKey("foreground") || !values.ContainsKey("accent"))
            throw new InvalidDataException("The Omarchy palette needs background, foreground, and accent colors.");
        var background = Get("background", Default.Background);
        var foreground = Get("foreground", Default.Foreground);
        var light = values.TryGetValue("mode", out var mode) ? mode == "light" : Luminance(Color.Parse(background)) > 0.4;
        return new(background, Get("dark_background", background), Get("lighter_background", background),
            foreground, Get("light_foreground", foreground), Get("muted", foreground), Get("accent", Default.Accent),
            Get("selection", Default.Selection), Get("red", Default.Error), light);
    }

    public string AccentText => Contrast(Accent, Background) >= Contrast(Accent, Foreground) ? Background : Foreground;

    private static double Contrast(string first, string second)
    {
        var a = Luminance(Color.Parse(first));
        var b = Luminance(Color.Parse(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}

public sealed class OmarchyTheme : IDisposable
{
    private readonly string home;
    private readonly DispatcherTimer timer;
    private string? lastPalette;
    private string? lastFont;
    private string? lastRounding;
    private bool refreshing;
    private int ticks;

    public OmarchyTheme(string home)
    {
        this.home = home;
        // Poll the durable path, not a theme file inode: Omarchy replaces the entire theme directory.
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            Dispatcher.UIThread, async (_, _) => await Refresh());
        timer.Stop();
    }

    public event Action<string>? StatusChanged;
    public void Start() { timer.Start(); _ = Refresh(); }

    public async Task Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var path = Path.Combine(home, ".local/state/omarchy/current/theme/colors.toml");
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path);
                if (text != lastPalette)
                {
                    Apply(ThemePalette.Parse(text));
                    lastPalette = text;
                    StatusChanged?.Invoke("Following Omarchy");
                }
            }
            else if (lastPalette is null) StatusChanged?.Invoke("Default palette · Omarchy theme unavailable");

            if (ticks++ % 5 != 0) return;
            var font = await ReadCommand("fc-match", "monospace", "-f", "%{family}");
            font = font?.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(font) && font != lastFont)
            {
                Application.Current!.Resources["AppFont"] = new FontFamily(font);
                lastFont = font;
            }
            var rounding = await ReadCommand("hyprctl", "-j", "getoption", "decoration:rounding");
            if (rounding is not null && rounding != lastRounding)
            {
                using var document = JsonDocument.Parse(rounding);
                var radius = document.RootElement.GetProperty("int").GetInt32();
                Application.Current!.Resources["AppRadius"] = new CornerRadius(Math.Clamp(radius, 0, 32));
                lastRounding = rounding;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            StatusChanged?.Invoke($"Theme update: {error.Message}");
        }
        finally { refreshing = false; }
    }

    public static void Apply(ThemePalette palette)
    {
        var app = Application.Current!;
        app.RequestedThemeVariant = palette.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        void Brush(string key, string color) => app.Resources[key] = new SolidColorBrush(Color.Parse(color));
        Brush("BackgroundBrush", palette.Background);
        Brush("SurfaceBrush", palette.Surface);
        Brush("RaisedBrush", palette.Raised);
        Brush("ForegroundBrush", palette.Foreground);
        Brush("SecondaryBrush", palette.Secondary);
        Brush("BorderBrush", palette.Border);
        Brush("AccentBrush", palette.Accent);
        Brush("AccentTextBrush", palette.AccentText);
        Brush("SelectionBrush", palette.Selection);
        Brush("ErrorBrush", palette.Error);
        Brush("ThemeBackgroundBrush", palette.Surface);
        Brush("ThemeForegroundBrush", palette.Foreground);
        Brush("ThemeBorderLowBrush", palette.Raised);
        Brush("ThemeBorderMidBrush", palette.Border);
        Brush("ThemeBorderHighBrush", palette.Accent);
        Brush("ThemeControlLowBrush", palette.Surface);
        Brush("ThemeControlMidBrush", palette.Raised);
        Brush("ThemeControlHighBrush", palette.Selection);
        Brush("HighlightBrush", palette.Accent);
        Brush("HighlightForegroundBrush", palette.AccentText);
    }

    private static async Task<string?> ReadCommand(string command, params string[] arguments)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); return null; }
            await error;
            return process.ExitCode == 0 ? await output : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public void Dispose() => timer.Stop();
}
