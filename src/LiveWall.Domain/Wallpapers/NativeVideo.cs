using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace LiveWall.Domain.Wallpapers;

/// <summary>
/// Entry extensions a native video wallpaper may use. The Video Renderer checks the same set on its own;
/// a contract test keeps the two equal.
/// </summary>
public static class NativeVideoFormats
{
    public static IReadOnlySet<string> EntryExtensions { get; } =
        new[] { ".mp4", ".webm", ".mkv", ".avi", ".mov", ".m4v", ".gif" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Content-addressed identity for a local video imported without a package: the id carries the media
/// SHA-256, so the same bytes always map to the same wallpaper and a second import is a duplicate.
/// The prefix is reserved; the library only accepts it when the entry media really has that hash.
/// </summary>
public static class LocalVideoIdentity
{
    public const string Prefix = "local.video.";
    public const string Version = "1.0.0";
    private const string EntryStem = "content/video";

    public static WallpaperId FromMediaHash(string sha256Hex)
    {
        if (!IsSha256Hex(sha256Hex))
        {
            throw new ArgumentException("A SHA-256 hex digest is required.", nameof(sha256Hex));
        }

        return new WallpaperId(Prefix + sha256Hex.ToLowerInvariant());
    }

    public static bool IsReserved(WallpaperId id) => id.Value.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryGetMediaHash(WallpaperId id, [NotNullWhen(true)] out string? sha256Hex)
    {
        string suffix = IsReserved(id) ? id.Value[Prefix.Length..] : string.Empty;
        sha256Hex = IsSha256Hex(suffix) && suffix.All(character => !char.IsUpper(character)) ? suffix : null;
        return sha256Hex is not null;
    }

    public static string EntryPointFor(string extension)
    {
        if (!NativeVideoFormats.EntryExtensions.Contains(extension))
        {
            throw new ArgumentException("Unsupported video extension.", nameof(extension));
        }

        return EntryStem + extension.ToLowerInvariant();
    }

    public static bool IsEntryPoint(string entryPoint) =>
        entryPoint.StartsWith(EntryStem, StringComparison.Ordinal) &&
        entryPoint[EntryStem.Length..] is { } extension &&
        NativeVideoFormats.EntryExtensions.Contains(extension) &&
        !extension.Any(char.IsAsciiLetterUpper);

    private static bool IsSha256Hex(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
