using System.IO.Pipes;
using LiveWall.Infrastructure.Ipc;
using LiveWall.Renderer.Video.NativeMethods;
using LiveWall.Renderer.Video.Protocol;
using LiveWall.Renderer.Video.Windowing;

namespace LiveWall.Renderer.Video;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? authentication = Environment.GetEnvironmentVariable("LIVEWALL_RENDERER_AUTH_TOKEN");
        Environment.SetEnvironmentVariable("LIVEWALL_RENDERER_AUTH_TOKEN", null);
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].Length > 64 ||
            authentication is null || authentication.Length != 64 || !authentication.All(Uri.IsHexDigit)) return 2;
        uint previousLevel = 0, previousFlags = 0;
        bool orderSet = false;
        try
        {
            if (!VideoNativeMethods.GetProcessShutdownParameters(out previousLevel, out previousFlags) ||
                !VideoNativeMethods.SetProcessShutdownParameters(0x280, 0))
                throw new InvalidOperationException("Video shutdown order could not be established.");
            orderSet = true;
            if (!VideoNativeMethods.GetProcessShutdownParameters(out uint level, out uint flags) || level != 0x280 || flags != 0)
                throw new InvalidOperationException("Video shutdown order readback failed.");
            RunAsync(args[0], args[1], authentication).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception)
        {
            // Never print envelopes, credentials, native command text or private content paths.
            Console.Error.WriteLine("Video Renderer failed; the Host session was closed.");
            return 1;
        }
        finally
        {
            if (orderSet && !VideoNativeMethods.SetProcessShutdownParameters(previousLevel, previousFlags))
                Console.Error.WriteLine("Video shutdown order restoration failed.");
        }
    }

    private static async Task RunAsync(string pipeName, string sessionId, string authentication)
    {
        using CancellationTokenSource connect = new(TimeSpan.FromSeconds(10));
        await using NamedPipeClientStream pipe = LocalNamedPipeFactory.CreateClient(pipeName);
        await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        if (!VideoNativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint hostPid) || hostPid == 0)
            throw new InvalidOperationException("Cannot identify the connected Host process.");
        string library = Environment.GetEnvironmentVariable("LIVEWALL_VIDEO_LIBMPV_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "native", "libmpv-2.dll");
        Environment.SetEnvironmentVariable("LIVEWALL_VIDEO_LIBMPV_PATH", null);
        const string hash = "04E4D27DC385DC18DDD93FBE69C75E635B864F3C6EC53A9122DF54C2F7842F4A";
        await using LengthPrefixedJsonChannel channel = new(pipe, VideoProtocolValidation.Json, leaveOpen: true);
        // Session owns backend disposal, including failed or cancelled initialization.
        using Mpv.VideoControlEvidence? evidence = Mpv.VideoControlEvidence.FromEnvironment();
        MpvVideoBackend backend = new(library, hash, hostPid, evidence);
        await new VideoRendererSession(sessionId, backend).RunAsync(channel, authentication, CancellationToken.None).ConfigureAwait(false);
    }
}

