using LiveWall.Application.Layouts;
using LiveWall.Application.Library;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Application.Configuration;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.Configuration;
using LiveWall.Infrastructure.Persistence;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.Host.Bootstrap;

internal static class WindowsHostCompositionRoot
{
    public static WindowsHostRuntime Create(string configurationDirectory, string installationScope) =>
        CreateComposition(
            configurationDirectory,
            CreateProductionOptions(configurationDirectory, installationScope)).Runtime;

    private static WindowsHostCompositionOptions CreateProductionOptions(
        string configurationDirectory,
        string installationScope) =>
        new(
            new JsonWallpaperRepository(Path.Combine(configurationDirectory, "library")),
            DefaultRendererProviders.Create(installationScope, AppContext.BaseDirectory),
            DesktopAttachmentPolicy.Production);

    internal static WindowsHostComposition CreateComposition(
        string configurationDirectory,
        WindowsHostCompositionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
        ArgumentNullException.ThrowIfNull(options);

        WindowsDisplayTopologySource displayTopologySource = new();
        ShellTopologySnapshot shellTopology = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        WindowsBuildIdentity build = shellTopology.BuildIdentity;
        WindowsShellSnapshot shell = new(
            build.Version,
            build.Build,
            RaisedDesktopEnabled: true,
            build.UpdateBuildRevision);
        WindowsDesktopHost desktopHost = options.AttachmentPolicy switch
        {
            DesktopAttachmentPolicy.Production => new WindowsDesktopHost(shell),
            DesktopAttachmentPolicy.AllowUnvalidatedRaisedDiagnostics =>
                WindowsDesktopHost.CreateExperimentalDiagnostics(shell),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AttachmentPolicy,
                "Unknown desktop attachment policy."),
        };
        SessionCoordinator coordinator = new(
            options.WallpaperRepository,
            new DefaultLayoutPlanner(),
            new RendererProviderSelector(options.RendererProviders),
            desktopHost,
            new RuntimeEnvironment(
                "win-x64",
                HardwareAccelerationAvailable: true,
                new HashSet<string>(StringComparer.Ordinal)));
        HostCommandLoop commandLoop = new(
            coordinator,
            new DefaultPlaybackPolicyEvaluator());
        HostBootstrapper bootstrapper = new(
            options.ConfigurationStore ?? new JsonHostConfigurationStore(configurationDirectory));
        WindowsDesktopEventBridge eventBridge = new(
            desktopHost,
            displayTopologySource,
            commandLoop);

        WindowsHostRuntime runtime = new(
            commandLoop,
            bootstrapper,
            displayTopologySource,
            eventBridge,
            async () =>
            {
                await desktopHost.DisposeAsync().ConfigureAwait(false);
                displayTopologySource.Dispose();
                (options.WallpaperRepository as IDisposable)?.Dispose();
            },
            options.PersistConfiguration);
        return new WindowsHostComposition(
            runtime,
            commandLoop,
            eventBridge,
            desktopHost,
            displayTopologySource);
    }
}

internal enum DesktopAttachmentPolicy
{
    Production,
    AllowUnvalidatedRaisedDiagnostics,
}

internal sealed record WindowsHostCompositionOptions(
    IWallpaperRepository WallpaperRepository,
    IReadOnlyList<IRendererProvider> RendererProviders,
    DesktopAttachmentPolicy AttachmentPolicy,
    IHostConfigurationStore? ConfigurationStore = null,
    bool PersistConfiguration = true);

internal sealed record WindowsHostComposition(
    WindowsHostRuntime Runtime,
    HostCommandLoop CommandLoop,
    WindowsDesktopEventBridge EventBridge,
    WindowsDesktopHost DesktopHost,
    WindowsDisplayTopologySource DisplayTopologySource);
