using LiveWall.Contracts.RendererProtocol;

namespace LiveWall.Renderer.Video.Protocol;

internal interface IVideoSessionBackend : IAsyncDisposable
{
    Task Failure { get; }
    void CheckHealth();
    Task InitializeAsync(CancellationToken token);
    Task<ulong> AttachAsync(AttachSurfacePayload surface, CancellationToken token);
    Task LoadAsync(string mediaPath, CancellationToken token);
    Task<VideoFrameSubmission> SubmitAsync(VideoFrameTicket ticket, CancellationToken token);
    Task<VideoFrameSubmission> BoundsAsync(VideoFrameTicket ticket, CancellationToken token);
    Task PlaybackAsync(string state, CancellationToken token);
    Task FitAsync(string fit, CancellationToken token);
    Task VolumeAsync(double volume, bool muted, CancellationToken token);
}
