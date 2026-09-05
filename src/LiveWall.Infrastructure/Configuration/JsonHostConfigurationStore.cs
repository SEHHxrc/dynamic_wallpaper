using System.Text.Json;
using System.Text.Json.Serialization;
using LiveWall.Application.Configuration;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.FileSystem;

namespace LiveWall.Infrastructure.Configuration;

public sealed class JsonHostConfigurationStore : IHostConfigurationStore
{
    public const int CurrentSchemaVersion = 1;
    private const string InvalidJsonError = "configuration.json.invalid";
    private const string UnsupportedVersionError = "configuration.schema.unsupported";
    private const string InvalidValueError = "configuration.value.invalid";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string playbackPolicyPath;
    private readonly string assignmentsPath;

    public JsonHostConfigurationStore(string configurationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
        string directory = Path.GetFullPath(configurationDirectory);
        playbackPolicyPath = Path.Combine(directory, "playback-policy.json");
        assignmentsPath = Path.Combine(directory, "assignments.json");
    }

    public async Task<HostConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        FileLoadResult<UserPlaybackPolicy> policy = await LoadPlaybackPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        FileLoadResult<IReadOnlyList<WallpaperAssignment>> assignments =
            await LoadAssignmentsAsync(cancellationToken).ConfigureAwait(false);

        return new HostConfigurationLoadResult(
            new HostConfiguration(policy.Value, assignments.Value),
            policy.Status,
            assignments.Status);
    }

    public async Task SavePlaybackPolicyAsync(
        UserPlaybackPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PlaybackPolicyDocument document = new(CurrentSchemaVersion, policy);
        await WriteDocumentAsync(playbackPolicyPath, document, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAssignmentsAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        EnsureUniqueDisplays(assignments);
        AssignmentDocument[] values = assignments
            .OrderBy(assignment => assignment.DisplayId.Value, StringComparer.Ordinal)
            .Select(assignment => new AssignmentDocument(
                assignment.DisplayId.Value,
                assignment.WallpaperId.Value,
                assignment.LayoutMode,
                assignment.FitMode,
                assignment.PresetId?.Value))
            .ToArray();
        AssignmentsDocument document = new(CurrentSchemaVersion, values);
        await WriteDocumentAsync(assignmentsPath, document, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileLoadResult<UserPlaybackPolicy>> LoadPlaybackPolicyAsync(
        CancellationToken cancellationToken)
    {
        FileReadResult<PlaybackPolicyDocument> result =
            await ReadDocumentAsync<PlaybackPolicyDocument>(playbackPolicyPath, cancellationToken)
                .ConfigureAwait(false);
        if (result.Document is null)
        {
            return new FileLoadResult<UserPlaybackPolicy>(UserPlaybackPolicy.Default, result.Status);
        }

        if (result.Document.SchemaVersion != CurrentSchemaVersion)
        {
            return Invalid(UserPlaybackPolicy.Default, UnsupportedVersionError);
        }

        try
        {
            UserPlaybackPolicy value = result.Document.Policy ??
                throw new ArgumentException("Playback policy is required.");
            _ = new UserPlaybackPolicy(
                value.PauseWhenDisplayOff,
                value.PauseWhenSessionLocked,
                value.PauseInRemoteSession,
                value.PauseOnBattery,
                value.PauseForFullscreenApplication,
                value.DefaultFramesPerSecond,
                value.ThrottledFramesPerSecond);
            return new FileLoadResult<UserPlaybackPolicy>(value, ConfigurationFileStatus.Loaded);
        }
        catch (ArgumentException)
        {
            return Invalid(UserPlaybackPolicy.Default, InvalidValueError);
        }
    }

    private async Task<FileLoadResult<IReadOnlyList<WallpaperAssignment>>> LoadAssignmentsAsync(
        CancellationToken cancellationToken)
    {
        FileReadResult<AssignmentsDocument> result =
            await ReadDocumentAsync<AssignmentsDocument>(assignmentsPath, cancellationToken)
                .ConfigureAwait(false);
        if (result.Document is null)
        {
            return new FileLoadResult<IReadOnlyList<WallpaperAssignment>>([], result.Status);
        }

        if (result.Document.SchemaVersion != CurrentSchemaVersion)
        {
            return Invalid<IReadOnlyList<WallpaperAssignment>>([], UnsupportedVersionError);
        }

        try
        {
            WallpaperAssignment[] assignments = (result.Document.Assignments ?? [])
                .Select(value => new WallpaperAssignment(
                    new DisplayId(value.DisplayId),
                    new WallpaperId(value.WallpaperId),
                    value.LayoutMode,
                    value.FitMode,
                    value.PresetId is null ? null : new PropertyPresetId(value.PresetId)))
                .ToArray();
            EnsureUniqueDisplays(assignments);
            return new FileLoadResult<IReadOnlyList<WallpaperAssignment>>(
                assignments,
                ConfigurationFileStatus.Loaded);
        }
        catch (ArgumentException)
        {
            return Invalid<IReadOnlyList<WallpaperAssignment>>([], InvalidValueError);
        }
    }

    private static async Task<FileReadResult<T>> ReadDocumentAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new FileReadResult<T>(default, ConfigurationFileStatus.Missing);
        }

        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            T? document = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return document is null
                ? new FileReadResult<T>(default, ConfigurationFileStatus.Invalid(InvalidJsonError))
                : new FileReadResult<T>(document, ConfigurationFileStatus.Loaded);
        }
        catch (JsonException)
        {
            return new FileReadResult<T>(default, ConfigurationFileStatus.Invalid(InvalidJsonError));
        }
        catch (FileNotFoundException)
        {
            return new FileReadResult<T>(default, ConfigurationFileStatus.Missing);
        }
    }

    private static async Task WriteDocumentAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        await AtomicFileWriter.WriteJsonAsync(path, document, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void EnsureUniqueDisplays(IReadOnlyList<WallpaperAssignment> assignments)
    {
        if (assignments.Select(assignment => assignment.DisplayId).Distinct().Count() != assignments.Count)
        {
            throw new ArgumentException("A display can only have one persisted assignment.", nameof(assignments));
        }
    }

    private static FileLoadResult<T> Invalid<T>(T fallback, string errorCode) =>
        new(fallback, ConfigurationFileStatus.Invalid(errorCode));

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record PlaybackPolicyDocument(int SchemaVersion, UserPlaybackPolicy? Policy);

    private sealed record AssignmentsDocument(
        int SchemaVersion,
        IReadOnlyList<AssignmentDocument>? Assignments);

    private sealed record AssignmentDocument(
        string DisplayId,
        string WallpaperId,
        LayoutMode LayoutMode,
        FitMode FitMode,
        string? PresetId);

    private sealed record FileReadResult<T>(T? Document, ConfigurationFileStatus Status);

    private sealed record FileLoadResult<T>(T Value, ConfigurationFileStatus Status);
}
