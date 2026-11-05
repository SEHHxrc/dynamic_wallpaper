using System.Diagnostics;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Sessions;

namespace LiveWall.Host.Orchestration;

public sealed record RendererExitProcessResult(string Mode, bool Valid, bool Initialized,
    bool ShutdownCompleted, bool ExitObserved, bool HandleDisposed, TimeSpan Elapsed, string? FailureType);

public static partial class WindowsHostEventChainDiagnostics
{
    // Does not Attach or Load. RendererChildProbe creates no child HWND or DComp
    // resource until Attach, so this is safe in an isolated/noninteractive desktop.
    public static async Task<RendererExitProcessResult> RunRendererExitProcessCheckAsync(
        string executablePath, bool graceful, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        DiagnosticRendererProcessTracker tracker = new();
        RendererProcessProvider provider = CreateRendererProvider(executablePath, tracker);
        IRendererSession? session = null;
        bool initialized = false;
        bool shutdownCompleted = false;
        string? failure = null;
        try
        {
            session = await provider.CreateAsync(new RendererLaunchContext(
                new SessionId($"exit-check-{Guid.NewGuid():N}"), 1, CreateSyntheticWallpaper(), AppContext.BaseDirectory),
                cancellationToken).ConfigureAwait(false);
            initialized = true;
            if (graceful)
            {
                await session.SendAsync(new ShutdownRenderer(1, "exit-process-check"), cancellationToken).ConfigureAwait(false);
                await using IAsyncEnumerator<RendererEvent> events = session.ReadEventsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                if (await events.MoveNextAsync().ConfigureAwait(false))
                    shutdownCompleted = events.Current is RendererShutdownCompleted { Generation: 1 };
            }
        }
        catch (Exception exception) { failure = exception.GetType().Name; }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { failure ??= exception.GetType().Name; }
            }
        }
        RendererCleanupEvidence[] cleanup = tracker.Snapshot();
        bool exited = cleanup.Length == 1 && cleanup[0].ExitObserved;
        bool disposed = cleanup.Length == 1 && cleanup[0].HandleDisposed && cleanup[0].Issue is null;
        return new(graceful ? "GracefulShutdown" : "ForcedDispose", failure is null && initialized &&
            (!graceful || shutdownCompleted) && exited && disposed, initialized, shutdownCompleted,
            exited, disposed, Stopwatch.GetElapsedTime(started), failure);
    }
}
