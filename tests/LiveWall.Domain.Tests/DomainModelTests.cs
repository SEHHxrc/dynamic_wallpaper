using FluentAssertions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Domain.Tests;

public sealed class DomainModelTests
{
    [Theory]
    [InlineData(0, 1080)]
    [InlineData(-1, 1080)]
    [InlineData(1920, 0)]
    [InlineData(1920, -1)]
    public void DisplayBoundsRejectsNonPositiveDimensions(int width, int height)
    {
        Action create = () => _ = new DisplayBounds(0, 0, width, height);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DisplayBoundsUnionSupportsNegativeVirtualDesktopCoordinates()
    {
        DisplayBounds left = new(-1920, 0, 1920, 1080);
        DisplayBounds right = new(0, -240, 2560, 1440);

        DisplayBounds result = DisplayBounds.Union([left, right]);

        result.Should().Be(new DisplayBounds(-1920, -240, 4480, 1440));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdentityValuesRejectMissingText(string? value)
    {
        Action display = () => _ = new DisplayId(value!);
        Action wallpaper = () => _ = new WallpaperId(value!);
        Action session = () => _ = new SessionId(value!);

        display.Should().Throw<ArgumentException>();
        wallpaper.Should().Throw<ArgumentException>();
        session.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(241, 1)]
    [InlineData(30, 0)]
    [InlineData(30, 31)]
    public void PlaybackPolicyRejectsUnsafeFrameRates(int defaultFps, int throttledFps)
    {
        Action create = () => _ = new UserPlaybackPolicy(
            true,
            true,
            true,
            false,
            true,
            defaultFps,
            throttledFps);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
