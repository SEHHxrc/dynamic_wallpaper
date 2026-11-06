using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LiveWall.Application.Configuration;
using LiveWall.Application.Importing;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.Packages;
using LiveWall.Infrastructure.Persistence;

namespace LiveWall.Application.Tests;

public sealed partial class HostCommandLoopTests
{
    private const string LibraryWallpaperId = "com.example.aurora";

    [Fact]
    public async Task StartupRestoresPersistedAssignmentFromTheRealLibrary()
    {
        using LibraryFixture library = new();
        await library.SaveVideoAsync();
        TestRuntime runtime = new(true);
        HostCommandLoop commandLoop = runtime.CreateCommandLoop(repository: library.Repository);
        WallpaperAssignment assignment = CreateLibraryAssignment();

        HostBootstrapResult result = await new HostBootstrapper(
                new FakeHostConfigurationStore(new HostConfiguration(UserPlaybackPolicy.Default, [assignment])))
            .InitializeAsync(commandLoop, CreateTopology(), CancellationToken.None);

        result.RestoreGenerationResult!.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        LoadRendererContent load = renderer.Commands.OfType<LoadRendererContent>().Single();
        load.CanonicalContentRoot.Should().Be(library.ContentRoot);
        load.Wallpaper.Id.Value.Should().Be(LibraryWallpaperId);
        load.Wallpaper.EntryPoint.Should().Be("content/wallpaper.mp4");
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task RestoreOfAWallpaperAbsentFromTheLibraryFailsClosedAndKeepsTheAssignment()
    {
        using LibraryFixture library = new();
        await AssertRestoreFailsClosedAsync(library);
    }

    [Fact]
    public async Task RestoreOfTamperedLibraryContentFailsClosedAndKeepsTheAssignment()
    {
        using LibraryFixture library = new();
        await library.SaveVideoAsync();
        File.Delete(Path.Combine(library.ContentRoot, "content", "wallpaper.mp4"));

        await AssertRestoreFailsClosedAsync(library);
    }

    [Fact]
    public async Task RestoreWithoutAMatchingRendererFailsClosedAndKeepsTheAssignment()
    {
        using LibraryFixture library = new();
        await library.SaveVideoAsync();
        FakeDesktopHost desktop = new();
        HostCommandLoop commandLoop = new(
            new SessionCoordinator(
                library.Repository,
                new LiveWall.Application.Layouts.DefaultLayoutPlanner(),
                new RendererProviderSelector([]),
                desktop,
                new RuntimeEnvironment("win-x64", true, new HashSet<string>())),
            new LiveWall.Application.Policies.DefaultPlaybackPolicyEvaluator());
        WallpaperAssignment assignment = CreateLibraryAssignment();

        HostBootstrapResult result = await new HostBootstrapper(
                new FakeHostConfigurationStore(new HostConfiguration(UserPlaybackPolicy.Default, [assignment])))
            .InitializeAsync(commandLoop, CreateTopology(), CancellationToken.None);

        result.RestoreGenerationResult!.State.Should().Be(ApplyGenerationTerminalState.Failed);
        desktop.CreatedSurfaceCount.Should().Be(0);
        (await GetStateAsync(commandLoop)).Assignments.Should().ContainSingle().Which.Should().Be(assignment);
    }

    [Fact]
    public void DefaultProvidersFailClosedWhenTheRendererIsNotInstalledSideBySide()
    {
        using LibraryFixture library = new();

        DefaultRendererProviders.Create("default", library.RootPath).Should().BeEmpty();
    }

    [Fact]
    public void DefaultProvidersOfferOnlyTheVideoRendererWhenInstalled()
    {
        using LibraryFixture library = new();
        File.WriteAllBytes(Path.Combine(library.RootPath, DefaultRendererProviders.VideoExecutableName), []);
        RuntimeEnvironment environment = new("win-x64", true, new HashSet<string>());
        WallpaperDefinition video = LibraryDefinition();

        IRendererProvider provider = DefaultRendererProviders.Create("default", library.RootPath)
            .Should().ContainSingle().Subject;

        provider.Match(video, environment).IsMatch.Should().BeTrue();
        provider.Match(video with { Kind = WallpaperKind.Web }, environment).IsMatch.Should().BeFalse();
        provider.Match(video with { Kind = WallpaperKind.External }, environment).IsMatch.Should().BeFalse();
    }

    [Fact]
    public void ProviderLocaleFallsBackForTheInvariantCulture()
    {
        DefaultRendererProviders.ResolveLocale(CultureInfo.InvariantCulture).Should().Be("en-US");
        DefaultRendererProviders.ResolveLocale(CultureInfo.GetCultureInfo("zh-CN")).Should().Be("zh-CN");
    }

    private static async Task AssertRestoreFailsClosedAsync(LibraryFixture library)
    {
        TestRuntime runtime = new(true);
        HostCommandLoop commandLoop = runtime.CreateCommandLoop(repository: library.Repository);
        WallpaperAssignment assignment = CreateLibraryAssignment();
        FakeHostConfigurationStore store = new(new HostConfiguration(UserPlaybackPolicy.Default, [assignment]));
        HostBootstrapper bootstrapper = new(store);

        HostBootstrapResult result = await bootstrapper.InitializeAsync(
            commandLoop,
            CreateTopology(),
            CancellationToken.None);

        result.RestoreGenerationResult!.State.Should().Be(ApplyGenerationTerminalState.Failed);
        runtime.Provider.SessionCount.Should().Be(0);
        runtime.Desktop.CreatedSurfaceCount.Should().Be(0);
        HostStateSnapshot state = await GetStateAsync(commandLoop);
        state.Sessions.Should().BeEmpty();
        state.Assignments.Should().ContainSingle().Which.Should().Be(assignment);

        await bootstrapper.PersistAsync(state, CancellationToken.None);
        store.SavedAssignments.Should().ContainSingle().Which.Should().Be(assignment);
    }

    private static WallpaperAssignment CreateLibraryAssignment() => new(
        new DisplayId("primary"),
        new WallpaperId(LibraryWallpaperId),
        LayoutMode.PerDisplay,
        FitMode.Cover,
        null);

    private static WallpaperDefinition LibraryDefinition() => new(
        new WallpaperId(LibraryWallpaperId),
        "1.0.0",
        WallpaperKind.Video,
        "content/wallpaper.mp4",
        new WallpaperMetadata("Aurora", null, null, null, new HashSet<string>()),
        new WallpaperCapabilities(false, false, false, false, 30),
        WallpaperOrigin.Native);

    private sealed class LibraryFixture : IDisposable
    {
        public LibraryFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"livewall-restore-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            Repository = new JsonWallpaperRepository(Path.Combine(RootPath, "library"));
        }

        public string RootPath { get; }

        public JsonWallpaperRepository Repository { get; }

        public string ContentRoot => Path.Combine(RootPath, "library", LibraryWallpaperId, "1.0.0");

        public async Task SaveVideoAsync()
        {
            string staged = Path.Combine(RootPath, "staged");
            Directory.CreateDirectory(Path.Combine(staged, "content"));
            await File.WriteAllBytesAsync(
                Path.Combine(staged, "content", "wallpaper.mp4"),
                RandomNumberGenerator.GetBytes(16));
            await File.WriteAllTextAsync(
                Path.Combine(staged, WallpaperManifestReader.FileName),
                $$"""
                {
                  "schemaVersion": 1,
                  "id": "{{LibraryWallpaperId}}",
                  "version": "1.0.0",
                  "title": "Aurora",
                  "type": "video",
                  "entry": "content/wallpaper.mp4",
                  "permissions": { "network": false, "audioCapture": false, "pointerInput": false, "systemMetrics": false },
                  "defaults": { "fit": "cover", "fpsLimit": 30, "muted": true }
                }
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await Repository.SaveImportedAsync(
                new ImportedWallpaper(
                    LibraryDefinition(),
                    staged,
                    await WallpaperContentTree.ComputeHashAsync(staged, CancellationToken.None),
                    new CompatibilityReport(CompatibilityGrade.Native, [])),
                CancellationToken.None);
        }

        public void Dispose()
        {
            Repository.Dispose();
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
