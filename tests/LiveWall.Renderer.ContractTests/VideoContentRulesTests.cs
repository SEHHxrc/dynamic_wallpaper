using FluentAssertions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Renderer.Video.Protocol;

namespace LiveWall.Renderer.ContractTests;

public sealed class VideoContentRulesTests
{
    // The Renderer only references Contracts, so it keeps its own copy of the entry rule.
    [Fact]
    public void RendererAcceptsExactlyTheVideoEntryExtensionsTheLibraryStores() =>
        VideoContentPath.Extensions.Should().BeEquivalentTo(NativeVideoFormats.EntryExtensions);
}
