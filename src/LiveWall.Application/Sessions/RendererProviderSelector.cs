using LiveWall.Domain.Wallpapers;

namespace LiveWall.Application.Sessions;

public sealed class RendererProviderSelector
{
    private readonly IReadOnlyList<IRendererProvider> providers;

    public RendererProviderSelector(IEnumerable<IRendererProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers.ToArray();
    }

    public IRendererProvider Select(
        WallpaperDefinition wallpaper,
        RuntimeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(environment);

        var candidates = providers
            .Select(provider => new { Provider = provider, Match = provider.Match(wallpaper, environment) })
            .Where(candidate => candidate.Match.IsMatch)
            .OrderByDescending(candidate => candidate.Match.Priority)
            .ThenBy(candidate => candidate.Provider.Descriptor.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return candidates.Length > 0
            ? candidates[0].Provider
            : throw new InvalidOperationException($"No renderer supports wallpaper '{wallpaper.Id}'.");
    }
}
