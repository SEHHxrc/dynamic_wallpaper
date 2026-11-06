using LiveWall.Application.Importing;
using LiveWall.Application.Library;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Host.Orchestration;

/// <summary>
/// Synthetic colour content and the Renderer-child probe provider shared by explicit diagnostics.
/// Never part of the production composition root.
/// </summary>
internal static class SyntheticDiagnosticContent
{
    private static readonly string[] ProbeCapabilities = ["HwndChild", "DirectComposition"];

    public static WallpaperDefinition CreateWallpaper(string title) => new(
        new WallpaperId("diagnostic.synthetic.color"),
        "1.0.0",
        WallpaperKind.Scene,
        "synthetic-color",
        new WallpaperMetadata(title, null, null, null, new HashSet<string>()),
        new WallpaperCapabilities(false, false, false, false, 30),
        WallpaperOrigin.Native);

    public static WallpaperLibraryEntry CreateEntry(WallpaperDefinition wallpaper) => new(
        wallpaper,
        AppContext.BaseDirectory,
        new CompatibilityReport(CompatibilityGrade.Native, []));

    public static RendererProcessProvider CreateProbeProvider(
        string executablePath,
        IRendererProcessLauncher? processLauncher = null,
        Func<RendererLaunchContext, int, int, IReadOnlyDictionary<string, string>?>?
            processEnvironmentFactory = null) =>
        new(
            new RendererProcessProviderOptions(
                new RendererDescriptor(
                    new RendererId("renderer-child-dcomp-probe"),
                    "Renderer-child DirectComposition test Renderer",
                    new HashSet<WallpaperKind> { WallpaperKind.Scene },
                    new HashSet<string>(ProbeCapabilities)),
                executablePath,
                "diagnostics",
                ProbeCapabilities,
                "en-US",
                TimeSpan.FromSeconds(10)),
            processLauncher,
            processEnvironmentFactory);
}

/// <summary>Single-entry, read-only library used by diagnostics.</summary>
internal sealed class ReadOnlyDiagnosticWallpaperRepository(WallpaperLibraryEntry entry) : IWallpaperRepository
{
    public Task<WallpaperLibraryEntry?> FindAsync(WallpaperId id, CancellationToken cancellationToken) =>
        Task.FromResult<WallpaperLibraryEntry?>(id == entry.Definition.Id ? entry : null);

    public Task<IReadOnlyList<WallpaperLibraryEntry>> ListAsync(
        WallpaperQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WallpaperLibraryEntry>>([entry]);

    public Task SaveImportedAsync(ImportedWallpaper wallpaper, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Diagnostic repository is read-only.");

    public Task RemoveAsync(WallpaperId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Diagnostic repository is read-only.");
}
