using System.Text.Json;
using System.Text.RegularExpressions;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Infrastructure.Packages;

/// <summary>
/// Strict reader for wallpaper.json v1. Mirrors packages/schemas/wallpaper.schema.json and treats
/// every manifest as untrusted input. It reads no files beyond the bytes it is given.
/// </summary>
public static partial class WallpaperManifestReader
{
    public const string FileName = "wallpaper.json";
    public const int MaximumManifestBytes = 64 * 1024;

    private const int MaximumEntryLength = 512;
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "id", "version", "title", "description", "author",
        "type", "entry", "preview", "permissions", "defaults",
    };
    private static readonly HashSet<string> PermissionProperties = new(StringComparer.Ordinal)
    {
        "network", "audioCapture", "pointerInput", "systemMetrics",
    };

    public static bool IsValidId(string? value) =>
        value is { Length: <= 128 } && IdPattern().IsMatch(value);

    public static bool IsValidVersion(string? value) =>
        value is { Length: <= 64 } && VersionPattern().IsMatch(value);

    /// <exception cref="InvalidDataException">The manifest violates the v1 format.</exception>
    public static WallpaperManifest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumManifestBytes)
        {
            throw Invalid("Manifest exceeds the size limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json);
            return Read(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Manifest is not valid JSON.", exception);
        }
    }

    private static WallpaperManifest Read(JsonElement root)
    {
        RequireExactProperties(root, RootProperties, required:
            ["schemaVersion", "id", "version", "title", "type", "entry", "permissions", "defaults"]);

        JsonElement schemaVersion = root.GetProperty("schemaVersion");
        if (schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out int version) || version != 1)
        {
            throw Invalid("Unsupported manifest schemaVersion.");
        }

        string id = RequireString(root, "id");
        if (!IsValidId(id))
        {
            throw Invalid("Manifest id is invalid.");
        }

        string wallpaperVersion = RequireString(root, "version");
        if (!IsValidVersion(wallpaperVersion))
        {
            throw Invalid("Manifest version is invalid.");
        }

        string title = RequireString(root, "title");
        if (title.Length is < 1 or > 200)
        {
            throw Invalid("Manifest title length is invalid.");
        }

        string? description = OptionalString(root, "description", 2000);
        string? author = OptionalString(root, "author", 200);
        WallpaperKind kind = RequireString(root, "type") switch
        {
            "video" => WallpaperKind.Video,
            "web" => WallpaperKind.Web,
            "image" => WallpaperKind.Image,
            _ => throw Invalid("Manifest type is not directly runnable in format v1."),
        };
        string entry = RequireString(root, "entry");
        ValidateEntry(entry, kind);

        string? preview = OptionalString(root, "preview", 16);
        if (preview is not null && !PreviewPattern().IsMatch(preview))
        {
            throw Invalid("Manifest preview is invalid.");
        }

        JsonElement permissions = RequireObject(root, "permissions");
        RequireExactProperties(permissions, PermissionProperties, [.. PermissionProperties]);
        JsonElement defaults = RequireObject(root, "defaults");
        if (RequireString(defaults, "fit") is not ("cover" or "contain" or "stretch" or "center"))
        {
            throw Invalid("Manifest default fit is invalid.");
        }

        JsonElement fpsLimit = RequireProperty(defaults, "fpsLimit");
        if (fpsLimit.ValueKind != JsonValueKind.Number ||
            !fpsLimit.TryGetInt32(out int framesPerSecond) || framesPerSecond is < 1 or > 240)
        {
            throw Invalid("Manifest fpsLimit is invalid.");
        }

        _ = RequireBoolean(defaults, "muted");
        return new WallpaperManifest(
            new WallpaperId(id),
            wallpaperVersion,
            kind,
            entry,
            new WallpaperMetadata(title, description, author, preview, new HashSet<string>()),
            new WallpaperCapabilities(
                RequireBoolean(permissions, "network"),
                RequireBoolean(permissions, "audioCapture"),
                RequireBoolean(permissions, "pointerInput"),
                RequireBoolean(permissions, "systemMetrics"),
                framesPerSecond));
    }

    private static void ValidateEntry(string entry, WallpaperKind kind)
    {
        string requiredRoot = kind == WallpaperKind.Web ? "web/" : "content/";
        if (entry.Length > MaximumEntryLength ||
            !entry.StartsWith(requiredRoot, StringComparison.Ordinal) ||
            entry.Length == requiredRoot.Length ||
            entry.Contains('\\') || entry.Contains(':') || entry.Any(char.IsControl))
        {
            throw Invalid("Manifest entry is invalid.");
        }

        foreach (string segment in entry.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw Invalid("Manifest entry contains an unsafe segment.");
            }
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        HashSet<string> allowed,
        IReadOnlyList<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Manifest object expected.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw Invalid("Manifest contains an unknown or duplicate property.");
            }
        }

        if (required.Any(name => !seen.Contains(name)))
        {
            throw Invalid("Manifest is missing a required property.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value))
        {
            throw Invalid("Manifest is missing a required property.");
        }

        return value;
    }

    private static JsonElement RequireObject(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        return value.ValueKind == JsonValueKind.Object ? value : throw Invalid("Manifest object expected.");
    }

    private static string RequireString(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid("Manifest string expected.");
    }

    private static bool RequireBoolean(JsonElement element, string name) =>
        RequireProperty(element, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid("Manifest boolean expected."),
        };

    private static string? OptionalString(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximumLength)
        {
            throw Invalid("Manifest optional string is invalid.");
        }

        return value.GetString();
    }

    private static InvalidDataException Invalid(string message) => new(message);

    // \z instead of $: .NET's $ also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^preview\.(?:webp|png|jpg|jpeg)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PreviewPattern();
}

public sealed record WallpaperManifest(
    WallpaperId Id,
    string Version,
    WallpaperKind Kind,
    string EntryPoint,
    WallpaperMetadata Metadata,
    WallpaperCapabilities Capabilities)
{
    public WallpaperDefinition ToDefinition(WallpaperOrigin origin) =>
        new(Id, Version, Kind, EntryPoint, Metadata, Capabilities, origin);
}
