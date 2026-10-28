using System.Text.Json;
using NAPS2.Scan;

namespace Scan;

/// <summary>Last-known identities only. Cached devices are not proof of availability or capabilities.</summary>
public sealed class ScannerCacheStore(string filePath)
{
    private sealed record Cache(int Version, List<ScannerIdentity> Devices);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; } = filePath;

    public (IReadOnlyList<ScanDevice> Devices, string Warning) Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var cache = JsonSerializer.Deserialize<Cache>(stream, Options);
            if (cache is null || cache.Version != 1 || cache.Devices is null || cache.Devices.Any(device => !Valid(device)))
                throw new InvalidDataException("Invalid or unsupported scanner cache.");
            return (cache.Devices.DistinctBy(device => (device.Driver, device.Id))
                .Select(device => new ScanDevice(Enum.Parse<Driver>(device.Driver), device.Id, device.Name)).ToArray(), "");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return ([], ""); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return ([], $"Remembered scanners could not be read: {error.Message} Discovery can still find devices.");
        }
    }

    public void Save(IEnumerable<ScanDevice> devices)
    {
        var identities = devices.Select(ScannerIdentity.From).DistinctBy(device => (device.Driver, device.Id)).ToList();
        if (identities.Any(device => !Valid(device))) throw new InvalidDataException("Cannot cache an invalid scanner identity.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".scanners-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, new Cache(1, identities), Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Valid(ScannerIdentity? device) => device is not null && device.Driver is "Escl" or "Sane"
        && !string.IsNullOrWhiteSpace(device.Id) && device.Name is not null;
}
