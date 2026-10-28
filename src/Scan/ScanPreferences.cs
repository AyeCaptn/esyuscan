using System.Text.Json;
using NAPS2.Scan;

namespace Scan;

// esyuscan preferences are independent of session manifests and never write NAPS2 source files.
public sealed record ScannerIdentity(string Driver, string Id, string Name)
{
    public static ScannerIdentity From(ScanDevice device) => new(device.Driver.ToString(), device.ID, device.Name);
    public bool Matches(ScanDevice device) => Driver == device.Driver.ToString() && Id == device.ID;
}

public sealed record RememberedScanSettings(ScannerIdentity Device, string Source, string Color, int Dpi, string Size, ScanGeometry? Geometry = null, bool RequireExactDevice = false);
public sealed record ScanPreset(string Id, string Name, RememberedScanSettings Settings, Naps2ProfileOrigin? ImportedProfile = null)
{
    public override string ToString() => Name;
}
public sealed record ScanPreferences(int Version, RememberedScanSettings? LastScan, List<ScanPreset> Presets, List<ScannerIdentity>? ManualDevices = null)
{
    public static ScanPreferences Empty => new(1, null, []);
}

public sealed class ScanPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }

    public ScanPreferencesStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "esyuscan", "preferences.json");

    public (ScanPreferences Preferences, string Warning) Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var preferences = JsonSerializer.Deserialize<ScanPreferences>(stream, JsonOptions);
            Validate(preferences);
            return (preferences!, "");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ScanPreferences.Empty, "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return (ScanPreferences.Empty, $"Saved scan preferences could not be read: {error.Message} Using defaults; the file is kept until you save a setting.");
        }
    }

    public void Save(ScanPreferences preferences)
    {
        Validate(preferences);
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".preferences-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, preferences, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ValidManual(ScannerIdentity? identity)
    {
        if (identity is null || identity.Name is null || string.IsNullOrWhiteSpace(identity.Id) || !Enum.TryParse<Driver>(identity.Driver, out var driver)
            || identity.Driver != driver.ToString()) return false;
        try { return ManualScannerAddress.Create(driver, identity.Id, identity.Name).ID == identity.Id; }
        catch (ArgumentException) { return false; }
    }

    private static void Validate(ScanPreferences? preferences)
    {
        static bool ValidOrigin(Naps2ProfileOrigin? origin) => origin is null ||
            !string.IsNullOrWhiteSpace(origin.Name) && origin.Notices is not null && origin.Notices.All(n => n is not null)
            && origin.UnmappedOptions is not null && origin.UnmappedOptions.All(o => o is not null
                && !string.IsNullOrWhiteSpace(o.Name) && o.Xml is not null);
        static bool ValidSettings(RememberedScanSettings? settings) => settings is not null
            && settings.Device is not null && !string.IsNullOrWhiteSpace(settings.Device.Driver)
            && !string.IsNullOrWhiteSpace(settings.Device.Id) && settings.Device.Name is not null
            && settings.Source is not null && settings.Color is not null && settings.Size is not null
            && (settings.Geometry is null || settings.Geometry.IsValid);
        if (preferences is null || preferences.Version != 1 || preferences.Presets is null
            || preferences.LastScan is not null && !ValidSettings(preferences.LastScan)
            || preferences.Presets.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)
                || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80 || !ValidSettings(p.Settings) || !ValidOrigin(p.ImportedProfile))
            || preferences.ManualDevices is not null && preferences.ManualDevices.Any(d => !ValidManual(d))
            || preferences.Presets.Select(p => p.Id).Distinct().Count() != preferences.Presets.Count
            || preferences.Presets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != preferences.Presets.Count)
            throw new InvalidDataException("Invalid or unsupported esyuscan preferences.");
    }
}
