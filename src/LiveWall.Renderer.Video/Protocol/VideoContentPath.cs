using LiveWall.Contracts.RendererProtocol;

namespace LiveWall.Renderer.Video.Protocol;

internal static class VideoContentPath
{
    // Mirrors the Domain's NativeVideoFormats; the Renderer only references Contracts, so a contract test keeps them equal.
    internal static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".webm", ".mkv", ".avi", ".mov", ".m4v", ".gif" };

    public static string Resolve(LoadWallpaperPayload load)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (string.IsNullOrWhiteSpace(load.WallpaperId) || load.WallpaperId.Length > 128 ||
            load.Kind != "Video" || load.Properties is null || load.Properties.Count != 0)
            throw new InvalidDataException("Unsupported video content definition.");
        if (string.IsNullOrWhiteSpace(load.ContentRoot) || !Path.IsPathFullyQualified(load.ContentRoot) ||
            load.ContentRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(load.EntryPoint) || Path.IsPathRooted(load.EntryPoint) ||
            load.EntryPoint.Contains(':') || load.EntryPoint.Any(char.IsControl))
            throw new InvalidDataException("Video content requires a local root and relative entry point.");
        string[] parts = load.EntryPoint.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.TrimEnd(' ', '.') != part))
            throw new InvalidDataException("Ambiguous content path.");
        string root = Path.GetFullPath(load.ContentRoot);
        string path = Path.GetFullPath(Path.Combine(root, load.EntryPoint));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
            !Extensions.Contains(Path.GetExtension(path))) throw new InvalidDataException("Invalid video entry point.");
        // Reject junction/symlink traversal through the root or any ancestor, not just the leaf.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not accepted as video content.");
        return path;
    }
}
