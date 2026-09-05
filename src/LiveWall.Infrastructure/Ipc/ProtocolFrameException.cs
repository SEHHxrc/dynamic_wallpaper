namespace LiveWall.Infrastructure.Ipc;

public sealed class ProtocolFrameException : IOException
{
    public ProtocolFrameException(string message)
        : base(message)
    {
    }

    public ProtocolFrameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
