namespace LiveWall.Platform.Windows.Desktop;

public sealed class DesktopAttachPointUnavailableException : InvalidOperationException
{
    public DesktopAttachPointUnavailableException(string message)
        : base(message)
    {
    }
}
