using System.Globalization;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Host.Orchestration;

/// <summary>Production Renderer set: side-by-side executables only, never a caller-supplied path.</summary>
internal static class DefaultRendererProviders
{
    internal const string VideoExecutableName = "LiveWall.Renderer.Video.exe";
    private const string FallbackLocale = "en-US";

    internal static RendererDescriptor VideoDescriptor { get; } = new(
        new RendererId("video"),
        "LiveWall Video Renderer",
        new HashSet<WallpaperKind> { WallpaperKind.Video },
        new HashSet<string>(VideoCapabilities));

    internal static IReadOnlyList<string> VideoCapabilities => ["HwndChild", "DirectComposition", "Video"];

    internal static TimeSpan VideoHandshakeTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// A missing executable yields no provider, so applying video content fails closed. libmpv is
    /// resolved and hash-checked by the Renderer itself next to its own executable.
    /// </summary>
    public static IReadOnlyList<IRendererProvider> Create(string installationScope, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string executablePath = Path.Combine(Path.GetFullPath(baseDirectory), VideoExecutableName);
        if (!File.Exists(executablePath))
        {
            return [];
        }

        return
        [
            new RendererProcessProvider(new RendererProcessProviderOptions(
                VideoDescriptor,
                executablePath,
                installationScope,
                VideoCapabilities,
                ResolveLocale(CultureInfo.CurrentUICulture),
                VideoHandshakeTimeout)),
        ];
    }

    // InvariantCulture has an empty name, which the Provider rejects.
    internal static string ResolveLocale(CultureInfo culture) =>
        string.IsNullOrWhiteSpace(culture.Name) ? FallbackLocale : culture.Name;
}
