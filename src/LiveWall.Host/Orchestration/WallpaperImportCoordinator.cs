using LiveWall.Application.Importing;
using LiveWall.Application.Library;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Infrastructure.Packages;

namespace LiveWall.Host.Orchestration;

public sealed record WallpaperImportResult(
    bool Succeeded,
    string? ImporterId,
    WallpaperId? WallpaperId,
    string? Version,
    bool AlreadyInLibrary,
    CompatibilityReport? Compatibility,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Host-side import pipeline: probe for an importer, let it stage normalized content in a private
/// directory, pin the content hash and hand the content to the library, which verifies and commits it.
/// Staging is always removed. It touches no command-loop state, so the future ImportWallpaper RPC can run
/// it in the background.
/// </summary>
internal sealed class WallpaperImportCoordinator
{
    public const string SourceUnrecognized = "import.source_unrecognized";
    public const string StagingMismatch = "import.staging_mismatch";
    public const string ImportIoFailed = "import.io_failed";
    public const string LibraryRejected = "library.rejected";
    public const string LibraryWriteFailed = "library.write_failed";

    private readonly IWallpaperImporter[] importers;
    private readonly IWallpaperRepository repository;
    private readonly string stagingRoot;
    private readonly Lock stagingGate = new();
    private bool leftoversCleared;

    public WallpaperImportCoordinator(
        IEnumerable<IWallpaperImporter> importers,
        IWallpaperRepository repository,
        string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        this.importers = importers.ToArray();
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.stagingRoot = Path.GetFullPath(stagingRoot);
    }

    public async Task<WallpaperImportResult> ImportAsync(
        ImportSource source,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<string> probeReasons = [];
        IWallpaperImporter? importer = null;
        foreach (IWallpaperImporter candidate in importers)
        {
            ImportProbeResult probe = await candidate.ProbeAsync(source, cancellationToken).ConfigureAwait(false);
            if (probe.Recognized)
            {
                importer = candidate;
                break;
            }

            probeReasons.AddRange(probe.Warnings);
        }

        if (importer is null)
        {
            return Rejected(null, [SourceUnrecognized, .. probeReasons.Distinct(StringComparer.Ordinal)]);
        }

        string staging = CreateStagingDirectory();
        try
        {
            ImportResult result;
            try
            {
                result = await importer.ImportAsync(
                        new ImportRequest(source, staging),
                        progress ?? NoProgress.Instance,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Rejected(importer.Id, [ImportIoFailed]);
            }

            if (!result.Succeeded || result.Wallpaper is not { } staged)
            {
                return Rejected(importer.Id, result.Diagnostics);
            }

            if (!Path.GetFullPath(staged.StagedContentDirectory).Equals(staging, StringComparison.OrdinalIgnoreCase))
            {
                return Rejected(importer.Id, [StagingMismatch]);
            }

            WallpaperDefinition definition = staged.Definition;
            // A reserved local video id names its media, so an existing readable version is the same video;
            // skipping avoids copying it again. The library would also treat the save as a duplicate.
            if (LocalVideoIdentity.IsReserved(definition.Id) &&
                await repository.FindAsync(definition.Id, cancellationToken).ConfigureAwait(false) is { } existing &&
                existing.Definition.Version.Equals(definition.Version, StringComparison.OrdinalIgnoreCase))
            {
                return new WallpaperImportResult(true, importer.Id, existing.Definition.Id,
                    existing.Definition.Version, AlreadyInLibrary: true, existing.Compatibility, result.Diagnostics);
            }

            try
            {
                string contentHash = await WallpaperContentTree.ComputeHashAsync(staging, cancellationToken)
                    .ConfigureAwait(false);
                await repository.SaveImportedAsync(
                        new ImportedWallpaper(definition, staging, contentHash, staged.Compatibility),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or PathSecurityException)
            {
                return Rejected(importer.Id, [LibraryRejected]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Rejected(importer.Id, [LibraryWriteFailed]);
            }

            return new WallpaperImportResult(true, importer.Id, definition.Id, definition.Version,
                AlreadyInLibrary: false, staged.Compatibility, result.Diagnostics);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private string CreateStagingDirectory()
    {
        lock (stagingGate)
        {
            if (!leftoversCleared)
            {
                // Before this Host's first import only leftovers of an earlier process can exist here:
                // the Host instance mutex excludes another live writer.
                TryDeleteDirectory(stagingRoot);
                leftoversCleared = true;
            }

            string staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            return staging;
        }
    }

    private static WallpaperImportResult Rejected(string? importerId, IReadOnlyList<string> diagnostics) =>
        new(false, importerId, null, null, AlreadyInLibrary: false, null, diagnostics);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Invisible leftovers; cleared before the next Host's first import.
        }
    }

    private sealed class NoProgress : IProgress<ImportProgress>
    {
        public static NoProgress Instance { get; } = new();

        public void Report(ImportProgress value)
        {
        }
    }
}
