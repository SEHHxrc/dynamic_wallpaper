using LiveWall.Application.Sessions;

namespace LiveWall.Platform.Windows.Desktop;

public sealed class RaisedDesktopAdapter : IDesktopHostAdapter
{
    public const string AdapterId = "raised-desktop-v1";

    public bool IsSupported(WindowsShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.RaisedDesktopEnabled;
    }

    public Task<DesktopAttachPoint> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<DesktopAttachPoint>(
            new DesktopAttachPointUnavailableException(
                "Raised Desktop attach-point discovery has not been validated for this shell build."));
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        _ = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }
}
