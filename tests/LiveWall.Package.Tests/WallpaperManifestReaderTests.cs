using System.Text;
using FluentAssertions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.Packages;

namespace LiveWall.Package.Tests;

public sealed class WallpaperManifestReaderTests
{
    private const string ValidEntry = "content/wallpaper.mp4";

    [Fact]
    public void ValidManifestMapsToDefinitionFields()
    {
        WallpaperManifest manifest = Parse(Manifest());

        manifest.Id.Should().Be(new WallpaperId("com.example.aurora"));
        manifest.Version.Should().Be("1.0.0");
        manifest.Kind.Should().Be(WallpaperKind.Video);
        manifest.EntryPoint.Should().Be(ValidEntry);
        manifest.Metadata.Title.Should().Be("Aurora");
        manifest.Capabilities.PreferredFramesPerSecond.Should().Be(30);
        manifest.ToDefinition(WallpaperOrigin.Native).Origin.Should().Be(WallpaperOrigin.Native);
    }

    [Theory]
    [InlineData("content/../wallpaper.json")]
    [InlineData("content/a/../../wallpaper.json")]
    [InlineData("content/./a.mp4")]
    [InlineData("content//a.mp4")]
    [InlineData("content/")]
    [InlineData("web/index.html")]
    [InlineData("wallpaper.json")]
    [InlineData("C:/content/a.mp4")]
    [InlineData("content/a:b.mp4")]
    public void UnsafeOrMisplacedVideoEntryIsRejected(string entry)
    {
        Action parse = () => Parse(Manifest(entry: entry));

        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("Com.Example")]
    [InlineData("single")]
    [InlineData("../escape.id")]
    [InlineData("com.example\\n")]
    public void InvalidIdIsRejected(string id)
    {
        Action parse = () => Parse(Manifest(id: id));

        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0\\n")]
    public void InvalidVersionIsRejected(string version)
    {
        Action parse = () => Parse(Manifest(version: version));

        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1}""")]
    public void MalformedDocumentIsRejected(string json)
    {
        Action parse = () => Parse(json);

        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void UnknownRootPropertyIsRejected()
    {
        string json = Manifest().Replace("\"title\"", "\"extra\":1,\"title\"", StringComparison.Ordinal);
        Action parse = () => Parse(json);

        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("scene")]
    [InlineData("external")]
    public void TypesNotRunnableInFormatV1AreRejected(string type)
    {
        Action parse = () => Parse(Manifest(type: type));

        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("241")]
    [InlineData("\"30\"")]
    public void OutOfRangeFpsLimitIsRejected(string fps)
    {
        Action parse = () => Parse(Manifest(fps: fps));

        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void OversizedManifestIsRejectedBeforeParsing()
    {
        byte[] json = new byte[WallpaperManifestReader.MaximumManifestBytes + 1];
        Action parse = () => WallpaperManifestReader.Parse(json);

        parse.Should().Throw<InvalidDataException>();
    }

    private static WallpaperManifest Parse(string json) =>
        WallpaperManifestReader.Parse(Encoding.UTF8.GetBytes(json));

    private static string Manifest(
        string id = "com.example.aurora",
        string version = "1.0.0",
        string type = "video",
        string entry = ValidEntry,
        string fps = "30") => $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "version": "{{version}}",
          "title": "Aurora",
          "type": "{{type}}",
          "entry": "{{entry}}",
          "permissions": { "network": false, "audioCapture": false, "pointerInput": false, "systemMetrics": false },
          "defaults": { "fit": "cover", "fpsLimit": {{fps}}, "muted": true }
        }
        """;
}
