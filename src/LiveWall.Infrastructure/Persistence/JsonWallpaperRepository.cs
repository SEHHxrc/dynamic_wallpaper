using System.Text.Json;
using LiveWall.Application.Importing;
using LiveWall.Application.Library;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Infrastructure.Packages;

namespace LiveWall.Infrastructure.Persistence;

/// <summary>
/// Minimal persistent library: an index plus library/&lt;id&gt;/&lt;version&gt; directories. The index is the
/// only commit point, so anything it does not name is invisible. Reads fail closed; the Host's
/// single-instance mutex makes this the only writer.
/// </summary>
public sealed class JsonWallpaperRepository : IWallpaperRepository, IDisposable
{
    public const int CurrentSchemaVersion = 1;

    // Names start with a dot because manifest ids cannot, so no wallpaper directory can collide.
    private const string IndexFileName = ".index.json";
    private const string StagingDirectoryName = ".staging";
    private const string TrashDirectoryName = ".trash";
    private const long MaximumIndexBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = StrictJson.CreateOptions();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string libraryRoot;
    private readonly string indexPath;
    private bool initialized;

    public JsonWallpaperRepository(string libraryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryDirectory);
        libraryRoot = Path.GetFullPath(libraryDirectory);
        indexPath = Path.Combine(libraryRoot, IndexFileName);
    }

    public void Dispose() => gate.Dispose();

    public async Task<WallpaperLibraryEntry?> FindAsync(WallpaperId id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<IndexEntry> index = await TryReadIndexAsync(cancellationToken).ConfigureAwait(false);
            IndexEntry? entry = index.FirstOrDefault(value => value.Id == id.Value);
            return entry is null
                ? null
                : await TryOpenEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<WallpaperLibraryEntry>> ListAsync(
        WallpaperQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<IndexEntry> index = await TryReadIndexAsync(cancellationToken).ConfigureAwait(false);
            List<WallpaperLibraryEntry> matches = [];
            foreach (IndexEntry entry in index.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                WallpaperLibraryEntry? opened = await TryOpenEntryAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
                if (opened is not null && Matches(opened.Definition, query))
                {
                    matches.Add(opened);
                }
            }

            return matches.Skip(query.Offset).Take(query.Limit).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Validates and hashes a private copy, so later changes to the staged source cannot reach the
    /// library. Re-saving identical content is a no-op; a readable version is immutable once committed,
    /// while an unreadable one is replaced, so importing it again repairs it. A reserved local video id
    /// is only accepted when its media really has the hash the id carries.
    /// </summary>
    public async Task SaveImportedAsync(ImportedWallpaper wallpaper, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ValidateDeclaration(wallpaper);

        string staging = Path.Combine(libraryRoot, StagingDirectoryName, Guid.NewGuid().ToString("N"));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            // A corrupt index must fail before an expensive copy, and is never overwritten.
            _ = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(staging);
        }
        finally
        {
            gate.Release();
        }

        try
        {
            // Copying and hashing can take minutes for a large video; lookups must not wait on it.
            await WallpaperContentTree.CopyAsync(
                    wallpaper.StagedContentDirectory,
                    staging,
                    cancellationToken)
                .ConfigureAwait(false);
            ContentTreeDigest digest = await WallpaperContentTree.ComputeDigestAsync(staging, cancellationToken)
                .ConfigureAwait(false);
            string contentHash = digest.TreeHash;
            if (!contentHash.Equals(wallpaper.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Wallpaper content does not match its declared hash.");
            }

            WallpaperManifest manifest = await ReadRunnableManifestAsync(staging, cancellationToken)
                .ConfigureAwait(false);
            WallpaperDefinition declared = wallpaper.Definition;
            if (manifest.Id != declared.Id ||
                manifest.Version != declared.Version ||
                manifest.Kind != declared.Kind ||
                manifest.EntryPoint != declared.EntryPoint)
            {
                throw new InvalidDataException("Wallpaper manifest does not match its definition.");
            }

            VerifyReservedIdentity(manifest, declared.Origin, digest);
            await CommitAsync(wallpaper, staging, contentHash, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public async Task RemoveAsync(WallpaperId id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            IReadOnlyList<IndexEntry> index = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            IndexEntry? entry = index.FirstOrDefault(value => value.Id == id.Value);
            if (entry is null)
            {
                return;
            }

            await WriteIndexAsync(index.Where(value => value != entry).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            DiscardContent(entry);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CommitAsync(
        ImportedWallpaper wallpaper,
        string staging,
        string contentHash,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<IndexEntry> index = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            WallpaperDefinition definition = wallpaper.Definition;
            IndexEntry? previous = index.FirstOrDefault(value => value.Id == definition.Id.Value);
            // Versions differing only by case share one directory on NTFS, so they are the same version.
            bool sameVersion = previous is not null &&
                previous.Version.Equals(definition.Version, StringComparison.OrdinalIgnoreCase);
            if (sameVersion && await TryOpenEntryAsync(previous!, cancellationToken).ConfigureAwait(false) is not null)
            {
                // Both copies of a reserved local video id were verified against the same media hash, so
                // only metadata such as the title can differ; the first import keeps its metadata.
                if (previous!.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase) ||
                    LocalVideoIdentity.IsReserved(definition.Id))
                {
                    return;
                }

                throw new InvalidDataException("A committed wallpaper version is immutable.");
            }

            string destination = ResolveContentRoot(definition.Id.Value, definition.Version);
            if (Directory.Exists(destination))
            {
                // Either leftover content from an interrupted commit or removal, or the same version
                // that can no longer be read and is being repaired.
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            IndexEntry committed = new(
                definition.Id.Value,
                definition.Version,
                contentHash,
                definition.Origin,
                wallpaper.Compatibility.Grade,
                wallpaper.Compatibility.Issues);
            try
            {
                await WriteIndexAsync(
                        [.. index.Where(value => value != previous), committed],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(destination);
                throw;
            }

            if (previous is not null && !sameVersion)
            {
                DiscardContent(previous);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void VerifyReservedIdentity(
        WallpaperManifest manifest,
        WallpaperOrigin origin,
        ContentTreeDigest digest)
    {
        if (!LocalVideoIdentity.IsReserved(manifest.Id))
        {
            return;
        }

        if (!LocalVideoIdentity.TryGetMediaHash(manifest.Id, out string? mediaHash) ||
            manifest.Version != LocalVideoIdentity.Version ||
            origin != WallpaperOrigin.Native ||
            !LocalVideoIdentity.IsEntryPoint(manifest.EntryPoint) ||
            !digest.FileHashes.TryGetValue(manifest.EntryPoint, out string? entryHash) ||
            !entryHash.Equals(mediaHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A reserved local video identity must match its media.");
        }
    }

    private static void ValidateDeclaration(ImportedWallpaper wallpaper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wallpaper.StagedContentDirectory);
        if (!IsValidContentHash(wallpaper.ContentHash))
        {
            throw new InvalidDataException("Wallpaper content hash is invalid.");
        }

        if (!WallpaperManifestReader.IsValidId(wallpaper.Definition.Id.Value) ||
            !WallpaperManifestReader.IsValidVersion(wallpaper.Definition.Version))
        {
            throw new InvalidDataException("Wallpaper identity is invalid.");
        }

        if (wallpaper.Definition.Origin == WallpaperOrigin.WallpaperEngineDelegate ||
            wallpaper.Compatibility.Grade == CompatibilityGrade.Unsupported)
        {
            throw new InvalidDataException("The library only stores content LiveWall can run itself.");
        }

        if (!IsValidIssues(wallpaper.Compatibility.Issues))
        {
            throw new InvalidDataException("Wallpaper compatibility report is invalid.");
        }
    }

    private async Task<WallpaperLibraryEntry?> TryOpenEntryAsync(
        IndexEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            string root = ResolveContentRoot(entry.Id, entry.Version);
            WallpaperManifest manifest = await ReadRunnableManifestAsync(root, cancellationToken)
                .ConfigureAwait(false);
            if (manifest.Id.Value != entry.Id || manifest.Version != entry.Version)
            {
                return null;
            }

            return new WallpaperLibraryEntry(
                manifest.ToDefinition(entry.Origin),
                root,
                new CompatibilityReport(entry.Grade, entry.Issues ?? []));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<WallpaperManifest> ReadRunnableManifestAsync(
        string contentRoot,
        CancellationToken cancellationToken)
    {
        CanonicalPathResolver resolver = new(contentRoot);
        string manifestPath = resolver.ResolvePackagePath(WallpaperManifestReader.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("Wallpaper manifest is missing.");
        }

        if (new FileInfo(manifestPath).Length > WallpaperManifestReader.MaximumManifestBytes)
        {
            throw new InvalidDataException("Manifest exceeds the size limit.");
        }

        byte[] json = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        WallpaperManifest manifest = WallpaperManifestReader.Parse(json);
        if (manifest.Kind != WallpaperKind.Video ||
            !NativeVideoFormats.EntryExtensions.Contains(Path.GetExtension(manifest.EntryPoint)))
        {
            throw new InvalidDataException("The library currently accepts video content only.");
        }

        if (manifest.Capabilities is { RequiresNetwork: true } or { RequiresAudioCapture: true })
        {
            throw new InvalidDataException("Video content cannot request network or audio capture.");
        }

        if (!File.Exists(resolver.ResolvePackagePath(manifest.EntryPoint)))
        {
            throw new InvalidDataException("Wallpaper entry file does not exist.");
        }

        if (manifest.Metadata.PreviewPath is { } preview &&
            !File.Exists(resolver.ResolvePackagePath(preview)))
        {
            throw new InvalidDataException("Wallpaper preview file does not exist.");
        }

        return manifest;
    }

    private string ResolveContentRoot(string id, string version) =>
        new CanonicalPathResolver(libraryRoot).ResolvePackagePath($"{id}/{version}");

    private async Task<IReadOnlyList<IndexEntry>> TryReadIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<IndexEntry>> ReadIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
        {
            return [];
        }

        if (new FileInfo(indexPath).Length > MaximumIndexBytes)
        {
            throw new InvalidDataException("Library index exceeds the size limit.");
        }

        IndexDocument? document;
        try
        {
            await using FileStream stream = new(
                indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonSerializer
                .DeserializeAsync<IndexDocument>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Library index is not valid.", exception);
        }

        if (document is not { SchemaVersion: CurrentSchemaVersion, Wallpapers: { } wallpapers } ||
            wallpapers.Any(entry => !IsValidEntry(entry)) ||
            wallpapers.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != wallpapers.Count)
        {
            throw new InvalidDataException("Library index is not valid.");
        }

        return wallpapers;
    }

    private Task WriteIndexAsync(IReadOnlyList<IndexEntry> entries, CancellationToken cancellationToken) =>
        AtomicFileWriter.WriteJsonAsync(
            indexPath,
            new IndexDocument(CurrentSchemaVersion, entries),
            SerializerOptions,
            cancellationToken);

    private static bool IsValidEntry(IndexEntry? entry) =>
        entry is not null &&
        WallpaperManifestReader.IsValidId(entry.Id) &&
        WallpaperManifestReader.IsValidVersion(entry.Version) &&
        IsValidContentHash(entry.ContentHash) &&
        IsValidIssues(entry.Issues ?? []);

    private static bool IsValidContentHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsValidIssues(IReadOnlyList<CompatibilityIssue> issues) =>
        issues.All(issue => issue is not null &&
            !string.IsNullOrWhiteSpace(issue.Code) &&
            !string.IsNullOrWhiteSpace(issue.Message));

    private static bool Matches(WallpaperDefinition definition, WallpaperQuery query)
    {
        if (query.Kind is { } kind && definition.Kind != kind)
        {
            return false;
        }

        if (query.Origin is { } origin && definition.Origin != origin)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return true;
        }

        WallpaperMetadata metadata = definition.Metadata;
        return new[] { metadata.Title, metadata.Description, metadata.Author }.Any(value =>
            value?.Contains(query.Text, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        Directory.CreateDirectory(libraryRoot);
        // Only leftovers can exist here: the Host mutex excludes another live writer.
        TryDeleteDirectory(Path.Combine(libraryRoot, StagingDirectoryName));
        TryDeleteDirectory(Path.Combine(libraryRoot, TrashDirectoryName));
        initialized = true;
    }

    /// <summary>
    /// Runs after the index commit, so failure only strands invisible content: a Renderer may still
    /// hold the old files open. Leftovers are cleared on the next start or a re-import.
    /// </summary>
    private void DiscardContent(IndexEntry entry)
    {
        try
        {
            string root = ResolveContentRoot(entry.Id, entry.Version);
            if (!Directory.Exists(root))
            {
                return;
            }

            string trash = Path.Combine(libraryRoot, TrashDirectoryName);
            Directory.CreateDirectory(trash);
            string discarded = Path.Combine(trash, Guid.NewGuid().ToString("N"));
            Directory.Move(root, discarded);
            TryDeleteDirectory(discarded);
            string parent = Path.GetDirectoryName(root)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deliberately ignored; see the summary.
        }
    }

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
            // Invisible leftovers; cleared on the next start.
        }
    }

    private sealed record IndexDocument(int SchemaVersion, IReadOnlyList<IndexEntry>? Wallpapers);

    private sealed record IndexEntry(
        string Id,
        string Version,
        string ContentHash,
        WallpaperOrigin Origin,
        CompatibilityGrade Grade,
        IReadOnlyList<CompatibilityIssue>? Issues);
}
