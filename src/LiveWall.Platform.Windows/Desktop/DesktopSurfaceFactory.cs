using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

internal interface IDesktopSurfaceFactory : IAsyncDisposable
{
    IAsyncEnumerable<DesktopWindowSignal> ReadSignalsAsync(
        CancellationToken cancellationToken);

    Task<ulong> CreateAsync(
        SurfaceId id,
        SurfaceRequest request,
        DesktopAttachmentLease attachmentLease,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        IReadOnlyList<ulong> provisionalSurfaceHandles,
        IReadOnlyList<ulong> replacedSurfaceHandles,
        CancellationToken cancellationToken);

    Task DestroyAsync(ulong surfaceHandle, CancellationToken cancellationToken);

    Task AbandonAsync(ulong surfaceHandle, CancellationToken cancellationToken);
}

internal sealed class NativeDesktopSurfaceFactory : IDesktopSurfaceFactory
{
    private readonly string className = $"LiveWall.DesktopSurface.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private readonly IDesktopWindowDispatcher dispatcher;
    private readonly bool ownsDispatcher;
    private readonly Func<DesktopAttachmentLease, bool> shellGenerationValidator;
    private readonly Dictionary<nint, TrackedDesktopSurfaceWindow> surfaces = [];
    private nint instance;
    private bool registered;
    private bool disposed;

    public NativeDesktopSurfaceFactory()
        : this(new DesktopWindowDispatcher(), ownsDispatcher: true)
    {
    }

    internal NativeDesktopSurfaceFactory(
        IDesktopWindowDispatcher dispatcher,
        bool ownsDispatcher = false,
        Func<DesktopAttachmentLease, bool>? shellGenerationValidator = null)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.ownsDispatcher = ownsDispatcher;
        this.shellGenerationValidator = shellGenerationValidator ??
            (static attachmentLease => attachmentLease.HasCurrentShellGeneration());
        windowProcedure = WindowProcedure;
    }

    public IAsyncEnumerable<DesktopWindowSignal> ReadSignalsAsync(
        CancellationToken cancellationToken) =>
        dispatcher.ReadSignalsAsync(cancellationToken);

    public Task<ulong> CreateAsync(
        SurfaceId id,
        SurfaceRequest request,
        DesktopAttachmentLease attachmentLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentLease);
        ThrowIfDisposed();
        return dispatcher.InvokeAsync(
            () => CreateOnDispatcher(id, request, attachmentLease),
            cancellationToken);
    }

    public Task ReplaceAsync(
        IReadOnlyList<ulong> provisionalSurfaceHandles,
        IReadOnlyList<ulong> replacedSurfaceHandles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provisionalSurfaceHandles);
        ArgumentNullException.ThrowIfNull(replacedSurfaceHandles);
        ThrowIfDisposed();
        ulong[] provisional = provisionalSurfaceHandles.ToArray();
        ulong[] replaced = replacedSurfaceHandles.ToArray();
        return dispatcher.InvokeAsync(
            () => ReplaceOnDispatcher(provisional, replaced),
            cancellationToken);
    }

    public Task DestroyAsync(ulong surfaceHandle, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return dispatcher.InvokeAsync(
            () => DestroyOnDispatcher(ToNativeHandle(surfaceHandle)),
            cancellationToken);
    }

    public Task AbandonAsync(ulong surfaceHandle, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return dispatcher.InvokeAsync(
            () => AbandonOnDispatcher(ToNativeHandle(surfaceHandle)),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            await dispatcher.InvokeAsync(DisposeOnDispatcher, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownsDispatcher)
            {
                await dispatcher.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal Task<bool> IsVisibleAsync(ulong surfaceHandle, CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(
            () => (unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
                    ToNativeHandle(surfaceHandle),
                    DesktopNativeMethods.WindowLongStyle).ToInt64()) &
                DesktopNativeMethods.WindowStyleVisible) != 0,
            cancellationToken);

    internal Task<uint> GetWindowThreadIdAsync(
        ulong surfaceHandle,
        CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(
            () => DesktopNativeMethods.GetWindowThreadProcessId(
                ToNativeHandle(surfaceHandle),
                out _),
            cancellationToken);

    internal uint LastCreateThreadId { get; private set; }

    internal uint LastReplaceThreadId { get; private set; }

    internal uint LastDestroyThreadId { get; private set; }

    private ulong CreateOnDispatcher(
        SurfaceId id,
        SurfaceRequest request,
        DesktopAttachmentLease attachmentLease)
    {
        ThrowIfDisposed();
        LastCreateThreadId = DesktopNativeMethods.GetCurrentThreadId();
        EnsureWindowClassRegistered();
        ValidateAttachmentLease(attachmentLease);
        nint parent = ToNativeHandle(attachmentLease.ParentWindowHandle);
        NativeRect parentBounds = GetParentBounds(parent);
        uint style = DesktopNativeMethods.WindowStyleChild |
            DesktopNativeMethods.WindowStyleClipChildren |
            DesktopNativeMethods.WindowStyleClipSiblings |
            (attachmentLease.PlacementKind == DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop
                ? DesktopNativeMethods.WindowStyleDisabled
                : 0);
        nint window = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate |
                DesktopNativeMethods.WindowExStyleToolWindow |
                (attachmentLease.PlacementKind == DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop
                    ? DesktopNativeMethods.WindowExStyleNoRedirectionBitmap
                    : 0),
            className,
            $"LiveWall Surface {id.Value}",
            style,
            checked(request.Bounds.X - parentBounds.Left),
            checked(request.Bounds.Y - parentBounds.Top),
            request.Bounds.Width,
            request.Bounds.Height,
            parent,
            0,
            instance,
            0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");
        }

        try
        {
            PositionHidden(window, request.Bounds, parentBounds, attachmentLease);
            surfaces.Add(
                window,
                new TrackedDesktopSurfaceWindow(
                    window,
                    DesktopSurfaceWindowState.Provisional,
                    attachmentLease));
            return unchecked((ulong)window.ToInt64());
        }
        catch
        {
            _ = DesktopNativeMethods.DestroyWindow(window);
            throw;
        }
    }

    private void ReplaceOnDispatcher(
        IReadOnlyList<ulong> provisionalSurfaceHandles,
        IReadOnlyList<ulong> replacedSurfaceHandles)
    {
        ThrowIfDisposed();
        LastReplaceThreadId = DesktopNativeMethods.GetCurrentThreadId();
        TrackedDesktopSurfaceWindow[] provisional = ValidateReplacementHandles(
            provisionalSurfaceHandles,
            DesktopSurfaceWindowState.Provisional,
            "provisional",
            validateCurrentWindow: true);
        DesktopShellGeneration[] nextGenerations = provisional
            .Select(window => window.AttachmentLease.ShellGeneration)
            .Distinct()
            .ToArray();
        if (nextGenerations.Length > 1)
        {
            throw new InvalidOperationException(
                "A Surface replacement cannot activate more than one Shell generation.");
        }

        TrackedDesktopSurfaceWindow[] replaced = ValidateReplacementHandles(
            replacedSurfaceHandles,
            DesktopSurfaceWindowState.Active,
            "active",
            validateCurrentWindow: false);
        if (provisional.Select(item => item.Window)
            .Intersect(replaced.Select(item => item.Window))
            .Any())
        {
            throw new ArgumentException("A Surface cannot be both provisional and replaced.");
        }

        DesktopShellGeneration? nextGeneration = nextGenerations.Length == 0
            ? null
            : nextGenerations[0];
        TrackedDesktopSurfaceWindow[] replacedInCurrentGeneration = replaced
            .Where(window => nextGeneration is null ||
                window.AttachmentLease.ShellGeneration == nextGeneration.Value)
            .ToArray();
        foreach (TrackedDesktopSurfaceWindow window in replacedInCurrentGeneration)
        {
            ValidateCurrentSurfaceWindow(window, "active");
        }

        nint deferred = DesktopNativeMethods.BeginDeferWindowPos(
            checked(provisional.Length + replacedInCurrentGeneration.Length));
        if (deferred == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginDeferWindowPos failed.");
        }

        foreach (TrackedDesktopSurfaceWindow window in replacedInCurrentGeneration)
        {
            deferred = DeferVisibility(deferred, window, visible: false);
        }

        foreach (TrackedDesktopSurfaceWindow window in provisional)
        {
            PrepareBackdrop(window.AttachmentLease);
            deferred = DeferVisibility(deferred, window, visible: true);
        }

        if (!DesktopNativeMethods.EndDeferWindowPos(deferred))
        {
            int error = Marshal.GetLastWin32Error();
            List<Exception> rollbackFailures = RollBackVisibility(
                provisional,
                replacedInCurrentGeneration);
            if (rollbackFailures.Count > 0)
            {
                rollbackFailures.Insert(
                    0,
                    new Win32Exception(error, "EndDeferWindowPos failed."));
                throw new AggregateException(
                    "Surface replacement and visibility rollback failed.",
                    rollbackFailures);
            }

            throw new Win32Exception(error, "EndDeferWindowPos failed; Surface visibility was rolled back.");
        }

        foreach (TrackedDesktopSurfaceWindow window in replaced)
        {
            surfaces[window.Window] = window with { State = DesktopSurfaceWindowState.Retired };
        }

        foreach (TrackedDesktopSurfaceWindow window in provisional)
        {
            surfaces[window.Window] = window with { State = DesktopSurfaceWindowState.Active };
        }
    }

    private TrackedDesktopSurfaceWindow[] ValidateReplacementHandles(
        IReadOnlyList<ulong> handles,
        DesktopSurfaceWindowState expectedState,
        string role,
        bool validateCurrentWindow)
    {
        nint[] windows = handles.Select(ToNativeHandle).ToArray();
        if (windows.Distinct().Count() != windows.Length)
        {
            throw new ArgumentException($"The {role} Surface list contains duplicate handles.");
        }

        TrackedDesktopSurfaceWindow[] trackedWindows = new TrackedDesktopSurfaceWindow[windows.Length];
        for (int index = 0; index < windows.Length; index++)
        {
            nint window = windows[index];
            if (!surfaces.TryGetValue(window, out TrackedDesktopSurfaceWindow? tracked))
            {
                throw new InvalidOperationException(
                    $"The {role} Surface is not owned by this factory.");
            }

            if (tracked.State != expectedState)
            {
                throw new InvalidOperationException(
                    $"The {role} Surface is in state '{tracked.State}' instead of '{expectedState}'.");
            }

            if (validateCurrentWindow)
            {
                ValidateCurrentSurfaceWindow(tracked, role);
            }

            trackedWindows[index] = tracked;
        }

        return trackedWindows;
    }

    private void ValidateCurrentSurfaceWindow(
        TrackedDesktopSurfaceWindow tracked,
        string role)
    {
        if (!DesktopNativeMethods.IsWindow(tracked.Window))
        {
            throw new InvalidOperationException(
                $"The {role} Surface is no longer a valid window in the current Shell generation.");
        }

        ValidateAttachmentLease(tracked.AttachmentLease);
    }

    private static nint DeferVisibility(
        nint deferred,
        TrackedDesktopSurfaceWindow tracked,
        bool visible)
    {
        nint window = tracked.Window;
        uint flags = DesktopNativeMethods.SetWindowPositionNoActivate |
            DesktopNativeMethods.SetWindowPositionNoMove |
            DesktopNativeMethods.SetWindowPositionNoSize;
        nint insertAfter;
        if (visible)
        {
            flags |= DesktopNativeMethods.SetWindowPositionShowWindow;
            insertAfter = tracked.AttachmentLease.PlacementKind ==
                DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop
                ? ToNativeHandle(tracked.AttachmentLease.ZOrderAnchorWindowHandle)
                : DesktopNativeMethods.WindowBottom;
        }
        else
        {
            flags |= DesktopNativeMethods.SetWindowPositionHideWindow |
                DesktopNativeMethods.SetWindowPositionNoZOrder;
            insertAfter = 0;
        }

        nint next = DesktopNativeMethods.DeferWindowPos(
            deferred,
            window,
            insertAfter,
            0,
            0,
            0,
            0,
            flags);
        if (next == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DeferWindowPos failed.");
        }

        return next;
    }

    private static List<Exception> RollBackVisibility(
        IEnumerable<TrackedDesktopSurfaceWindow> provisional,
        IEnumerable<TrackedDesktopSurfaceWindow> replaced)
    {
        List<Exception> failures = [];
        foreach (TrackedDesktopSurfaceWindow window in provisional)
        {
            TrySetVisibility(window.Window, visible: false, failures);
        }

        foreach (TrackedDesktopSurfaceWindow window in replaced)
        {
            TrySetVisibility(window.Window, visible: true, failures);
        }

        return failures;
    }

    private static void TrySetVisibility(
        nint window,
        bool visible,
        List<Exception> failures)
    {
        if (!SetVisibility(window, visible))
        {
            failures.Add(new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not {(visible ? "show" : "hide")} Surface 0x{window:X} during rollback."));
        }
    }

    private static bool SetVisibility(nint window, bool visible)
    {
        uint flags = DesktopNativeMethods.SetWindowPositionNoActivate |
            DesktopNativeMethods.SetWindowPositionNoMove |
            DesktopNativeMethods.SetWindowPositionNoSize |
            DesktopNativeMethods.SetWindowPositionNoZOrder |
            (visible
                ? DesktopNativeMethods.SetWindowPositionShowWindow
                : DesktopNativeMethods.SetWindowPositionHideWindow);
        return DesktopNativeMethods.SetWindowPos(window, 0, 0, 0, 0, 0, flags);
    }

    private void DestroyOnDispatcher(nint surface)
    {
        LastDestroyThreadId = DesktopNativeMethods.GetCurrentThreadId();
        if (!surfaces.ContainsKey(surface))
        {
            return;
        }

        if (DesktopNativeMethods.IsWindow(surface) && !DesktopNativeMethods.DestroyWindow(surface))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DestroyWindow failed.");
        }

        surfaces.Remove(surface);
    }

    private void AbandonOnDispatcher(nint surface)
    {
        LastDestroyThreadId = DesktopNativeMethods.GetCurrentThreadId();
        if (!surfaces.TryGetValue(surface, out TrackedDesktopSurfaceWindow? tracked))
        {
            return;
        }

        if (shellGenerationValidator(tracked.AttachmentLease))
        {
            DestroyOnDispatcher(surface);
            return;
        }

        // A stale HWND may already have been destroyed and reused by another owner. Removing
        // bookkeeping is the only valid operation once the leased Shell generation changed.
        surfaces.Remove(surface);
    }

    private void DisposeOnDispatcher()
    {
        List<Exception> failures = [];
        foreach (nint surface in surfaces.Keys.ToArray())
        {
            try
            {
                DestroyOnDispatcher(surface);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (registered)
        {
            if (!DesktopNativeMethods.UnregisterClass(className, instance))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "UnregisterClass failed."));
            }

            registered = false;
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Desktop Surface cleanup failed.", failures);
        }
    }

    private void EnsureWindowClassRegistered()
    {
        if (registered)
        {
            return;
        }

        instance = DesktopNativeMethods.GetModuleHandle(null);
        if (instance == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleHandle failed.");
        }

        WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = windowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (DesktopNativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
        }

        registered = true;
    }

    private static NativeRect GetParentBounds(nint parent)
    {
        if (!DesktopNativeMethods.IsWindow(parent) ||
            !DesktopNativeMethods.GetWindowRect(parent, out NativeRect bounds))
        {
            throw new DesktopAttachPointUnavailableException(
                "The desktop attach point is no longer a valid window.");
        }

        return bounds;
    }

    private static void PositionHidden(
        nint surface,
        DisplayBounds bounds,
        NativeRect parentBounds,
        DesktopAttachmentLease attachmentLease)
    {
        if (!DesktopNativeMethods.SetWindowPos(
                surface,
                attachmentLease.PlacementKind == DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop
                    ? ToNativeHandle(attachmentLease.ZOrderAnchorWindowHandle)
                    : DesktopNativeMethods.WindowBottom,
                checked(bounds.X - parentBounds.Left),
                checked(bounds.Y - parentBounds.Top),
                bounds.Width,
                bounds.Height,
                DesktopNativeMethods.SetWindowPositionNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed.");
        }
    }

    private static nint ToNativeHandle(ulong handle) =>
        unchecked((nint)(long)handle);

    private void ValidateAttachmentLease(DesktopAttachmentLease attachmentLease)
    {
        nint parent = ToNativeHandle(attachmentLease.ParentWindowHandle);
        if (!DesktopNativeMethods.IsWindow(parent) ||
            !shellGenerationValidator(attachmentLease))
        {
            throw new DesktopAttachPointUnavailableException(
                "The desktop attachment lease is no longer valid for the current Shell generation.");
        }

        _ = DesktopNativeMethods.GetWindowThreadProcessId(parent, out uint parentProcessId);
        if (parentProcessId == 0 ||
            parentProcessId != attachmentLease.ShellGeneration.ShellProcessId)
        {
            throw new DesktopAttachPointUnavailableException(
                "The desktop attachment parent no longer belongs to the leased Shell process.");
        }

        if (attachmentLease.PlacementKind != DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop)
        {
            return;
        }

        nint anchor = ToNativeHandle(attachmentLease.ZOrderAnchorWindowHandle);
        nint backdrop = ToNativeHandle(attachmentLease.BackdropWindowHandle);
        _ = DesktopNativeMethods.GetWindowThreadProcessId(anchor, out uint anchorProcessId);
        _ = DesktopNativeMethods.GetWindowThreadProcessId(backdrop, out uint backdropProcessId);
        if (!DesktopNativeMethods.IsWindow(anchor) ||
            !DesktopNativeMethods.IsWindow(backdrop) ||
            DesktopNativeMethods.GetParent(anchor) != parent ||
            DesktopNativeMethods.GetParent(backdrop) != parent ||
            parentProcessId == 0 ||
            parentProcessId != attachmentLease.ShellGeneration.ShellProcessId ||
            anchorProcessId != parentProcessId ||
            backdropProcessId != parentProcessId ||
            !IsBeforeInSiblingZOrder(parent, anchor, backdrop))
        {
            throw new DesktopAttachPointUnavailableException(
                "The Raised Desktop attachment lease is stale or its Shell anchors changed.");
        }

        ValidateNoCompetingDesktopSurface(anchor, backdrop);
    }

    private void ValidateNoCompetingDesktopSurface(nint anchor, nint backdrop)
    {
        for (nint current = DesktopNativeMethods.GetWindow(
                anchor,
                DesktopNativeMethods.GetWindowNext);
            current != 0 && current != backdrop;
            current = DesktopNativeMethods.GetWindow(current, DesktopNativeMethods.GetWindowNext))
        {
            ulong style = unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
                current,
                DesktopNativeMethods.WindowLongStyle).ToInt64());
            if (!surfaces.ContainsKey(current) &&
                (style & DesktopNativeMethods.WindowStyleVisible) != 0)
            {
                throw new DesktopAttachPointUnavailableException(
                    "CompetingDesktopSurface: another visible desktop Surface appeared " +
                    "between DefView and the Raised Desktop backdrop.");
            }
        }
    }

    private static bool IsBeforeInSiblingZOrder(nint parent, nint before, nint after)
    {
        for (nint current = DesktopNativeMethods.GetTopWindow(parent);
            current != 0;
            current = DesktopNativeMethods.GetWindow(current, DesktopNativeMethods.GetWindowNext))
        {
            if (current == before)
            {
                return true;
            }

            if (current == after)
            {
                return false;
            }
        }

        return false;
    }

    private static void PrepareBackdrop(DesktopAttachmentLease attachmentLease)
    {
        if (attachmentLease.PlacementKind != DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop)
        {
            return;
        }

        if (!DesktopNativeMethods.SetWindowPos(
                ToNativeHandle(attachmentLease.BackdropWindowHandle),
                DesktopNativeMethods.WindowBottom,
                0,
                0,
                0,
                0,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionNoMove |
                    DesktopNativeMethods.SetWindowPositionNoSize))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not keep the Raised Desktop backdrop below LiveWall Surfaces.");
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(window, message, wParam, lParam);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private enum DesktopSurfaceWindowState
    {
        Provisional,
        Active,
        Retired,
    }

    private sealed record TrackedDesktopSurfaceWindow(
        nint Window,
        DesktopSurfaceWindowState State,
        DesktopAttachmentLease AttachmentLease);
}
