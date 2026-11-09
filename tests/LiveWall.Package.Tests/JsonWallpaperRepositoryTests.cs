using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using LiveWall.Application.Importing;
using LiveWall.Application.Library;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.Packages;
using LiveWall.Infrastructure.Persistence;

namespace LiveWall.Package.Tests;

public sealed class JsonWallpaperRepositoryTests : IDisposable
{
    private const string Id = "com.example.aurora";
    private const string IndexFileName = ".index.json";
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), $"livewall-library-tests-{Guid.NewGuid():N}");
    private readonly string libraryPath;
    private readonly JsonWallpaperRepository repository;
    private readonly List<string> junctions = [];

    public JsonWallpaperRepositoryTests()
    {
        libraryPath = Path.Combine(rootPath, "library");
        Directory.CreateDirectory(rootPath);
        repository = new JsonWallpaperRepository(libraryPath);
    }

    [Fact]
    public async Task EmptyLibraryReadsWithoutCreatingAnything()
    {
        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None)).Should().BeEmpty();

        Directory.Exists(libraryPath).Should().BeFalse();
    }

    [Fact]
    public async Task SavedWallpaperIsFoundFromANewInstanceWithCanonicalRoot()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);

        using JsonWallpaperRepository reopened = new(libraryPath);
        WallpaperLibraryEntry? entry = await reopened.FindAsync(new WallpaperId(Id), CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.CanonicalContentRoot.Should().Be(Path.Combine(libraryPath, Id, "1.0.0"));
        entry.Definition.EntryPoint.Should().Be("content/wallpaper.mp4");
        entry.Definition.Origin.Should().Be(WallpaperOrigin.Native);
        entry.Compatibility.Grade.Should().Be(CompatibilityGrade.Native);
        File.ReadAllBytes(Path.Combine(entry.CanonicalContentRoot, "content", "wallpaper.mp4"))
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task CompatibilityReportSurvivesAPersistenceRoundTrip()
    {
        CompatibilityReport report = new(
            CompatibilityGrade.Partial,
            [new CompatibilityIssue("web.audio", CompatibilitySeverity.Warning, "Audio is dropped.", "audio")]);
        await repository.SaveImportedAsync(
            await StageAsync(compatibility: report, origin: WallpaperOrigin.WallpaperEngineProject),
            CancellationToken.None);

        WallpaperLibraryEntry? entry = await repository.FindAsync(new WallpaperId(Id), CancellationToken.None);

        entry!.Compatibility.Should().BeEquivalentTo(report);
        entry.Definition.Origin.Should().Be(WallpaperOrigin.WallpaperEngineProject);
    }

    [Fact]
    public async Task SavingCopiesSoTheStagedSourceStaysOwnedByTheCaller()
    {
        ImportedWallpaper staged = await StageAsync();

        await repository.SaveImportedAsync(staged, CancellationToken.None);

        File.Exists(Path.Combine(staged.StagedContentDirectory, "wallpaper.json")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(libraryPath, ".staging")).Should().BeEmpty();
    }

    [Fact]
    public async Task ResavingIdenticalContentIsANoOp()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);
        string index = await File.ReadAllTextAsync(Path.Combine(libraryPath, IndexFileName));

        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);

        (await File.ReadAllTextAsync(Path.Combine(libraryPath, IndexFileName))).Should().Be(index);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-RC")]
    public async Task ACommittedVersionIsImmutable(string version)
    {
        await repository.SaveImportedAsync(
            await StageAsync(version: version.ToLowerInvariant()),
            CancellationToken.None);

        Func<Task> save = async () => await repository.SaveImportedAsync(
            await StageAsync(version: version, media: [9, 9]),
            CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllBytes(Path.Combine(libraryPath, Id, version.ToLowerInvariant(), "content", "wallpaper.mp4"))
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ANewVersionReplacesTheOldOneOnlyAfterCommit()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);

        await repository.SaveImportedAsync(await StageAsync(version: "2.0.0", media: [7]), CancellationToken.None);

        WallpaperLibraryEntry? entry = await repository.FindAsync(new WallpaperId(Id), CancellationToken.None);
        entry!.Definition.Version.Should().Be("2.0.0");
        Directory.Exists(Path.Combine(libraryPath, Id, "1.0.0")).Should().BeFalse();
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task WrongDeclaredHashLeavesNoVisibleEntry()
    {
        ImportedWallpaper staged = await StageAsync();

        Func<Task> save = () => repository.SaveImportedAsync(
            staged with { ContentHash = new string('0', 64) },
            CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
        Directory.Exists(Path.Combine(libraryPath, Id)).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(Path.Combine(libraryPath, ".staging")).Should().BeEmpty();
    }

    [Fact]
    public async Task ManifestDisagreeingWithTheDefinitionIsRejected()
    {
        ImportedWallpaper staged = await StageAsync();

        Func<Task> save = () => repository.SaveImportedAsync(
            staged with { Definition = staged.Definition with { EntryPoint = "content/other.mp4" } },
            CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("web", "web/index.html", "web/index.html")]
    [InlineData("image", "content/wallpaper.png", "content/wallpaper.png")]
    [InlineData("video", "content/wallpaper.exe", "content/wallpaper.exe")]
    public async Task ContentOutsideTheVideoSliceIsRejected(string type, string entry, string file)
    {
        ImportedWallpaper staged = await StageAsync(type: type, entry: entry, mediaPath: file);

        Func<Task> save = () => repository.SaveImportedAsync(staged, CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task MissingEntryFileIsRejected()
    {
        ImportedWallpaper staged = await StageAsync(entry: "content/absent.mp4");

        Func<Task> save = () => repository.SaveImportedAsync(staged, CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("\"network\": false", "\"network\": true")]
    [InlineData("\"audioCapture\": false", "\"audioCapture\": true")]
    public async Task VideoContentCannotRequestSensitivePermissions(string original, string replacement)
    {
        ImportedWallpaper staged = await StageAsync(editManifest: json => json.Replace(original, replacement));

        Func<Task> save = () => repository.SaveImportedAsync(staged, CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task UnsupportedGradeAndDelegateOriginAreRejected()
    {
        ImportedWallpaper unsupported = await StageAsync(
            compatibility: new CompatibilityReport(CompatibilityGrade.Unsupported, []));
        ImportedWallpaper delegated = await StageAsync(origin: WallpaperOrigin.WallpaperEngineDelegate);

        await FluentActions.Awaiting(() => repository.SaveImportedAsync(unsupported, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Awaiting(() => repository.SaveImportedAsync(delegated, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task IdsThatWouldCollideWithReservedDeviceNamesAreRejected()
    {
        ImportedWallpaper staged = await StageAsync(id: "con.example");

        Func<Task> save = () => repository.SaveImportedAsync(staged, CancellationToken.None);

        await save.Should().ThrowAsync<IOException>();
        File.Exists(Path.Combine(libraryPath, IndexFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task AnIdNamedLikeTheIndexCannotCollideWithIt()
    {
        await repository.SaveImportedAsync(await StageAsync(id: "index.json"), CancellationToken.None);

        (await repository.FindAsync(new WallpaperId("index.json"), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ContentTamperedAfterCommitFailsClosedOnRead()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);

        File.Delete(Path.Combine(libraryPath, Id, "1.0.0", "content", "wallpaper.mp4"));

        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ManifestIdentityChangedAfterCommitFailsClosedOnRead()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);
        string manifest = Path.Combine(libraryPath, Id, "1.0.0", WallpaperManifestReader.FileName);

        await File.WriteAllTextAsync(
            manifest,
            (await File.ReadAllTextAsync(manifest)).Replace(Id, "com.example.other"));

        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":2,"wallpapers":[]}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"wallpapers":[null]}""")]
    [InlineData("""{"schemaVersion":1,"wallpapers":[],"extra":1}""")]
    [InlineData("""{"schemaVersion":1,"wallpapers":[{"id":"../../evil.id","version":"1.0.0","contentHash":"00","origin":"native","grade":"native"}]}""")]
    public async Task InvalidIndexReadsAsEmptyAndIsNeverOverwritten(string content)
    {
        Directory.CreateDirectory(libraryPath);
        string index = Path.Combine(libraryPath, IndexFileName);
        await File.WriteAllTextAsync(index, content);

        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
        (await repository.ListAsync(new WallpaperQuery(), CancellationToken.None)).Should().BeEmpty();
        await FluentActions.Awaiting(async () => await repository.SaveImportedAsync(
                await StageAsync(),
                CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Awaiting(() => repository.RemoveAsync(new WallpaperId(Id), CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();

        (await File.ReadAllTextAsync(index)).Should().Be(content);
    }

    [Fact]
    public async Task IndexedPathEscapingTheLibraryIsNeverOpened()
    {
        string outside = Path.Combine(rootPath, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(libraryPath);
        string json = $$"""
            {"schemaVersion":1,"wallpapers":[{"id":"a.b","version":"1.0.0+..","contentHash":"{{new string('0', 64)}}","origin":"native","grade":"native"}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(libraryPath, IndexFileName), json);

        (await repository.FindAsync(new WallpaperId("a.b"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ReparsePointInStagedContentIsRejected()
    {
        ImportedWallpaper staged = await StageAsync();
        CreateJunction(Path.Combine(staged.StagedContentDirectory, "content", "link"));

        Func<Task> save = () => repository.SaveImportedAsync(staged, CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task EntryReachedThroughAReparsePointFailsClosedOnRead()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);
        string content = Path.Combine(libraryPath, Id, "1.0.0", "content");
        Directory.Delete(content, recursive: true);
        string outside = CreateJunction(content);
        await File.WriteAllBytesAsync(Path.Combine(outside, "wallpaper.mp4"), [1, 2, 3]);

        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RemoveIsIdempotentAndDeletesContent()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);

        await repository.RemoveAsync(new WallpaperId(Id), CancellationToken.None);
        await repository.RemoveAsync(new WallpaperId(Id), CancellationToken.None);

        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
        Directory.Exists(Path.Combine(libraryPath, Id)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveCommitsEvenWhenTheContentIsStillOpen()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);
        string media = Path.Combine(libraryPath, Id, "1.0.0", "content", "wallpaper.mp4");

        using (File.Open(media, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await repository.RemoveAsync(new WallpaperId(Id), CancellationToken.None);
        }

        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task UnindexedLeftoversAreInvisibleAndClearedByAReimport()
    {
        string orphan = Path.Combine(libraryPath, Id, "1.0.0");
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "stale.bin"), "stale");
        string interrupted = Path.Combine(libraryPath, ".staging", "interrupted");
        Directory.CreateDirectory(interrupted);

        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);

        File.Exists(Path.Combine(orphan, "stale.bin")).Should().BeFalse();
        Directory.Exists(interrupted).Should().BeFalse();
        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task CancelledSaveLeavesNoVisibleEntry()
    {
        ImportedWallpaper staged = await StageAsync();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> save = () => repository.SaveImportedAsync(staged, cancelled.Token);

        await save.Should().ThrowAsync<OperationCanceledException>();
        (await repository.FindAsync(new WallpaperId(Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task QueryFiltersAndPagesInStableOrder()
    {
        await repository.SaveImportedAsync(await StageAsync(id: "com.example.beta", title: "Beta"), CancellationToken.None);
        await repository.SaveImportedAsync(await StageAsync(id: "com.example.alpha", title: "Alpha"), CancellationToken.None);

        (await repository.ListAsync(new WallpaperQuery(Text: "ALPH"), CancellationToken.None))
            .Should().ContainSingle().Which.Definition.Id.Value.Should().Be("com.example.alpha");
        (await repository.ListAsync(new WallpaperQuery(Kind: WallpaperKind.Web), CancellationToken.None))
            .Should().BeEmpty();
        (await repository.ListAsync(new WallpaperQuery(Origin: WallpaperOrigin.Native), CancellationToken.None))
            .Should().HaveCount(2);
        (await repository.ListAsync(new WallpaperQuery(Offset: 1, Limit: 1), CancellationToken.None))
            .Should().ContainSingle().Which.Definition.Id.Value.Should().Be("com.example.beta");
        await FluentActions.Awaiting(() => repository.ListAsync(new WallpaperQuery(Limit: 0), CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ContentHashCoversPathsAndBytesIndependentlyOfEnumerationOrder()
    {
        string first = await WriteTreeAsync("first", ("a.bin", [1]), ("b/c.bin", [2]));
        string same = await WriteTreeAsync("same", ("b/c.bin", [2]), ("a.bin", [1]));
        string renamed = await WriteTreeAsync("renamed", ("a2.bin", [1]), ("b/c.bin", [2]));
        string edited = await WriteTreeAsync("edited", ("a.bin", [3]), ("b/c.bin", [2]));

        string hash = await WallpaperContentTree.ComputeHashAsync(first, CancellationToken.None);

        hash.Should().MatchRegex("^[0-9A-F]{64}$");
        (await WallpaperContentTree.ComputeHashAsync(same, CancellationToken.None)).Should().Be(hash);
        (await WallpaperContentTree.ComputeHashAsync(renamed, CancellationToken.None)).Should().NotBe(hash);
        (await WallpaperContentTree.ComputeHashAsync(edited, CancellationToken.None)).Should().NotBe(hash);
    }

    [Fact]
    public async Task AnUnreadableCommittedVersionIsRepairedByImportingItAgain()
    {
        await repository.SaveImportedAsync(await StageAsync(), CancellationToken.None);
        File.Delete(Path.Combine(libraryPath, Id, "1.0.0", "content", "wallpaper.mp4"));

        await repository.SaveImportedAsync(await StageAsync(media: [4, 5]), CancellationToken.None);

        WallpaperLibraryEntry? entry = await repository.FindAsync(new WallpaperId(Id), CancellationToken.None);
        entry.Should().NotBeNull();
        File.ReadAllBytes(Path.Combine(entry!.CanonicalContentRoot, "content", "wallpaper.mp4")).Should().Equal(4, 5);
    }

    [Fact]
    public async Task AReservedLocalVideoIdentityIsAcceptedWithItsOwnMedia()
    {
        byte[] media = [9, 8, 7];
        string id = LocalVideoIdentity.FromMediaHash(Convert.ToHexString(SHA256.HashData(media))).Value;

        await repository.SaveImportedAsync(await StageLocalAsync(id, media), CancellationToken.None);

        (await repository.FindAsync(new WallpaperId(id), CancellationToken.None)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("other-media", "1.0.0", "content/video.mp4", WallpaperOrigin.Native)]
    [InlineData("matching", "2.0.0", "content/video.mp4", WallpaperOrigin.Native)]
    [InlineData("matching", "1.0.0", "content/clip.mp4", WallpaperOrigin.Native)]
    [InlineData("matching", "1.0.0", "content/video.mp4", WallpaperOrigin.WallpaperEngineProject)]
    [InlineData("short-suffix", "1.0.0", "content/video.mp4", WallpaperOrigin.Native)]
    public async Task AForgedReservedLocalVideoIdentityIsRejected(
        string suffix,
        string version,
        string entry,
        WallpaperOrigin origin)
    {
        byte[] media = [9, 8, 7];
        string id = suffix switch
        {
            "matching" => LocalVideoIdentity.FromMediaHash(Convert.ToHexString(SHA256.HashData(media))).Value,
            "other-media" => LocalVideoIdentity.FromMediaHash(Convert.ToHexString(SHA256.HashData([1, 2]))).Value,
            _ => LocalVideoIdentity.Prefix + "abc",
        };

        Func<Task> save = async () => await repository.SaveImportedAsync(
            await StageLocalAsync(id, media, version: version, entry: entry, origin: origin),
            CancellationToken.None);

        await save.Should().ThrowAsync<InvalidDataException>();
        (await repository.FindAsync(new WallpaperId(id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheSameLocalVideoUnderAnotherTitleIsADuplicateThatKeepsTheFirstTitle()
    {
        byte[] media = [9, 8, 7];
        string id = LocalVideoIdentity.FromMediaHash(Convert.ToHexString(SHA256.HashData(media))).Value;
        await repository.SaveImportedAsync(await StageLocalAsync(id, media, title: "First"), CancellationToken.None);

        await repository.SaveImportedAsync(await StageLocalAsync(id, media, title: "Second"), CancellationToken.None);

        WallpaperLibraryEntry? entry = await repository.FindAsync(new WallpaperId(id), CancellationToken.None);
        entry!.Definition.Metadata.Title.Should().Be("First");
    }

    [Fact]
    public async Task ContentDigestReportsEachFileHashAlongsideTheTreeHash()
    {
        string root = await WriteTreeAsync("digest", ("a.bin", [1]), ("b/c.bin", [2]));

        ContentTreeDigest digest = await WallpaperContentTree.ComputeDigestAsync(root, CancellationToken.None);

        digest.TreeHash.Should().Be(await WallpaperContentTree.ComputeHashAsync(root, CancellationToken.None));
        digest.FileHashes.Should().HaveCount(2);
        digest.FileHashes["a.bin"].Should().Be(Convert.ToHexString(SHA256.HashData([1])));
        digest.FileHashes["B/C.BIN"].Should().Be(Convert.ToHexString(SHA256.HashData([2])));
    }

    public void Dispose()
    {
        repository.Dispose();
        // Recursive delete refuses junctions without elevation; a plain delete only unlinks them.
        junctions.ForEach(junction => Directory.Delete(junction));
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    // Junctions need no privilege, unlike symbolic links, so these tests cannot silently skip.
    private string CreateJunction(string link)
    {
        string target = Path.Combine(rootPath, "junction-targets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0);
        junctions.Add(link);
        new DirectoryInfo(link).Attributes.Should().HaveFlag(FileAttributes.ReparsePoint);
        return target;
    }

    private async Task<string> WriteTreeAsync(string name, params (string Path, byte[] Bytes)[] files)
    {
        string root = Path.Combine(rootPath, "trees", name);
        foreach ((string path, byte[] bytes) in files)
        {
            string target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes);
        }

        return root;
    }

    private Task<ImportedWallpaper> StageLocalAsync(
        string id,
        byte[] media,
        string title = "clip",
        string version = LocalVideoIdentity.Version,
        string entry = "content/video.mp4",
        WallpaperOrigin origin = WallpaperOrigin.Native) =>
        StageAsync(id: id, version: version, entry: entry, mediaPath: entry, title: title, media: media, origin: origin);

    private async Task<ImportedWallpaper> StageAsync(
        string id = Id,
        string version = "1.0.0",
        string type = "video",
        string entry = "content/wallpaper.mp4",
        string mediaPath = "content/wallpaper.mp4",
        string title = "Aurora",
        byte[]? media = null,
        CompatibilityReport? compatibility = null,
        WallpaperOrigin origin = WallpaperOrigin.Native,
        Func<string, string>? editManifest = null)
    {
        string manifest = $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "version": "{{version}}",
              "title": "{{title}}",
              "type": "{{type}}",
              "entry": "{{entry}}",
              "permissions": { "network": false, "audioCapture": false, "pointerInput": false, "systemMetrics": false },
              "defaults": { "fit": "cover", "fpsLimit": 30, "muted": true }
            }
            """;
        string staged = Path.Combine(rootPath, "staged", Guid.NewGuid().ToString("N"));
        string mediaFile = Path.Combine(staged, mediaPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(mediaFile)!);
        await File.WriteAllBytesAsync(mediaFile, media ?? [1, 2, 3]);
        await File.WriteAllTextAsync(
            Path.Combine(staged, WallpaperManifestReader.FileName),
            editManifest?.Invoke(manifest) ?? manifest);
        return new ImportedWallpaper(
            new WallpaperDefinition(
                new WallpaperId(id),
                version,
                type switch
                {
                    "web" => WallpaperKind.Web,
                    "image" => WallpaperKind.Image,
                    _ => WallpaperKind.Video,
                },
                entry,
                new WallpaperMetadata(title, null, null, null, new HashSet<string>()),
                new WallpaperCapabilities(false, false, false, false, 30),
                origin),
            staged,
            await WallpaperContentTree.ComputeHashAsync(staged, CancellationToken.None),
            compatibility ?? new CompatibilityReport(CompatibilityGrade.Native, []));
    }
}
