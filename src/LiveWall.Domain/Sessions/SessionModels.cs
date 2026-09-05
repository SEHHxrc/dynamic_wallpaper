using LiveWall.Domain.Displays;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Domain.Sessions;

public readonly record struct SessionId
{
    public SessionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct RendererId
{
    public RendererId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record WallpaperSession(
    SessionId Id,
    WallpaperId WallpaperId,
    IReadOnlyList<DisplayId> Displays,
    RendererId RendererId,
    PlaybackState State,
    long Generation);
