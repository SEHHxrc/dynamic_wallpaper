using System.Text;
using System.Text.Json;
using FluentAssertions;
using LiveWall.Renderer.Video.Mpv;
using LiveWall.VideoPresentationProbe;

namespace LiveWall.Renderer.ContractTests;

public sealed class MpvC0Tests
{
    [Theory]
    [InlineData(false, true, true, 1d, "gpu-next", "d3d11", false)]
    [InlineData(true, false, true, 1d, "gpu-next", "d3d11", false)]
    [InlineData(true, true, false, 1d, "gpu-next", "d3d11", false)]
    [InlineData(true, true, true, -1d, "gpu-next", "d3d11", false)]
    [InlineData(true, true, true, 1d, "null", "d3d11", false)]
    [InlineData(true, true, true, 1d, "gpu-next", "vulkan", false)]
    [InlineData(true, true, true, 1d, "gpu-next", "d3d11", true)]
    public void OutputEvidenceNeverProvesDesktopPresentation(bool loaded, bool restarted, bool configured,
        double position, string vo, string gpu, bool attempted)
    {
        MpvOutputEvidence evidence = new(loaded, restarted, configured, position, vo, gpu, 2);
        evidence.OutputAttempted.Should().Be(attempted);
        evidence.ProvesDesktopPresentation.Should().BeFalse();
        JsonSerializer.Serialize(evidence).Should().Contain("\"ProvesDesktopPresentation\":false");
    }

    [Fact]
    public void MissingTimestampDoesNotBecomeAnOutputAttempt() =>
        new MpvOutputEvidence(true, true, true, null, "gpu-next", "d3d11", 1).OutputAttempted.Should().BeFalse();

    [Theory]
    [InlineData(2880, 1800, -160d, -160d, 0d, 0d, true)]
    [InlineData(2880, 1800, 0d, 0d, 90d, 90d, false)]
    [InlineData(2880, 1800, 0d, 0d, 0d, 0d, true)]
    [InlineData(0, 1800, 0d, 0d, 0d, 0d, false)]
    [InlineData(2880, 1800, 0d, 0d, 1d, 0d, false)]
    [InlineData(2880, 1800, 0d, 0d, null, 0d, false)]
    [InlineData(2880, 1800, 0d, 0d, double.NaN, 0d, false)]
    [InlineData(2880, 1800, 0d, 0d, double.PositiveInfinity, 0d, false)]
    public void ViewportRejectsLetterboxAndMissingOrDummyMeasurements(int width, int height,
        double? left, double? right, double? top, double? bottom, bool expected)
    {
        new MpvViewportEvidence(width, height, left, right, top, bottom, "yes", 1).HasNoLetterbox.Should().Be(expected);
    }

    [Fact]
    public void CoverGeometryIsNotCompositionProofAndStretchIsNotCover()
    {
        MpvViewportEvidence viewport = new(2880, 1800, -160, -160, 0, 0, "yes", 1);
        viewport.CoverConfigured.Should().BeTrue();
        (viewport with { KeepAspect = "no" }).CoverConfigured.Should().BeFalse();
        (viewport with { PanScan = 0 }).CoverConfigured.Should().BeFalse();
        (viewport with { PanScan = double.NaN }).CoverConfigured.Should().BeFalse();
        new MpvOutputEvidence(true, true, true, 1, "gpu-next", "d3d11", 2, viewport).ProvesDesktopPresentation.Should().BeFalse();
    }

    [Fact]
    public void RelativeNativeLibraryIsRejectedBeforeLoading()
    {
        Action action = () => MpvClient.ValidateLibrary("libmpv-2.dll", new string('0', 64));
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void InvalidNativeHashIsRejectedBeforeOpening(string hash)
    {
        Action action = () => MpvClient.ValidateLibrary(Path.GetFullPath("absent.dll"), hash);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TamperedLibraryIsRejectedBeforeNativeLoad()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "Not native code");
            Action action = () => MpvClient.ValidateLibrary(path, new string('0', 64));
            action.Should().Throw<InvalidDataException>();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FixtureIsDeterministicIndexedVideoWithChangingFrames()
    {
        string root = Path.Combine(Path.GetTempPath(), "livewall-c0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "first.avi"), second = Path.Combine(root, "second.avi");
        try
        {
            SyntheticVideo.Write(first); SyntheticVideo.Write(second);
            byte[] a = File.ReadAllBytes(first), b = File.ReadAllBytes(second);
            a.AsSpan().SequenceEqual(b).Should().BeTrue();
            Encoding.ASCII.GetString(a, 0, 4).Should().Be("RIFF");
            BitConverter.ToUInt32(a, 4).Should().Be((uint)a.Length - 8);
            Encoding.ASCII.GetString(a, 8, 4).Should().Be("AVI ");
            int movie = Find(a, "movi");
            int index = Find(a, "idx1");
            BitConverter.ToUInt32(a, index + 4).Should().Be(SyntheticVideo.Frames * 16u);
            int frameBytes = SyntheticVideo.Width * SyntheticVideo.Height * 3;
            a.AsSpan(movie + 12, frameBytes).SequenceEqual(a.AsSpan(movie + 20 + frameBytes, frameBytes)).Should().BeFalse();
            // The bottom band is a deliberate green direction marker, not a colour fault.
            Pixel(a, movie, 0, 200, 0).Should().Be((255, 255, 0));
            Pixel(a, movie, 0, 200, 90).Should().Be((255, 96, 0));
            Pixel(a, movie, 36, 200, 179).Should().Be((100, 168, 0));
            for (int n = 0; n < SyntheticVideo.Frames; n++)
            {
                uint offset = BitConverter.ToUInt32(a, index + 8 + n * 16 + 8);
                Encoding.ASCII.GetString(a, movie + checked((int)offset), 4).Should().Be("00db");
            }
            Action overwrite = () => SyntheticVideo.Write(first);
            overwrite.Should().Throw<IOException>();
        }
        finally { File.Delete(first); File.Delete(second); Directory.Delete(root); }
    }

    private static (int R, int G, int B) Pixel(byte[] data, int movie, int frame, int x, int y)
    {
        int offset = movie + 12 + frame * (SyntheticVideo.Width * SyntheticVideo.Height * 3 + 8) +
            ((SyntheticVideo.Height - 1 - y) * SyntheticVideo.Width + x) * 3;
        return (data[offset + 2], data[offset + 1], data[offset]);
    }

    private static int Find(byte[] bytes, string text)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i <= bytes.Length - pattern.Length; i++)
            if (bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        throw new InvalidDataException("Missing AVI chunk.");
    }
}
