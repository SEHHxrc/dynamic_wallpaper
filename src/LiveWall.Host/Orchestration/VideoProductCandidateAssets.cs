using System.Security.Cryptography;
using System.Text.Json;
using LiveWall.Application.Library;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.FileSystem;

namespace LiveWall.Host.Orchestration;

// Diagnostic fixture only. This is neither a general content importer nor C2's library.
public sealed class VideoProductCandidateAssets
{
    public const string LibrarySha256 = "04E4D27DC385DC18DDD93FBE69C75E635B864F3C6EC53A9122DF54C2F7842F4A";
    public const string MediaSha256 = "8CADEB631BE8B6245B43CFEDC6B9F43F42C750AE4C144E83A9598D3DABADD68C";
    public WallpaperLibraryEntry Entry { get; }
    public string LibraryPath { get; }
    public string ManifestSha256 { get; }

    private VideoProductCandidateAssets(WallpaperLibraryEntry entry, string libraryPath, string manifestHash)
    { Entry = entry; LibraryPath = libraryPath; ManifestSha256 = manifestHash; }

    public static VideoProductCandidateAssets Load(string contentRoot, string libraryPath)
    {
        if (!Path.IsPathFullyQualified(contentRoot) || !Path.IsPathFullyQualified(libraryPath) ||
            contentRoot.StartsWith(@"\\", StringComparison.Ordinal) || libraryPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Video diagnostics require absolute local asset paths.");
        RejectReparseAncestors(Path.GetFullPath(contentRoot));
        RejectReparseAncestors(Path.GetFullPath(libraryPath));
        CanonicalPathResolver paths = new(contentRoot);
        string manifestPath = paths.ResolvePackagePath("manifest.json");
        if (new FileInfo(manifestPath).Length > 65536) throw new InvalidDataException("Diagnostic manifest is too large.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        WallpaperDefinition wallpaper = ReadFixtureDefinition(document.RootElement);
        if (Hash(paths.ResolvePackagePath("content/motion.avi")) != MediaSha256 || Hash(libraryPath) != LibrarySha256)
            throw new InvalidDataException("Diagnostic media or native library hash mismatch.");
        return new(new WallpaperLibraryEntry(wallpaper, paths.RootPath, new CompatibilityReport(CompatibilityGrade.Native, [])),
            Path.GetFullPath(libraryPath), Hash(manifestPath));
    }

    internal static WallpaperDefinition ReadFixtureDefinition(JsonElement manifest)
    {
        if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
            manifest.GetProperty("id").GetString() != "livewall.diagnostic.video-c0" ||
            manifest.GetProperty("version").GetString() != "1.0.0" ||
            manifest.GetProperty("type").GetString() != "video" ||
            manifest.GetProperty("entry").GetString() != "content/motion.avi" ||
            manifest.GetProperty("defaults").GetProperty("fit").GetString() != "cover" ||
            !manifest.GetProperty("defaults").GetProperty("muted").GetBoolean())
            throw new InvalidDataException("Only the original, muted cover video fixture is accepted.");
        foreach (string permission in new[] { "network", "audioCapture", "pointerInput", "systemMetrics" })
            if (manifest.GetProperty("permissions").GetProperty(permission).GetBoolean())
                throw new InvalidDataException("The video fixture cannot request extra permissions.");
        return new(new WallpaperId("livewall.diagnostic.video-c0"), "1.0.0", WallpaperKind.Video,
            "content/motion.avi", new WallpaperMetadata("LiveWall original motion fixture", null, null, null, new HashSet<string>()),
            new WallpaperCapabilities(false, false, false, false, 24), WallpaperOrigin.Native);
    }

    internal RendererProcessProvider CreateProvider(string executablePath, string? evidencePath = null) => new(
        new RendererProcessProviderOptions(DefaultRendererProviders.VideoDescriptor,
            executablePath, "diagnostics-video", DefaultRendererProviders.VideoCapabilities, "zh-CN", DefaultRendererProviders.VideoHandshakeTimeout),
        processEnvironmentFactory: (_, _, _) => evidencePath is null
            ? new Dictionary<string, string> { ["LIVEWALL_VIDEO_LIBMPV_PATH"] = LibraryPath }
            : new Dictionary<string, string> { ["LIVEWALL_VIDEO_LIBMPV_PATH"] = LibraryPath,
                ["LIVEWALL_VIDEO_CONTROL_EVIDENCE"] = evidencePath });

    private static string Hash(string path) { using FileStream stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse paths are not accepted for diagnostic assets.");
    }
}
