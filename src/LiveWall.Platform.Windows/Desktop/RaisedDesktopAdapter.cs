using LiveWall.Application.Sessions;

namespace LiveWall.Platform.Windows.Desktop;

internal sealed class RaisedDesktopAdapter : IDesktopHostAdapter
{
    public const string AdapterId = "raised-desktop-v1";
    private static readonly string[] ValidatedBuilds = [];
    private readonly bool allowUnvalidatedBuild;
    private readonly Func<CancellationToken, Task<DesktopAttachmentLease>> discoverAttachment;

    public RaisedDesktopAdapter()
        : this(allowUnvalidatedBuild: false)
    {
    }

    internal RaisedDesktopAdapter(bool allowUnvalidatedBuild)
        : this(
            allowUnvalidatedBuild,
            cancellationToken => RaisedDesktopDiagnostics.DiscoverAttachmentLeaseAsync(
                AdapterId,
                snapshotObserver: null,
                cancellationToken))
    {
    }

    internal RaisedDesktopAdapter(
        bool allowUnvalidatedBuild,
        Func<CancellationToken, Task<DesktopAttachmentLease>> discoverAttachment)
    {
        this.allowUnvalidatedBuild = allowUnvalidatedBuild;
        this.discoverAttachment = discoverAttachment ??
            throw new ArgumentNullException(nameof(discoverAttachment));
    }

    public bool IsSupported(WindowsShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.RaisedDesktopEnabled &&
            (allowUnvalidatedBuild ||
                (snapshot.HasCompleteBuildIdentity &&
                    ValidatedBuilds.Contains(snapshot.FullBuild, StringComparer.Ordinal)));
    }

    public Task<DesktopAttachmentLease> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return discoverAttachment(cancellationToken);
    }
}
