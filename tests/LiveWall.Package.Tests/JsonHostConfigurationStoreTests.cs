using FluentAssertions;
using LiveWall.Application.Configuration;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.Configuration;

namespace LiveWall.Package.Tests;

public sealed class JsonHostConfigurationStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), $"livewall-config-tests-{Guid.NewGuid():N}");

    public JsonHostConfigurationStoreTests()
    {
        Directory.CreateDirectory(rootPath);
    }

    [Fact]
    public async Task MissingFilesReturnSafeDefaultsWithoutCreatingFiles()
    {
        JsonHostConfigurationStore store = new(rootPath);

        HostConfigurationLoadResult result = await store.LoadAsync(CancellationToken.None);

        result.Configuration.Should().BeEquivalentTo(HostConfiguration.Default);
        result.PlaybackPolicyStatus.State.Should().Be(ConfigurationLoadState.Missing);
        result.AssignmentsStatus.State.Should().Be(ConfigurationLoadState.Missing);
        Directory.EnumerateFiles(rootPath).Should().BeEmpty();
    }

    [Fact]
    public async Task RoundTripsPolicyAndAssignmentsWithStringEnums()
    {
        JsonHostConfigurationStore store = new(rootPath);
        UserPlaybackPolicy policy = new(true, false, true, true, false, 60, 20);
        WallpaperAssignment[] assignments =
        [
            new(
                new DisplayId("display-b"),
                new WallpaperId("sample.two"),
                LayoutMode.Span,
                FitMode.Contain,
                new PropertyPresetId("quiet")),
            new(
                new DisplayId("display-a"),
                new WallpaperId("sample.one"),
                LayoutMode.PerDisplay,
                FitMode.Cover,
                null),
        ];

        await store.SavePlaybackPolicyAsync(policy, CancellationToken.None);
        await store.SaveAssignmentsAsync(assignments, CancellationToken.None);
        HostConfigurationLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        loaded.Configuration.PlaybackPolicy.Should().Be(policy);
        loaded.Configuration.Assignments.Should().BeEquivalentTo(assignments);
        loaded.PlaybackPolicyStatus.State.Should().Be(ConfigurationLoadState.Loaded);
        loaded.AssignmentsStatus.State.Should().Be(ConfigurationLoadState.Loaded);
        (await File.ReadAllTextAsync(Path.Combine(rootPath, "assignments.json")))
            .Should().Contain("\"layoutMode\": \"perDisplay\"");
    }

    [Fact]
    public async Task InvalidJsonIsPreservedAndOnlyThatFileFallsBack()
    {
        string invalidPath = Path.Combine(rootPath, "playback-policy.json");
        await File.WriteAllTextAsync(invalidPath, "{not-json");
        JsonHostConfigurationStore store = new(rootPath);
        await store.SaveAssignmentsAsync(
            [new WallpaperAssignment(
                new DisplayId("primary"),
                new WallpaperId("sample"),
                LayoutMode.PerDisplay,
                FitMode.Cover,
                null)],
            CancellationToken.None);

        HostConfigurationLoadResult result = await store.LoadAsync(CancellationToken.None);

        result.PlaybackPolicyStatus.Should().Be(
            ConfigurationFileStatus.Invalid("configuration.json.invalid"));
        result.AssignmentsStatus.State.Should().Be(ConfigurationLoadState.Loaded);
        (await File.ReadAllTextAsync(invalidPath)).Should().Be("{not-json");
    }

    [Fact]
    public async Task UnsupportedSchemaIsPreservedAndRejected()
    {
        string policyPath = Path.Combine(rootPath, "playback-policy.json");
        const string source = """
            { "schemaVersion": 99, "policy": null }
            """;
        await File.WriteAllTextAsync(policyPath, source);
        JsonHostConfigurationStore store = new(rootPath);

        HostConfigurationLoadResult result = await store.LoadAsync(CancellationToken.None);

        result.PlaybackPolicyStatus.Should().Be(
            ConfigurationFileStatus.Invalid("configuration.schema.unsupported"));
        (await File.ReadAllTextAsync(policyPath)).Should().Be(source);
    }

    [Fact]
    public async Task UndeclaredFieldsAreRejectedToMatchThePublishedSchema()
    {
        string assignmentsPath = Path.Combine(rootPath, "assignments.json");
        const string source = """
            { "schemaVersion": 1, "assignments": [], "unexpected": true }
            """;
        await File.WriteAllTextAsync(assignmentsPath, source);
        JsonHostConfigurationStore store = new(rootPath);

        HostConfigurationLoadResult result = await store.LoadAsync(CancellationToken.None);

        result.AssignmentsStatus.Should().Be(
            ConfigurationFileStatus.Invalid("configuration.json.invalid"));
        (await File.ReadAllTextAsync(assignmentsPath)).Should().Be(source);
    }

    [Fact]
    public async Task DuplicateDisplayAssignmentsAreRejectedBeforeWriting()
    {
        JsonHostConfigurationStore store = new(rootPath);
        WallpaperAssignment first = new(
            new DisplayId("primary"),
            new WallpaperId("one"),
            LayoutMode.PerDisplay,
            FitMode.Cover,
            null);
        WallpaperAssignment second = first with { WallpaperId = new WallpaperId("two") };

        Func<Task> act = () => store.SaveAssignmentsAsync([first, second], CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        File.Exists(Path.Combine(rootPath, "assignments.json")).Should().BeFalse();
    }

    public void Dispose()
    {
        Directory.Delete(rootPath, recursive: true);
    }
}
