using LiveWall.Application.Importing;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Application.Library;

public interface IWallpaperRepository
{
    Task<WallpaperLibraryEntry?> FindAsync(WallpaperId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<WallpaperLibraryEntry>> ListAsync(
        WallpaperQuery query,
        CancellationToken cancellationToken);

    Task SaveImportedAsync(ImportedWallpaper wallpaper, CancellationToken cancellationToken);

    Task RemoveAsync(WallpaperId id, CancellationToken cancellationToken);
}

public sealed record WallpaperLibraryEntry(
    WallpaperDefinition Definition,
    string CanonicalContentRoot,
    CompatibilityReport Compatibility);

public sealed record WallpaperQuery(
    string? Text = null,
    WallpaperKind? Kind = null,
    WallpaperOrigin? Origin = null,
    int Offset = 0,
    int Limit = 100);
