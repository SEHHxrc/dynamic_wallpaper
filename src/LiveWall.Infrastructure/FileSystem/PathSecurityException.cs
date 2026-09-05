namespace LiveWall.Infrastructure.FileSystem;

public sealed class PathSecurityException : IOException
{
    public PathSecurityException(string message)
        : base(message)
    {
    }

    public PathSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
