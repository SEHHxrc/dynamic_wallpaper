using System.IO.Pipes;
using System.Security.Principal;

namespace LiveWall.Infrastructure.Ipc;

public static class LocalNamedPipeFactory
{
    private const string RequiredPrefix = @"LOCAL\livewall.";

    public static NamedPipeServerStream CreateServer(
        string pipeName,
        int maximumServerInstances,
        bool isFirstInstance)
    {
        ValidatePipeName(pipeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumServerInstances, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumServerInstances, 254);

        PipeOptions options = PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly;
        if (isFirstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maximumServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 16 * 1024,
            outBufferSize: 16 * 1024);
    }

    public static NamedPipeClientStream CreateClient(string pipeName)
    {
        ValidatePipeName(pipeName);
        return new NamedPipeClientStream(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
    }

    private static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!pipeName.StartsWith(RequiredPrefix, StringComparison.Ordinal) ||
            pipeName.Length > 200 ||
            pipeName.Any(character =>
                character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9') &&
                character is not '.' and not '-' and not '\\' &&
                character is not (>= 'A' and <= 'Z')))
        {
            throw new ArgumentException("Pipe name is not a valid local LiveWall endpoint.", nameof(pipeName));
        }
    }
}
