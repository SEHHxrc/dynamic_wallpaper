using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LiveWall.Application.Importing;
using LiveWall.Application.Library;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;
using LiveWall.Importers.Native;
using LiveWall.Infrastructure.Packages;
using LiveWall.Infrastructure.Persistence;

namespace LiveWall.Package.Tests;

public sealed class LocalVideoImporterTests : IDisposable
{
    private static readonly byte[] Mp4Header = [0, 0, 0, 0x18, .. "ftypisom"u8, 0, 0, 0, 0];
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), $"livewall-import-tests-{Guid.NewGuid():N}");
    private readonly LocalVideoImporter importer = new();

    public LocalVideoImporterTests() => Directory.CreateDirectory(rootPath);

    public static TheoryData<string, byte[], string> Signatures => new()
    {
        { ".mp4", Mp4Header, "iso-bmff" },
        { ".m4v", Mp4Header, "iso-bmff" },
        { ".mov", Mp4Header, "iso-bmff" },
        { ".mov", [0, 0, 0, 8, .. "wide"u8, 0, 0, 0, 0], "quicktime" },
        { ".mkv", [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0], "matroska" },
        { ".webm", [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0], "matroska" },
        { ".avi", [.. "RIFF"u8, 0, 0, 0, 0, .. "AVI "u8], "avi" },
        { ".gif", [.. "GIF89a"u8, 0, 0, 0, 0, 0, 0], "gif" },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public async Task ProbeRecognizesEachSupportedContainerBySignature(string extension, byte[] header, string format)
    {
        string source = WriteSource($"clip{extension}", [.. header, .. RandomNumberGenerator.GetBytes(32)]);

        ImportProbeResult probe = await importer.ProbeAsync(new ImportSource(source), CancellationToken.None);

        probe.Recognized.Should().BeTrue();
        probe.ImporterId.Should().Be(LocalVideoImporter.ImporterId);
        probe.DetectedFormat.Should().Be(format);
        probe.PredictedCompatibility!.Grade.Should().Be(CompatibilityGrade.Native);
    }

    [Theory]
    [InlineData("clip.mp4", "RIFF")]
    [InlineData("clip.avi", "ftyp")]
    [InlineData("clip.mkv", "GIF8")]
    public async Task ExtensionWithTheWrongSignatureIsRejected(string name, string magic)
    {
        byte[] bytes = new byte[64];
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, magic == "ftyp" ? 4 : 0);
        string source = WriteSource(name, bytes);

        ImportProbeResult probe = await importer.ProbeAsync(new ImportSource(source), CancellationToken.None);

        probe.Recognized.Should().BeFalse();
        probe.Warnings.Should().Equal(LocalVideoImportDiagnostics.SignatureMismatch);
    }

    [Theory]
    [InlineData("clip.exe", LocalVideoImportDiagnostics.UnsupportedExtension)]
    [InlineData("clip.html", LocalVideoImportDiagnostics.UnsupportedExtension)]
    [InlineData("missing.mp4", LocalVideoImportDiagnostics.NotFound)]
    public async Task UnsupportedOrMissingSourcesAreRejected(string name, string reason)
    {
        string source = name == "missing.mp4" ? Path.Combine(rootPath, name) : WriteSource(name, Mp4Header);

        ImportProbeResult probe = await importer.ProbeAsync(new ImportSource(source), CancellationToken.None);

        probe.Recognized.Should().BeFalse();
        probe.Warnings.Should().Equal(reason);
    }

    [Theory]
    [InlineData(@"\\server\share\clip.mp4")]
    [InlineData(@"\\?\C:\clip.mp4")]
    [InlineData(@"\\.\C:\clip.mp4")]
    [InlineData(@"relative\clip.mp4")]
    [InlineData(@"C:clip.mp4")]
    [InlineData(@"C:\clip.mp4:stream")]
    [InlineData("")]
    public async Task NonLocalOrAmbiguousPathsAreRejected(string path)
    {
        ImportProbeResult probe = await importer.ProbeAsync(new ImportSource(path), CancellationToken.None);

        probe.Recognized.Should().BeFalse();
        probe.Warnings.Should().Equal(LocalVideoImportDiagnostics.PathInvalid);
    }

    [Fact]
    public async Task EmptyAndOversizedSourcesAreRejected()
    {
        string empty = WriteSource("empty.mp4", []);
        string large = WriteSource("large.mp4", [.. Mp4Header, .. new byte[64]]);

        (await importer.ProbeAsync(new ImportSource(empty), CancellationToken.None)).Warnings
            .Should().Equal(LocalVideoImportDiagnostics.Empty);
        (await new LocalVideoImporter(maximumBytes: 32).ProbeAsync(new ImportSource(large), CancellationToken.None))
            .Warnings.Should().Equal(LocalVideoImportDiagnostics.TooLarge);
    }

    [Fact]
    public async Task ImportWritesAManifestTheLibraryAcceptsWithAContentAddressedId()
    {
        byte[] media = [.. Mp4Header, .. RandomNumberGenerator.GetBytes(4096)];
        string source = WriteSource("Holiday Clip.MP4", media);
        string staging = CreateStaging();
        List<ImportProgress> progress = [];

        ImportResult result = await importer.ImportAsync(
            new ImportRequest(new ImportSource(source), staging),
            new SynchronousProgress(progress),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        StagedWallpaper staged = result.Wallpaper!;
        string mediaHash = Convert.ToHexStringLower(SHA256.HashData(media));
        staged.Definition.Id.Should().Be(LocalVideoIdentity.FromMediaHash(mediaHash));
        staged.Definition.EntryPoint.Should().Be("content/video.mp4");
        staged.Definition.Metadata.Title.Should().Be("Holiday Clip");
        staged.Compatibility.Grade.Should().Be(CompatibilityGrade.Native);
        File.ReadAllBytes(Path.Combine(staging, "content", "video.mp4")).Should().Equal(media);
        Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(staging, path).Replace('\\', '/'))
            .Should().BeEquivalentTo("content", "content/video.mp4", WallpaperManifestReader.FileName);
        progress.Should().NotBeEmpty();
        progress[^1].Fraction.Should().Be(1);

        WallpaperManifest manifest = WallpaperManifestReader.Parse(
            await File.ReadAllBytesAsync(Path.Combine(staging, WallpaperManifestReader.FileName)));
        manifest.Id.Should().Be(staged.Definition.Id);
        manifest.Kind.Should().Be(WallpaperKind.Video);
        manifest.Capabilities.RequiresNetwork.Should().BeFalse();
        manifest.Capabilities.RequiresAudioCapture.Should().BeFalse();

        using JsonWallpaperRepository library = new(Path.Combine(rootPath, "library"));
        await library.SaveImportedAsync(await PinAsync(staged), CancellationToken.None);
        WallpaperLibraryEntry? entry = await library.FindAsync(staged.Definition.Id, CancellationToken.None);
        entry.Should().NotBeNull();
        entry!.Definition.Metadata.Title.Should().Be("Holiday Clip");
    }

    [Fact]
    public async Task TitleDropsFormattingCharactersAndFallsBackWhenEmpty()
    {
        string bidiSource = WriteSource("clip\u202Egnp.mp4", [.. Mp4Header, 1]);
        string blankSource = WriteSource("\u200B.mp4", [.. Mp4Header, 2]);

        ImportResult bidi = await ImportAsync(bidiSource);
        ImportResult blank = await ImportAsync(blankSource);

        bidi.Wallpaper!.Definition.Metadata.Title.Should().Be("clipgnp");
        blank.Wallpaper!.Definition.Metadata.Title.Should().Be("Local video");
    }

    [Fact]
    public async Task ImportRequiresAnEmptyStagingDirectory()
    {
        string source = WriteSource("clip.mp4", Mp4Header);
        string staging = CreateStaging();
        await File.WriteAllTextAsync(Path.Combine(staging, "stale.txt"), "stale");

        Func<Task> import = () => importer.ImportAsync(
            new ImportRequest(new ImportSource(source), staging),
            new SynchronousProgress([]),
            CancellationToken.None);

        await import.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SourceReplacedAfterProbeIsCheckedAgainOnImport()
    {
        string source = WriteSource("clip.mp4", Mp4Header);
        (await importer.ProbeAsync(new ImportSource(source), CancellationToken.None)).Recognized.Should().BeTrue();
        await File.WriteAllBytesAsync(source, "MZ not a video"u8.ToArray());

        ImportResult result = await ImportAsync(source);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Equal(LocalVideoImportDiagnostics.SignatureMismatch);
    }

    [Fact]
    public async Task CancelledImportThrows()
    {
        string source = WriteSource("clip.mp4", [.. Mp4Header, .. new byte[4096]]);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> import = () => importer.ImportAsync(
            new ImportRequest(new ImportSource(source), CreateStaging()),
            new SynchronousProgress([]),
            cancelled.Token);

        await import.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose() => Directory.Delete(rootPath, recursive: true);

    private async Task<ImportResult> ImportAsync(string source) => await importer.ImportAsync(
        new ImportRequest(new ImportSource(source), CreateStaging()),
        new SynchronousProgress([]),
        CancellationToken.None);

    private static async Task<ImportedWallpaper> PinAsync(StagedWallpaper staged) => new(
        staged.Definition,
        staged.StagedContentDirectory,
        await WallpaperContentTree.ComputeHashAsync(staged.StagedContentDirectory, CancellationToken.None),
        staged.Compatibility);

    private string WriteSource(string name, byte[] bytes)
    {
        string directory = Path.Combine(rootPath, "sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string CreateStaging()
    {
        string staging = Path.Combine(rootPath, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return staging;
    }

    private sealed class SynchronousProgress(List<ImportProgress> values) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => values.Add(value);
    }
}
