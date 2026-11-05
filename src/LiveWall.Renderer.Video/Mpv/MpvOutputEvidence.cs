namespace LiveWall.Renderer.Video.Mpv;

// These are output-backend observations, NOT compositor/FirstFramePresented evidence.
internal sealed record MpvOutputEvidence(bool FileLoaded, bool PlaybackRestarted,
    bool VideoOutputConfigured, double? PositionSeconds, string? VideoOutput,
    string? GpuApi, int LoopRestarts, MpvViewportEvidence? Viewport = null)
{
    public bool OutputAttempted => FileLoaded && PlaybackRestarted && VideoOutputConfigured &&
        PositionSeconds is >= 0 && VideoOutput == "gpu-next" && GpuApi == "d3d11";

    public bool ProvesDesktopPresentation { get; }
}

internal sealed record MpvViewportEvidence(int? Width, int? Height, double? Left,
    double? Right, double? Top, double? Bottom, string? KeepAspect, double? PanScan)
{
    // Negative margins are intentional cover cropping. Missing/dummy values are not proof.
    public bool HasNoLetterbox => Width is > 0 && Height is > 0 &&
        NoPositiveMargin(Left) && NoPositiveMargin(Right) && NoPositiveMargin(Top) && NoPositiveMargin(Bottom);
    public bool CoverConfigured => KeepAspect == "yes" && PanScan is double value && double.IsFinite(value) && value >= 0.999;
    private static bool NoPositiveMargin(double? margin) => margin is double value && double.IsFinite(value) && value <= 0.5;
}
