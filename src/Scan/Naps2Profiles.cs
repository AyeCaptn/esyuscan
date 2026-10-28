using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Scan;

// Separate from SDK/GUI profile types: only esyuscan's supported settings are mapped.
// Keep the remaining XML as inert data so geometry and future options can be reviewed later.
public sealed record Naps2ProfileOption(string Name, string Xml);
public sealed record Naps2ProfileOrigin(string Name, List<string> Notices, List<Naps2ProfileOption> UnmappedOptions);
public sealed record Naps2Profile(string Name, RememberedScanSettings? Settings, Naps2ProfileOrigin Origin, string? Error)
{
    public bool CanImport => Settings is not null && Error is null;
    public string Summary => Settings is not { } s ? Error ?? "Unsupported profile" :
        $"{s.Device.Name} · {s.Device.Driver}\nDevice ID: {s.Device.Id}\n{s.Source} · {s.Color} · {s.Dpi} DPI · {s.Size}";
}

public enum ProfileDuplicatePolicy { Skip, KeepBoth, Replace }
public sealed record Naps2ImportItem(Naps2Profile Profile, ScanPreset? Preset, string Outcome);

public static class Naps2Profiles
{
    public const int MaximumCharacters = 4 * 1024 * 1024;
    private static readonly HashSet<string> Mapped = ["DisplayName", "DriverName", "Device", "PaperSource", "BitDepth", "Resolution", "PageSize"];
    private static readonly HashSet<string> Metadata = ["Version", "IconID", "IsDefault", "Caps"];

    public static IReadOnlyList<string> StandardLocations(string home, string applicationData, string? xdgConfigHome, string executableDirectory)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            paths.Add(Path.Combine(applicationData, "naps2", "profiles.xml"));
            paths.Add(Path.Combine(applicationData, "NAPS2", "profiles.xml"));
        }
        if (!string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome))
            paths.Add(Path.Combine(xdgConfigHome, "naps2", "profiles.xml"));
        if (!string.IsNullOrWhiteSpace(home)) paths.Add(Path.Combine(home, ".config", "naps2", "profiles.xml"));
        if (!string.IsNullOrWhiteSpace(executableDirectory)) paths.Add(Path.GetFullPath(Path.Combine(executableDirectory, "..", "Data", "profiles.xml")));
        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> Discover() => StandardLocations(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), AppContext.BaseDirectory).Where(File.Exists).ToArray();

    public static IReadOnlyList<Naps2Profile> ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumCharacters) throw new InvalidDataException("Profile files must be at most 4 MiB.");
        return Read(stream);
    }

    public static IReadOnlyList<Naps2Profile> Read(Stream stream)
    {
        try
        {
            // No DTDs, external entities, schemas, or executable deserialization types.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumCharacters
            });
            var document = new XDocument();
            var parents = new Stack<XElement>();
            while (reader.Read())
            {
                if (reader.Depth > 32) throw new InvalidDataException("Profile XML is nested too deeply.");
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.NamespaceURI.Length > 0) throw new InvalidDataException("Namespaced profile elements are unsupported.");
                    var element = new XElement(reader.LocalName);
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            var attributeName = reader.Name == "xmlns" ? XName.Get("xmlns") :
                                reader.Prefix == "xmlns" ? XNamespace.Xmlns + reader.LocalName : XName.Get(reader.LocalName, reader.NamespaceURI);
                            element.SetAttributeValue(attributeName, reader.Value);
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    if (parents.TryPeek(out var parent)) parent.Add(element); else document.Add(element);
                    if (!reader.IsEmptyElement) parents.Push(element);
                }
                else if (reader.NodeType == XmlNodeType.EndElement) parents.Pop();
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                {
                    if (parents.TryPeek(out var parent)) parent.Add(new XText(reader.Value));
                }
            }
            var root = document.Root ?? throw new InvalidDataException("The profile file is empty.");
            var itemName = root.Name.LocalName switch
            {
                "ArrayOfScanProfile" => "ScanProfile",
                "ArrayOfExtendedScanSettings" => "ExtendedScanSettings",
                "ArrayOfScanSettings" => "ScanSettings",
                _ => throw new InvalidDataException("Choose a NAPS2 profiles.xml file (ArrayOfScanProfile or legacy scan settings).")
            };
            var elements = root.Elements().ToArray();
            if (elements.Length > 256) throw new InvalidDataException("Import at most 256 profiles at a time.");
            if (elements.Any(e => e.Name != itemName)) throw new InvalidDataException("The profile collection contains unsupported elements.");
            return elements.Select(ParseProfile).ToArray();
        }
        catch (XmlException error) { throw new InvalidDataException($"Invalid or unsafe profile XML: {error.Message}", error); }
    }

    private static Naps2Profile ParseProfile(XElement element)
    {
        var name = element.Element("DisplayName")?.Value.Trim() ?? "(unnamed profile)";
        var notices = new List<string>();
        var unmapped = element.Elements().Where(e => !Mapped.Contains(e.Name.LocalName) && !Metadata.Contains(e.Name.LocalName))
            .Select(e => new Naps2ProfileOption(e.Name.LocalName, e.ToString(SaveOptions.DisableFormatting))).ToList();
        var origin = new Naps2ProfileOrigin(name, notices, unmapped);
        try
        {
            if (element.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate XML settings are ambiguous.");
            if (element.Element("DisplayName")?.HasElements == true || element.Element("Version")?.HasElements == true)
                throw new InvalidDataException("Profile name and version must contain single values.");
            if (element.Element("DisplayName") is null || name.Length is 0 or > 80) throw new InvalidDataException("Profile names must contain 1–80 characters.");
            var version = element.Element("Version")?.Value;
            if (version is not null && version is not ("0" or "1" or "2")) throw new InvalidDataException($"Unsupported profile version ‘{version}’.");
            var type = element.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value;
            if (type is not null && type != "ExtendedScanSettings") throw new InvalidDataException($"Unsupported profile type ‘{type}’.");
            string Value(XElement parent, string field) => parent.Element(field) is not { } child ? "" :
                child.HasElements ? throw new InvalidDataException($"{field} must contain a single value.") : child.Value.Trim();
            string WithDefault(string field, string fallback)
            {
                if (element.Element(field) is not null) return Value(element, field);
                notices.Add($"{field} is omitted; using NAPS2's default ‘{fallback}’.");
                return fallback;
            }
            var device = element.Element("Device") ?? throw new InvalidDataException("No scanner identity is saved in this profile.");
            if (device.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate device fields are ambiguous.");
            var driverName = Value(element, "DriverName");
            if (driverName.Length == 0) driverName = Value(device, "DriverName");
            var driver = driverName.ToLowerInvariant() switch
            {
                "escl" => "Escl",
                "wia" => "Wia",
                "twain" => "Twain",
                "sane" => "Sane",
                "apple" => "Apple",
                _ => throw new InvalidDataException($"Missing or unsupported driver ‘{driverName}’; esyuscan will not guess a driver.")
            };
            var id = Value(device, "ID");
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("No scanner device ID is saved; matching by name is unsafe.");
            if (driver is not ("Escl" or "Sane")) notices.Add($"{driver} identity is preserved. This esyuscan version discovers AirScan (Escl) and SANE scanners; choose a scanner explicitly to use a different driver.");
            foreach (var field in device.Elements().Where(e => e.Name.LocalName is not ("ID" or "Name" or "DriverName")))
                unmapped.Add(new("Device." + field.Name.LocalName, field.ToString(SaveOptions.DisableFormatting)));
            var source = WithDefault("PaperSource", "Glass") switch
            {
                "Glass" => "Flatbed",
                "Feeder" => "Feeder",
                "Duplex" => "Duplex",
                var invalid => throw new InvalidDataException($"Unsupported paper source ‘{invalid}’.")
            };
            var color = WithDefault("BitDepth", "C24Bit") switch
            {
                "C24Bit" => "Color",
                "Grayscale" => "Grayscale",
                "BlackWhite" => "BlackAndWhite",
                var invalid => throw new InvalidDataException($"Unsupported bit depth ‘{invalid}’.")
            };
            var resolution = WithDefault("Resolution", "Dpi200");
            if (!resolution.StartsWith("Dpi", StringComparison.Ordinal) || !int.TryParse(resolution.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var dpi)
                || dpi is < 1 or > 100000) throw new InvalidDataException($"Invalid NAPS2 resolution ‘{resolution}’.");
            var size = WithDefault("PageSize", "Letter");
            if (size is not ("A4" or "Letter"))
            {
                if (size is not ("Legal" or "A5" or "A3" or "B5" or "B4" or "Custom" or "SystemDefault"))
                    throw new InvalidDataException($"Unsupported page size ‘{size}’.");
                notices.Add($"Paper size ‘{size}’ is retained as intent; esyuscan currently offers A4 and Letter. Applying validates capabilities and explains any replacement. Custom dimensions are kept in the unmapped options.");
            }
            if (version == "1" && driver == "Twain" || element.Name == "ScanSettings" && type is null && driver == "Wia")
                notices.Add("This legacy profile used the native scanner UI; esyuscan uses the mapped settings instead.");
            foreach (var option in unmapped)
            {
                var text = option.Xml.Length > 240 ? option.Xml[..240] + "…" : option.Xml;
                notices.Add($"Not applied: {text}");
            }
            return new(name, new(new(driver, id, Value(device, "Name")), source, color, dpi, size, RequireExactDevice: true), origin, null);
        }
        catch (InvalidDataException error) { return new(name, null, origin, error.Message); }
    }

    // Plans against the whole batch so duplicates within a source file follow the same policy as existing presets.
    public static IReadOnlyList<Naps2ImportItem> Plan(IEnumerable<Naps2Profile> profiles, IEnumerable<ScanPreset> existing, ProfileDuplicatePolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var names = existing.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<Naps2ImportItem>();
        foreach (var profile in profiles)
        {
            if (!profile.CanImport) { result.Add(new(profile, null, profile.Error ?? "Unsupported profile")); continue; }
            var name = profile.Name;
            var duplicate = names.GetValueOrDefault(name);
            if (duplicate is not null && policy == ProfileDuplicatePolicy.Skip)
            {
                result.Add(new(profile, null, $"Skip: ‘{name}’ already exists."));
                continue;
            }
            if (duplicate is not null && policy == ProfileDuplicatePolicy.KeepBoth)
            {
                for (var suffix = 2; ; suffix++)
                {
                    var ending = $" ({suffix})";
                    var candidate = name[..Math.Min(name.Length, 80 - ending.Length)] + ending;
                    if (!names.ContainsKey(candidate)) { name = candidate; break; }
                }
            }
            var preset = new ScanPreset(duplicate is not null && policy == ProfileDuplicatePolicy.Replace ? duplicate.Id : Guid.NewGuid().ToString("N"),
                name, profile.Settings!, profile.Origin);
            names[name] = preset;
            result.Add(new(profile, preset, duplicate is not null && policy == ProfileDuplicatePolicy.Replace ? $"Replace ‘{name}’" : $"Import as ‘{name}’"));
        }
        return result;
    }
}
