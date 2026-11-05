using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.NativeMethods;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveWall.Platform.Windows.Lifecycle;

public enum WindowsExitReason { WindowClose, SessionShutdown, SessionLogoff, SessionCloseApp, ConsoleClose }

public sealed record WindowsExitRequest(WindowsExitReason Reason, bool Critical = false);

public enum WindowsExitWaitOutcome { None, Completed, TimedOut, Failed }

public sealed record WindowsExitObservation(WindowsExitRequest Request, WindowsExitWaitOutcome CleanupOutcome,
    TimeSpan Elapsed, TimeSpan TotalBudget);

// Owns a separate hidden top-level window, never the Surface dispatcher. Waiting
// briefly in WM_ENDSESSION must not block the thread required for HWND cleanup.
public sealed class WindowsProcessExitMonitor : IAsyncDisposable
{
    private readonly DesktopWindowDispatcher dispatcher;
    private readonly SessionEndMessageHandler handler;
    private readonly ProcessExitNativeMethods.ConsoleControlHandler consoleHandler;
    private int disposed;
    public WindowsProcessExitMonitor(Func<WindowsExitRequest, Task> requestExitAsync, TimeSpan? waitBudget = null,
        Func<WindowsExitObservation, CancellationToken, Task>? persistObservationAsync = null)
    {
        handler = new(requestExitAsync, waitBudget ?? TimeSpan.FromSeconds(4), persistObservationAsync);
        dispatcher = new(handler);
        consoleHandler = kind =>
        {
            if (kind is not (2 or 5 or 6)) return false; // Leave Ctrl+C/Break to Console.CancelKeyPress.
            handler.WaitForExit(new(kind == 2 ? WindowsExitReason.ConsoleClose :
                kind == 5 ? WindowsExitReason.SessionLogoff : WindowsExitReason.SessionShutdown));
            return true;
        };
        if (!ProcessExitNativeMethods.SetConsoleCtrlHandler(consoleHandler, true))
        {
            dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register Host console exit notifications.");
        }
    }

    public Task Ready => dispatcher.Ready;
    public WindowsExitWaitOutcome WaitOutcome => handler.WaitOutcome;
    public WindowsExitWaitOutcome CleanupOutcome => handler.CleanupOutcome;
    public WindowsExitWaitOutcome PersistenceOutcome => handler.PersistenceOutcome;
    public void MarkRunning() => handler.MarkRunning();
    internal ulong WindowHandle => dispatcher.SignalWindowHandle;
    internal Task RequestCloseForDiagnosticsAsync(CancellationToken cancellationToken) => dispatcher.InvokeAsync(() =>
    {
        if (!DesktopNativeMethods.PostMessage(unchecked((nint)(long)WindowHandle), SessionEndMessageHandler.Close, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not post a close request to the owned lifecycle window.");
    }, cancellationToken);
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            _ = ProcessExitNativeMethods.SetConsoleCtrlHandler(consoleHandler, false);
        return dispatcher.DisposeAsync();
    }
}

internal sealed class SessionEndMessageHandler
{
    internal const uint QueryEndSession = 0x0011;
    internal const uint EndSession = 0x0016;
    internal const uint Close = 0x0010;
    internal const uint QueryReadiness = 0x8033;
    private readonly Func<WindowsExitRequest, Task> requestExitAsync;
    private readonly TimeSpan waitBudget;
    private readonly Func<WindowsExitObservation, CancellationToken, Task>? persistObservationAsync;
    private Task<WindowsExitWaitOutcome>? completion;
    private Stopwatch? operationElapsed;
    private readonly object gate = new();
    private int waitOutcome;
    private int cleanupOutcome;
    private int persistenceOutcome;
    private int running;

    public SessionEndMessageHandler(Func<WindowsExitRequest, Task> requestExitAsync, TimeSpan waitBudget,
        Func<WindowsExitObservation, CancellationToken, Task>? persistObservationAsync = null)
    {
        this.requestExitAsync = requestExitAsync ?? throw new ArgumentNullException(nameof(requestExitAsync));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(waitBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(waitBudget, TimeSpan.FromSeconds(4));
        this.waitBudget = waitBudget;
        this.persistObservationAsync = persistObservationAsync;
    }

    public WindowsExitWaitOutcome WaitOutcome => (WindowsExitWaitOutcome)Volatile.Read(ref waitOutcome);
    public WindowsExitWaitOutcome CleanupOutcome => (WindowsExitWaitOutcome)Volatile.Read(ref cleanupOutcome);
    public WindowsExitWaitOutcome PersistenceOutcome => (WindowsExitWaitOutcome)Volatile.Read(ref persistenceOutcome);
    public void MarkRunning() => Volatile.Write(ref running, 1);

    // No exception may cross the native WndProc or console control callback.
    public bool TryHandle(uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message == QueryReadiness) { result = Volatile.Read(ref running); return true; }
        if (message == QueryEndSession) { result = 1; return true; }
        if (message != EndSession && message != Close) return false;
        if (message == EndSession && wParam == 0) return true;
        uint flags = unchecked((uint)lParam.ToInt64());
        WindowsExitReason reason = message == Close ? WindowsExitReason.WindowClose :
                (flags & 0x80000000) != 0 ? WindowsExitReason.SessionLogoff :
                (flags & 1) != 0 ? WindowsExitReason.SessionCloseApp : WindowsExitReason.SessionShutdown;
        WaitForExit(new(reason, (flags & 0x40000000) != 0));
        return true;
    }

    internal void WaitForExit(WindowsExitRequest request)
    {
        try
        {
            Task<WindowsExitWaitOutcome> pending;
            lock (gate)
            {
                if (completion is null)
                {
                    operationElapsed = Stopwatch.StartNew();
                    // Isolate even a synchronously blocking callback from the native thread.
                    completion = Task.Run(() => ObserveExitAsync(request, operationElapsed));
                }
                pending = completion;
            }
            TimeSpan remaining = waitBudget - operationElapsed!.Elapsed;
            WindowsExitWaitOutcome outcome = (pending.IsCompleted || (remaining > TimeSpan.Zero && pending.Wait(remaining)))
                ? pending.GetAwaiter().GetResult() : WindowsExitWaitOutcome.TimedOut;
            Interlocked.CompareExchange(ref waitOutcome, (int)outcome, (int)WindowsExitWaitOutcome.None);
        }
        catch (Exception) { Interlocked.CompareExchange(ref waitOutcome, (int)WindowsExitWaitOutcome.Failed, (int)WindowsExitWaitOutcome.None); }
    }

    private async Task<WindowsExitWaitOutcome> ObserveExitAsync(WindowsExitRequest request, Stopwatch elapsed)
    {
        WindowsExitWaitOutcome cleanup;
        try
        {
            Task stop = requestExitAsync(request) ?? throw new InvalidOperationException("Exit callback returned no completion task.");
            TimeSpan reserve = persistObservationAsync is null ? TimeSpan.Zero :
                TimeSpan.FromMilliseconds(Math.Min(500, waitBudget.TotalMilliseconds / 4));
            TimeSpan remaining = waitBudget - elapsed.Elapsed - reserve;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            await stop.WaitAsync(remaining).ConfigureAwait(false);
            cleanup = WindowsExitWaitOutcome.Completed;
        }
        catch (TimeoutException) { cleanup = WindowsExitWaitOutcome.TimedOut; }
        catch (Exception) { cleanup = WindowsExitWaitOutcome.Failed; }
        Volatile.Write(ref cleanupOutcome, (int)cleanup);
        if (persistObservationAsync is null) return cleanup;

        TimeSpan writeRemaining = waitBudget - elapsed.Elapsed;
        if (writeRemaining <= TimeSpan.Zero)
        {
            Volatile.Write(ref persistenceOutcome, (int)WindowsExitWaitOutcome.TimedOut);
            return WindowsExitWaitOutcome.TimedOut;
        }
        using CancellationTokenSource writeCancellation = new(writeRemaining);
        CancellationToken writeToken = writeCancellation.Token;
        try
        {
            WindowsExitObservation observation = new(request, cleanup, elapsed.Elapsed, waitBudget);
            Task write = Task.Run(() => persistObservationAsync(observation, writeToken), writeToken);
            await write.WaitAsync(writeToken).ConfigureAwait(false);
            Volatile.Write(ref persistenceOutcome, (int)WindowsExitWaitOutcome.Completed);
            return cleanup;
        }
        catch (OperationCanceledException) when (writeCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref persistenceOutcome, (int)WindowsExitWaitOutcome.TimedOut);
            return WindowsExitWaitOutcome.TimedOut;
        }
        catch (Exception)
        {
            Volatile.Write(ref persistenceOutcome, (int)WindowsExitWaitOutcome.Failed);
            return WindowsExitWaitOutcome.Failed;
        }
    }
}
