using FluentAssertions;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Domain.Tests;

public sealed class NativeVideoTests
{
    private const string Digest = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void LocalVideoIdentityCarriesTheLowerCaseMediaDigest()
    {
        WallpaperId id = LocalVideoIdentity.FromMediaHash(Digest);

        id.Value.Should().Be(LocalVideoIdentity.Prefix + Digest.ToLowerInvariant());
        LocalVideoIdentity.IsReserved(id).Should().BeTrue();
        LocalVideoIdentity.TryGetMediaHash(id, out string? hash).Should().BeTrue();
        hash.Should().Be(Digest.ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void LocalVideoIdentityRejectsAnythingButASha256Digest(string digest)
    {
        Action create = () => LocalVideoIdentity.FromMediaHash(digest);

        create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("com.example.video")]
    [InlineData("local.video.abc")]
    [InlineData("local.video.0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void OnlyACanonicalLocalVideoIdYieldsAMediaDigest(string value)
    {
        LocalVideoIdentity.TryGetMediaHash(new WallpaperId(value), out string? hash).Should().BeFalse();
        hash.Should().BeNull();
    }

    [Theory]
    [InlineData("content/video.mp4", true)]
    [InlineData("content/video.webm", true)]
    [InlineData("content/video.MP4", false)]
    [InlineData("content/clip.mp4", false)]
    [InlineData("content/videos.mp4", false)]
    [InlineData("content/video.exe", false)]
    [InlineData("web/video.mp4", false)]
    public void LocalVideoEntryPointHasOneFixedLowerCaseShape(string entryPoint, bool expected) =>
        LocalVideoIdentity.IsEntryPoint(entryPoint).Should().Be(expected);

    [Fact]
    public void EntryPointsAreNormalizedToALowerCaseExtension()
    {
        LocalVideoIdentity.EntryPointFor(".MKV").Should().Be("content/video.mkv");
        NativeVideoFormats.EntryExtensions.Should().Contain(".WEBM");
        FluentActions.Invoking(() => LocalVideoIdentity.EntryPointFor(".exe")).Should().Throw<ArgumentException>();
    }
}
