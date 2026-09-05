using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

internal interface IDesktopWindowDispatcher : IAsyncDisposable
{
    uint OwnerThreadId { get; }

    Task Ready { get; }

    IAsyncEnumerable<DesktopWindowSignal> ReadSignalsAsync(CancellationToken cancellationToken);

    Task InvokeAsync(Action action, CancellationToken cancellationToken);

    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken);
}

internal sealed class DesktopWindowDispatcher : IDesktopWindowDispatcher
{
    private const uint DispatchWorkMessage = DesktopNativeMethods.WindowMessageApplication + 1;
    private const uint ShutdownMessage = DesktopNativeMethods.WindowMessageApplication + 2;
    private readonly object lifecycleGate = new();
    private readonly ConcurrentQueue<IDispatcherWorkItem> workItems = new();
    private readonly Channel<DesktopWindowSignal> signals = Channel.CreateUnbounded<DesktopWindowSignal>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private readonly string className = $"LiveWall.DesktopDispatcher.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private nint instance;
    private nint signalWindow;
    private uint taskbarCreatedMessage;
    private bool accepting = true;
    private bool shutdownRequested;

    public DesktopWindowDispatcher()
    {
        windowProcedure = WindowProcedure;
        thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "LiveWall Desktop Window Dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public uint OwnerThreadId { get; private set; }

    public Task Ready => ready.Task;

    internal ulong SignalWindowHandle => unchecked((ulong)signalWindow.ToInt64());

    internal uint TaskbarCreatedMessage => taskbarCreatedMessage;

    internal Task Completion => stopped.Task;

    public IAsyncEnumerable<DesktopWindowSignal> ReadSignalsAsync(
        CancellationToken cancellationToken) =>
        signals.Reader.ReadAllAsync(cancellationToken);

    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken);
    }

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (DesktopNativeMethods.GetCurrentThreadId() == OwnerThreadId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }

        DispatcherWorkItem<T> workItem = new(action, cancellationToken);
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(!accepting, this);
            workItems.Enqueue(workItem);
            if (!DesktopNativeMethods.PostMessage(signalWindow, DispatchWorkMessage, 0, 0))
            {
                workItem.Fail(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not wake the Desktop Window Dispatcher."));
            }
        }

        return await workItem.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        bool shouldSignal;
        lock (lifecycleGate)
        {
            shouldSignal = accepting;
            accepting = false;
        }

        if (!shouldSignal)
        {
            await stopped.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await Ready.ConfigureAwait(false);
        }
        catch
        {
            await stopped.Task.ConfigureAwait(false);
            return;
        }

        if (!DesktopNativeMethods.PostMessage(signalWindow, ShutdownMessage, 0, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not stop the Desktop Window Dispatcher.");
        }

        await stopped.Task.ConfigureAwait(false);
    }

    private void RunMessageLoop()
    {
        try
        {
            OwnerThreadId = DesktopNativeMethods.GetCurrentThreadId();
            instance = DesktopNativeMethods.GetModuleHandle(null);
            if (instance == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleHandle failed.");
            }

            RegisterDispatcherClass();
            taskbarCreatedMessage = DesktopNativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (taskbarCreatedMessage == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "RegisterWindowMessage for TaskbarCreated failed.");
            }

            signalWindow = DesktopNativeMethods.CreateWindowEx(
                DesktopNativeMethods.WindowExStyleNoActivate |
                    DesktopNativeMethods.WindowExStyleToolWindow,
                className,
                "LiveWall Shell Signal Window",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                instance,
                0);
            if (signalWindow == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create the Desktop Window Dispatcher signal window.");
            }

            ready.TrySetResult();
            while (!shutdownRequested)
            {
                int result = DesktopNativeMethods.GetMessage(out NativeMessage message, 0, 0, 0);
                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed.");
                }

                if (result == 0)
                {
                    break;
                }

                _ = DesktopNativeMethods.TranslateMessage(in message);
                _ = DesktopNativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            FailPendingWork(exception);
        }
        finally
        {
            CleanupOnOwnerThread();
            signals.Writer.TryComplete();
            stopped.TrySetResult();
        }
    }

    private void RegisterDispatcherClass()
    {
        WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = windowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (DesktopNativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not register the Desktop Window Dispatcher class.");
        }
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == DispatchWorkMessage)
        {
            DrainWorkItems();
            return 0;
        }

        if (message == ShutdownMessage)
        {
            shutdownRequested = true;
            return 0;
        }

        DesktopWindowSignalKind? signalKind = message == taskbarCreatedMessage
            ? DesktopWindowSignalKind.TaskbarCreated
            : message == DesktopNativeMethods.WindowMessageDisplayChange
                ? DesktopWindowSignalKind.DisplayChanged
                : message == DesktopNativeMethods.WindowMessageDpiChanged
                    ? DesktopWindowSignalKind.DpiChanged
                    : null;
        if (signalKind is not null)
        {
            signals.Writer.TryWrite(new DesktopWindowSignal(signalKind.Value));
            return 0;
        }

        return DesktopNativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private void DrainWorkItems()
    {
        while (workItems.TryDequeue(out IDispatcherWorkItem? workItem))
        {
            workItem.Execute();
        }
    }

    private void FailPendingWork(Exception exception)
    {
        while (workItems.TryDequeue(out IDispatcherWorkItem? workItem))
        {
            workItem.Fail(exception);
        }
    }

    private void CleanupOnOwnerThread()
    {
        FailPendingWork(new ObjectDisposedException(nameof(DesktopWindowDispatcher)));
        if (signalWindow != 0)
        {
            _ = DesktopNativeMethods.DestroyWindow(signalWindow);
            signalWindow = 0;
        }

        if (instance != 0)
        {
            _ = DesktopNativeMethods.UnregisterClass(className, instance);
        }
    }

    private interface IDispatcherWorkItem
    {
        void Execute();

        void Fail(Exception exception);
    }

    private sealed class DispatcherWorkItem<T> : IDispatcherWorkItem
    {
        private readonly Func<T> action;
        private readonly CancellationToken cancellationToken;
        private readonly TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration cancellationRegistration;
        private int state;

        public DispatcherWorkItem(Func<T> action, CancellationToken cancellationToken)
        {
            this.action = action;
            this.cancellationToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.UnsafeRegister(
                    static state => ((DispatcherWorkItem<T>)state!).Cancel(),
                    this);
            }
        }

        public Task<T> Task => completion.Task;

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                cancellationRegistration.Dispose();
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                Interlocked.Exchange(ref state, 2);
                cancellationRegistration.Dispose();
            }
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                completion.TrySetException(exception);
                cancellationRegistration.Dispose();
            }
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref state, 3, 0) == 0)
            {
                completion.TrySetCanceled(cancellationToken);
            }
        }
    }
}

public enum DesktopWindowSignalKind
{
    TaskbarCreated,
    DisplayChanged,
    DpiChanged,
}

public sealed record DesktopWindowSignal(DesktopWindowSignalKind Kind);
