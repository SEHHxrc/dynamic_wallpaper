using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveWall.Application.Importing;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Importers.Native;

/// <summary>Stable reason codes reported by <see cref="LocalVideoImporter"/>.</summary>
public static class LocalVideoImportDiagnostics
{
    public const string PathInvalid = "source.path_invalid";
    public const string NetworkPath = "source.network_path";
    public const string NotFound = "source.not_found";
    public const string ReparsePoint = "source.reparse_point";
    public const string UnsupportedExtension = "source.unsupported_extension";
    public const string Unreadable = "source.unreadable";
    public const string Empty = "source.empty";
    public const string TooLarge = "source.too_large";
    public const string SignatureMismatch = "source.signature_mismatch";
}

/// <summary>
/// Wraps one user-selected local video file into a native package: <c>wallpaper.json</c> plus
/// <c>content/video.&lt;ext&gt;</c>. The container is identified by its signature, never by the extension
/// alone, and the signature is checked on the same handle the bytes are copied from. The id carries the
/// media SHA-256 (<see cref="LocalVideoIdentity"/>), so the same bytes always map to the same wallpaper.
/// </summary>
public sealed class LocalVideoImporter : IWallpaperImporter
{
    public const string ImporterId = "livewall.native.local-video";
    public const long DefaultMaximumBytes = 8L * 1024 * 1024 * 1024;

    // Format v1 names; the library re-reads and validates what is written here.
    private const string ManifestFileName = "wallpaper.json";
    private const int DefaultFramesPerSecond = 30;
    private const int SignatureLength = 12;
    private const int MaximumTitleLength = 200;
    private const string FallbackTitle = "Local video";
    private const int CopyBufferBytes = 1024 * 1024;

    private static readonly CompatibilityReport NativeCompatibility = new(CompatibilityGrade.Native, []);

    private static ReadOnlySpan<byte> EbmlMagic => [0x1A, 0x45, 0xDF, 0xA3];
    private readonly long maximumBytes;

    public LocalVideoImporter(long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.maximumBytes = maximumBytes;
    }

    public string Id => ImporterId;

    public async ValueTask<ImportProbeResult> ProbeAsync(ImportSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryResolveSource(source.Path, out string? path, out string? extension, out string? error))
        {
            return Unrecognized(error);
        }

        FileStream? stream = TryOpenSource(path);
        if (stream is null)
        {
            return Unrecognized(LocalVideoImportDiagnostics.Unreadable);
        }

        await using (stream.ConfigureAwait(false))
        {
            if (CheckLength(stream.Length) is { } lengthError)
            {
                return Unrecognized(lengthError);
            }

            byte[] header = new byte[SignatureLength];
            int read = await stream.ReadAtLeastAsync(header, SignatureLength, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            return Detect(header.AsSpan(0, read)) is { } container && Accepts(extension, container)
                ? new ImportProbeResult(true, ImporterId, Describe(container), NativeCompatibility, [])
                : Unrecognized(LocalVideoImportDiagnostics.SignatureMismatch);
        }
    }

    public async Task<ImportResult> ImportAsync(
        ImportRequest request,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        string staging = RequireEmptyStaging(request.StagingDirectory);
        if (!TryResolveSource(request.Source.Path, out string? path, out string? extension, out string? error))
        {
            return Failed(error);
        }

        FileStream? source = TryOpenSource(path);
        if (source is null)
        {
            return Failed(LocalVideoImportDiagnostics.Unreadable);
        }

        await using (source.ConfigureAwait(false))
        {
            if (CheckLength(source.Length) is { } lengthError)
            {
                return Failed(lengthError);
            }

            // Re-checked on the handle that is copied: a file swapped after Probe is caught here, and
            // FileShare.Read keeps writers out until the copy is complete.
            byte[] header = new byte[SignatureLength];
            int headerLength = await source.ReadAtLeastAsync(
                    header,
                    SignatureLength,
                    throwOnEndOfStream: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (Detect(header.AsSpan(0, headerLength)) is not { } container || !Accepts(extension, container))
            {
                return Failed(LocalVideoImportDiagnostics.SignatureMismatch);
            }

            string entryPoint = LocalVideoIdentity.EntryPointFor(extension);
            string mediaPath = Path.Combine(staging, entryPoint.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            string? mediaHash = await CopyAsync(
                    source,
                    header.AsMemory(0, headerLength),
                    mediaPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (mediaHash is null)
            {
                return Failed(LocalVideoImportDiagnostics.TooLarge);
            }

            WallpaperDefinition definition = new(
                LocalVideoIdentity.FromMediaHash(mediaHash),
                LocalVideoIdentity.Version,
                WallpaperKind.Video,
                entryPoint,
                new WallpaperMetadata(TitleFor(request.Source, path), null, null, null, new HashSet<string>()),
                new WallpaperCapabilities(false, false, false, false, DefaultFramesPerSecond),
                WallpaperOrigin.Native);
            await WriteManifestAsync(Path.Combine(staging, ManifestFileName), definition, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new ImportProgress("Completed", 1));
            return new ImportResult(
                true,
                new StagedWallpaper(definition, staging, NativeCompatibility),
                NativeCompatibility,
                []);
        }
    }

    // Only drive-letter paths: UNC, device (\\?\, \\.\), relative and alternate-data-stream paths are refused.
    private static bool TryResolveSource(
        string? input,
        [NotNullWhen(true)] out string? path,
        [NotNullWhen(true)] out string? extension,
        [NotNullWhen(false)] out string? error)
    {
        path = null;
        extension = null;
        if (input is not { Length: >= 3 } ||
            !char.IsAsciiLetter(input[0]) || input[1] != ':' || input[2] is not ('\\' or '/') ||
            input.IndexOf(':', 2) >= 0 || input.Any(char.IsControl))
        {
            error = LocalVideoImportDiagnostics.PathInvalid;
            return false;
        }

        try
        {
            string full = Path.GetFullPath(input);
            string candidateExtension = Path.GetExtension(full);
            if (!NativeVideoFormats.EntryExtensions.Contains(candidateExtension))
            {
                error = LocalVideoImportDiagnostics.UnsupportedExtension;
                return false;
            }

            if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            {
                error = LocalVideoImportDiagnostics.NetworkPath;
                return false;
            }

            if (!File.Exists(full))
            {
                error = LocalVideoImportDiagnostics.NotFound;
                return false;
            }

            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                error = LocalVideoImportDiagnostics.ReparsePoint;
                return false;
            }

            path = full;
            extension = candidateExtension;
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            error = LocalVideoImportDiagnostics.Unreadable;
            return false;
        }
    }

    private static FileStream? TryOpenSource(string path)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? CheckLength(long length) => length switch
    {
        0 => LocalVideoImportDiagnostics.Empty,
        _ when length > maximumBytes => LocalVideoImportDiagnostics.TooLarge,
        _ => null,
    };

    /// <returns>Lower-case media SHA-256, or <see langword="null"/> when the source grew past the limit.</returns>
    private async Task<string?> CopyAsync(
        FileStream source,
        ReadOnlyMemory<byte> header,
        string destination,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken)
    {
        double expected = Math.Max(source.Length, 1);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream output = new(destination, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
        hash.AppendData(header.Span);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        long copied = header.Length;
        double reported = 0;
        byte[] buffer = new byte[CopyBufferBytes];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > maximumBytes)
            {
                return null;
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            double fraction = Math.Min(1, copied / expected);
            if (fraction - reported >= 0.01)
            {
                reported = fraction;
                progress.Report(new ImportProgress("Copying", fraction));
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task WriteManifestAsync(
        string path,
        WallpaperDefinition definition,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("id", definition.Id.Value);
            writer.WriteString("version", definition.Version);
            writer.WriteString("title", definition.Metadata.Title);
            writer.WriteString("type", "video");
            writer.WriteString("entry", definition.EntryPoint);
            writer.WriteStartObject("permissions");
            writer.WriteBoolean("network", false);
            writer.WriteBoolean("audioCapture", false);
            writer.WriteBoolean("pointerInput", false);
            writer.WriteBoolean("systemMetrics", false);
            writer.WriteEndObject();
            writer.WriteStartObject("defaults");
            writer.WriteString("fit", "cover");
            writer.WriteNumber("fpsLimit", DefaultFramesPerSecond);
            writer.WriteBoolean("muted", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        stream.Flush(flushToDisk: true);
    }

    private static string RequireEmptyStaging(string stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory) ||
            !Directory.Exists(stagingDirectory) ||
            Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
        {
            throw new ArgumentException("An existing, empty staging directory is required.", nameof(stagingDirectory));
        }

        return Path.GetFullPath(stagingDirectory);
    }

    // The title is shown to the user, so drop characters that can hide or reorder text.
    private static string TitleFor(ImportSource source, string resolvedPath)
    {
        string name = Path.GetFileNameWithoutExtension(source.DeclaredFileName ?? resolvedPath);
        StringBuilder title = new(Math.Min(name.Length, MaximumTitleLength));
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (title.Length + rune.Utf16SequenceLength > MaximumTitleLength)
            {
                break;
            }

            title.Append(rune.ToString());
        }

        string trimmed = title.ToString().Trim();
        return trimmed.Length == 0 ? FallbackTitle : trimmed;
    }

    private static VideoContainer? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length < SignatureLength)
        {
            return null;
        }

        if (header[..4].SequenceEqual(EbmlMagic))
        {
            return VideoContainer.Matroska;
        }

        if (header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVI "u8))
        {
            return VideoContainer.Avi;
        }

        if (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))
        {
            return VideoContainer.Gif;
        }

        ReadOnlySpan<byte> box = header[4..8];
        if (box.SequenceEqual("ftyp"u8))
        {
            return VideoContainer.IsoBaseMedia;
        }

        return box.SequenceEqual("moov"u8) || box.SequenceEqual("mdat"u8) || box.SequenceEqual("wide"u8) ||
            box.SequenceEqual("free"u8) || box.SequenceEqual("skip"u8) || box.SequenceEqual("pnot"u8)
            ? VideoContainer.QuickTime
            : null;
    }

    private static bool Accepts(string extension, VideoContainer container) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => container == VideoContainer.IsoBaseMedia,
        ".mov" => container is VideoContainer.IsoBaseMedia or VideoContainer.QuickTime,
        ".webm" or ".mkv" => container == VideoContainer.Matroska,
        ".avi" => container == VideoContainer.Avi,
        ".gif" => container == VideoContainer.Gif,
        _ => false,
    };

    private static string Describe(VideoContainer container) => container switch
    {
        VideoContainer.IsoBaseMedia => "iso-bmff",
        VideoContainer.QuickTime => "quicktime",
        VideoContainer.Matroska => "matroska",
        VideoContainer.Avi => "avi",
        _ => "gif",
    };

    private static ImportProbeResult Unrecognized(string reason) => new(false, null, null, null, [reason]);

    private static ImportResult Failed(string reason) =>
        new(false, null, new CompatibilityReport(CompatibilityGrade.Unsupported, []), [reason]);

    private enum VideoContainer
    {
        IsoBaseMedia,
        QuickTime,
        Matroska,
        Avi,
        Gif,
    }
}
