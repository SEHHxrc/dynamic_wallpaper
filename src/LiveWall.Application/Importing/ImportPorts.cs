using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Application.Importing;

public interface IWallpaperImporter
{
    string Id { get; }

    ValueTask<ImportProbeResult> ProbeAsync(
        ImportSource source,
        CancellationToken cancellationToken);

    Task<ImportResult> ImportAsync(
        ImportRequest request,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken);
}

public interface IPackageContainerReader
{
    string FormatId { get; }

    bool CanRead(ReadOnlyMemory<byte> header);

    Task<PackageIndex> ReadIndexAsync(Stream stream, CancellationToken cancellationToken);

    Task ExtractAsync(
        Stream stream,
        PackageIndex index,
        string outputDirectory,
        ExtractionPolicy policy,
        CancellationToken cancellationToken);
}

public interface IContentConverter
{
    bool CanConvert(PackageAsset asset, TargetContentFormat target);

    Task<ConvertedAsset> ConvertAsync(
        PackageAsset asset,
        TargetContentFormat target,
        CancellationToken cancellationToken);
}

public sealed record ImportSource(string Path, string? DeclaredFileName = null);

public sealed record ImportProbeResult(
    bool Recognized,
    string? ImporterId,
    string? DetectedFormat,
    CompatibilityReport? PredictedCompatibility,
    IReadOnlyList<string> Warnings);

public sealed record ImportRequest(
    ImportSource Source,
    string StagingDirectory,
    string? PreferredStrategy = null);

public sealed record ImportResult(
    bool Succeeded,
    StagedWallpaper? Wallpaper,
    CompatibilityReport Compatibility,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Normalized content an importer wrote into its staging directory. It is not yet pinned by a content
/// hash; the Host computes that before handing the content to the library.
/// </summary>
public sealed record StagedWallpaper(
    WallpaperDefinition Definition,
    string StagedContentDirectory,
    CompatibilityReport Compatibility);

/// <summary>Staged content pinned by its content hash, ready for the library to verify and commit.</summary>
public sealed record ImportedWallpaper(
    WallpaperDefinition Definition,
    string StagedContentDirectory,
    string ContentHash,
    CompatibilityReport Compatibility);

public sealed record ImportProgress(string Stage, double Fraction, string? Message = null);

public sealed record PackageIndex(
    string FormatId,
    IReadOnlyList<PackageEntry> Entries,
    long DeclaredUncompressedSize);

public sealed record PackageEntry(string Path, long Offset, long Size, long? CompressedSize = null);

public sealed record ExtractionPolicy(
    int MaximumEntryCount,
    long MaximumEntryBytes,
    long MaximumTotalBytes,
    int MaximumNestingDepth);

public sealed record PackageAsset(string Path, string MediaType, long Size);

public sealed record ConvertedAsset(
    PackageAsset Source,
    PackageAsset Output,
    IReadOnlyList<string> FeatureLosses);

public enum TargetContentFormat
{
    Original,
    Png,
    WebP,
    Mp4,
    WebProject,
}
