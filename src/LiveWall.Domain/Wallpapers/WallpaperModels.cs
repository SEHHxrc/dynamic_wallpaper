namespace LiveWall.Domain.Wallpapers;

public readonly record struct WallpaperId
{
    public WallpaperId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct PropertyPresetId
{
    public PropertyPresetId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record WallpaperDefinition(
    WallpaperId Id,
    string Version,
    WallpaperKind Kind,
    string EntryPoint,
    WallpaperMetadata Metadata,
    WallpaperCapabilities Capabilities,
    WallpaperOrigin Origin);

public sealed record WallpaperMetadata(
    string Title,
    string? Description,
    string? Author,
    string? PreviewPath,
    IReadOnlySet<string> Tags);

public sealed record WallpaperCapabilities(
    bool RequiresNetwork,
    bool RequiresAudioCapture,
    bool SupportsPointerInput,
    bool UsesSystemMetrics,
    int? PreferredFramesPerSecond);

public enum WallpaperKind
{
    Video,
    Web,
    Image,
    Scene,
    External,
}

public enum WallpaperOrigin
{
    Native,
    WallpaperEngineProject,
    WallpaperEnginePkg,
    WallpaperEngineMpkg,
    WallpaperEngineDelegate,
}
