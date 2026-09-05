using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;

namespace LiveWall.Application.Configuration;

public interface IHostConfigurationStore
{
    Task<HostConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken);

    Task SavePlaybackPolicyAsync(
        UserPlaybackPolicy policy,
        CancellationToken cancellationToken);

    Task SaveAssignmentsAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        CancellationToken cancellationToken);
}

public sealed record HostConfiguration(
    UserPlaybackPolicy PlaybackPolicy,
    IReadOnlyList<WallpaperAssignment> Assignments)
{
    public static HostConfiguration Default { get; } = new(UserPlaybackPolicy.Default, []);
}

public sealed record HostConfigurationLoadResult(
    HostConfiguration Configuration,
    ConfigurationFileStatus PlaybackPolicyStatus,
    ConfigurationFileStatus AssignmentsStatus);

public sealed record ConfigurationFileStatus(
    ConfigurationLoadState State,
    string? ErrorCode = null)
{
    public static ConfigurationFileStatus Loaded { get; } = new(ConfigurationLoadState.Loaded);

    public static ConfigurationFileStatus Missing { get; } = new(ConfigurationLoadState.Missing);

    public static ConfigurationFileStatus Invalid(string errorCode) =>
        new(ConfigurationLoadState.Invalid, errorCode);
}

public enum ConfigurationLoadState
{
    Loaded,
    Missing,
    Invalid,
}
