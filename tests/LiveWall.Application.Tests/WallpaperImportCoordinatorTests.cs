using System.Security.Cryptography;
using FluentAssertions;
using LiveWall.Application.Importing;
using LiveWall.Application.Library;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Orchestration;
using LiveWall.Importers.Native;
using LiveWall.Infrastructure.Persistence;

namespace LiveWall.Application.Tests;

public sealed class WallpaperImportCoordinatorTests : IDisposable
{
    private static readonly byte[] Mp4Header = [0, 0, 0, 0x18, .. "ftypisom"u8, 0, 0, 0, 0];
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), $"livewall-import-coordinator-{Guid.NewGuid():N}");
    private readonly string stagingRoot;
    private readonly JsonWallpaperRepository repository;

    public WallpaperImportCoordinatorTests()
    {
        Directory.CreateDirectory(rootPath);
        stagingRoot = Path.Combine(rootPath, "cache", "imports");
        repository = new JsonWallpaperRepository(Path.Combine(rootPath, "library"));
    }

    [Fact]
    public async Task ALocalVideoIsCommittedToTheLibraryAndItsStagingIsRemoved()
    {
        byte[] media = [.. Mp4Header, .. RandomNumberGenerator.GetBytes(2048)];

        WallpaperImportResult result = await ImportAsync(WriteSource("Beach.mp4", media));

        result.Succeeded.Should().BeTrue();
        result.AlreadyInLibrary.Should().BeFalse();
        result.ImporterId.Should().Be(LocalVideoImporter.ImporterId);
        result.Compatibility!.Grade.Should().Be(CompatibilityGrade.Native);
        result.WallpaperId.Should().Be(LocalVideoIdentity.FromMediaHash(Convert.ToHexString(SHA256.HashData(media))));
        WallpaperLibraryEntry? entry = await repository.FindAsync(result.WallpaperId!.Value, CancellationToken.None);
        entry!.Definition.Metadata.Title.Should().Be("Beach");
        File.ReadAllBytes(Path.Combine(entry.CanonicalContentRoot, "content", "video.mp4")).Should().Equal(media);
        Directory.EnumerateFileSystemEntries(stagingRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameMediaUnderAnotherNameIsAlreadyInTheLibrary()
    {
        byte[] media = [.. Mp4Header, .. RandomNumberGenerator.GetBytes(512)];
        WallpaperImportResult first = await ImportAsync(WriteSource("First.mp4", media));

        WallpaperImportResult second = await ImportAsync(WriteSource("Second.mp4", media));

        second.Succeeded.Should().BeTrue();
        second.AlreadyInLibrary.Should().BeTrue();
        second.WallpaperId.Should().Be(first.WallpaperId);
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None))
            .Should().ContainSingle().Which.Definition.Metadata.Title.Should().Be("First");
        Directory.EnumerateFileSystemEntries(stagingRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnrecognizedSourceIsRejectedWithTheImportersReason()
    {
        WallpaperImportResult result = await ImportAsync(WriteSource("notes.txt", [1, 2, 3]));

        result.Succeeded.Should().BeFalse();
        result.ImporterId.Should().BeNull();
        result.Diagnostics.Should().Equal(
            WallpaperImportCoordinator.SourceUnrecognized,
            LocalVideoImportDiagnostics.UnsupportedExtension);
        Directory.Exists(stagingRoot).Should().BeFalse();
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task LeftoverStagingFromAnEarlierHostIsClearedBeforeTheFirstImport()
    {
        string leftover = Path.Combine(stagingRoot, "interrupted");
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Combine(leftover, "partial.bin"), "partial");

        (await ImportAsync(WriteSource("clip.mp4", [.. Mp4Header, 1]))).Succeeded.Should().BeTrue();

        Directory.Exists(leftover).Should().BeFalse();
    }

    [Fact]
    public async Task AnImporterFailureIsReportedAndLeavesNoStaging()
    {
        FakeImporter importer = new(_ => new ImportResult(
            false, null, new CompatibilityReport(CompatibilityGrade.Unsupported, []), ["fake.reason"]));

        WallpaperImportResult result = await ImportAsync(WriteSource("clip.mp4", Mp4Header), importer);

        result.Succeeded.Should().BeFalse();
        result.ImporterId.Should().Be(FakeImporter.FakeId);
        result.Diagnostics.Should().Equal("fake.reason");
        Directory.EnumerateFileSystemEntries(stagingRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task AnImporterIoFailureIsReported()
    {
        FakeImporter importer = new(_ => throw new IOException("Simulated disk failure."));

        WallpaperImportResult result = await ImportAsync(WriteSource("clip.mp4", Mp4Header), importer);

        result.Diagnostics.Should().Equal(WallpaperImportCoordinator.ImportIoFailed);
        Directory.EnumerateFileSystemEntries(stagingRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task ContentStagedOutsideTheImportersDirectoryIsRejected()
    {
        string elsewhere = Path.Combine(rootPath, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        FakeImporter importer = new(_ => Staged(elsewhere));

        WallpaperImportResult result = await ImportAsync(WriteSource("clip.mp4", Mp4Header), importer);

        result.Diagnostics.Should().Equal(WallpaperImportCoordinator.StagingMismatch);
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ContentTheLibraryRejectsIsReportedAsRejected()
    {
        FakeImporter importer = new(request =>
        {
            string media = Path.Combine(request.StagingDirectory, "content", "video.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(media)!);
            File.WriteAllBytes(media, Mp4Header);
            return Staged(request.StagingDirectory); // No wallpaper.json: the library must refuse it.
        });

        WallpaperImportResult result = await ImportAsync(WriteSource("clip.mp4", Mp4Header), importer);

        result.Diagnostics.Should().Equal(WallpaperImportCoordinator.LibraryRejected);
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None)).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(stagingRoot).Should().BeEmpty();
    }

    public void Dispose()
    {
        repository.Dispose();
        Directory.Delete(rootPath, recursive: true);
    }

    private Task<WallpaperImportResult> ImportAsync(string source, params IWallpaperImporter[] importers) =>
        new WallpaperImportCoordinator(
                importers.Length == 0 ? [new LocalVideoImporter()] : importers,
                repository,
                stagingRoot)
            .ImportAsync(new ImportSource(source), null, CancellationToken.None);

    private string WriteSource(string name, byte[] bytes)
    {
        string directory = Path.Combine(rootPath, "sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static ImportResult Staged(string directory)
    {
        CompatibilityReport native = new(CompatibilityGrade.Native, []);
        WallpaperDefinition definition = new(
            new WallpaperId("com.example.fake"),
            "1.0.0",
            WallpaperKind.Video,
            "content/video.mp4",
            new WallpaperMetadata("Fake", null, null, null, new HashSet<string>()),
            new WallpaperCapabilities(false, false, false, false, 30),
            WallpaperOrigin.Native);
        return new ImportResult(true, new StagedWallpaper(definition, directory, native), native, []);
    }

    private sealed class FakeImporter(Func<ImportRequest, ImportResult> import) : IWallpaperImporter
    {
        public const string FakeId = "test.fake";

        public string Id => FakeId;

        public ValueTask<ImportProbeResult> ProbeAsync(ImportSource source, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ImportProbeResult(true, FakeId, "fake", null, []));

        public Task<ImportResult> ImportAsync(
            ImportRequest request,
            IProgress<ImportProgress> progress,
            CancellationToken cancellationToken) => Task.FromResult(import(request));
    }
}
