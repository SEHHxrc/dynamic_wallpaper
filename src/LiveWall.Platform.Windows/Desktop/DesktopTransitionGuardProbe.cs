using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Domain.Displays;
using LiveWall.Platform.Windows.NativeMethods;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiveWall.Platform.Windows.Desktop;

public enum DesktopTransitionGuardProbeState
{
    Created,
    Armed,
    Observed,
    Retired,
}

public enum DesktopTransitionGuardAvailability
{
    Available,
    GuardUnavailable,
}

public sealed record DesktopTransitionGuardPlan(
    DesktopTransitionGuardAvailability Availability,
    DisplayId TargetDisplayId,
    DisplayBounds CurrentBounds,
    DisplayBounds CandidateBounds,
    IReadOnlyList<DisplayBounds> GuardRegions,
    long TotalPixelArea,
    long AdjacentDisplayIntersection,
    string Reason);

public sealed record DesktopTransitionGuardProbeResult(
    DesktopTransitionGuardPlan Plan,
    DesktopTransitionGuardProbeState State,
    bool ArmedBeforeChange,
    int PostArmWindowMutationCount,
    bool ShellGenerationUnchanged,
    bool TargetReachedCandidateBounds,
    bool OwnedWindowsRemain);

internal static class DesktopTransitionGuardPlanner
{
    private const int MaximumRegionCount = 8;
    private const int MaximumDimension = 8192;
    private const long MaximumTotalPixelArea = 32L * 1024 * 1024;

    public static DesktopTransitionGuardPlan Create(
        DisplayDescriptor target,
        IReadOnlyList<DisplayDescriptor> connectedDisplays)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(connectedDisplays);
        DisplayBounds current = target.Bounds;
        DisplayBounds candidate = new(
            current.X,
            current.Y,
            current.Height,
            current.Width);
        if (candidate.Width > MaximumDimension || candidate.Height > MaximumDimension)
        {
            return Unavailable(target.Id, current, candidate, "CandidateBoundsExceeded");
        }

        List<DisplayBounds> regions = Subtract(candidate, current);
        foreach (DisplayDescriptor other in connectedDisplays.Where(item => item.Id != target.Id))
        {
            regions = regions.SelectMany(region => Subtract(region, other.Bounds)).ToList();
            if (regions.Count > MaximumRegionCount)
            {
                return Unavailable(target.Id, current, candidate, "RegionFragmentLimitExceeded");
            }
        }

        regions = regions
            .Where(region => region.Width > 0 && region.Height > 0)
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToList();
        if (regions.Count == 0)
        {
            return Unavailable(target.Id, current, candidate, "GuardRegionEmpty");
        }

        long totalArea = regions.Sum(Area);
        if (totalArea > MaximumTotalPixelArea)
        {
            return Unavailable(target.Id, current, candidate, "GuardAreaExceeded");
        }

        DisplayBounds[] otherBounds = connectedDisplays
            .Where(item => item.Id != target.Id)
            .Select(item => item.Bounds)
            .ToArray();
        long intersection = regions.Sum(region =>
            otherBounds.Sum(other => Area(Intersect(region, other))));
        if (intersection != 0)
        {
            return Unavailable(target.Id, current, candidate, "AdjacentDisplayIntersection");
        }

        return new DesktopTransitionGuardPlan(
            DesktopTransitionGuardAvailability.Available,
            target.Id,
            current,
            candidate,
            Array.AsReadOnly(regions.ToArray()),
            totalArea,
            intersection,
            string.Empty);
    }

    private static DesktopTransitionGuardPlan Unavailable(
        DisplayId id,
        DisplayBounds current,
        DisplayBounds candidate,
        string reason) =>
        new(
            DesktopTransitionGuardAvailability.GuardUnavailable,
            id,
            current,
            candidate,
            [],
            0,
            0,
            reason);

    private static List<DisplayBounds> Subtract(DisplayBounds source, DisplayBounds excluded)
    {
        DisplayBounds? intersection = Intersect(source, excluded);
        if (intersection is null)
        {
            return [source];
        }

        DisplayBounds cut = intersection;
        List<DisplayBounds> result = [];
        int sourceRight = checked(source.X + source.Width);
        int sourceBottom = checked(source.Y + source.Height);
        int cutRight = checked(cut.X + cut.Width);
        int cutBottom = checked(cut.Y + cut.Height);
        Add(result, source.X, source.Y, source.Width, checked(cut.Y - source.Y));
        Add(result, source.X, cutBottom, source.Width, checked(sourceBottom - cutBottom));
        Add(result, source.X, cut.Y, checked(cut.X - source.X), cut.Height);
        Add(result, cutRight, cut.Y, checked(sourceRight - cutRight), cut.Height);
        return result;
    }

    private static DisplayBounds? Intersect(DisplayBounds left, DisplayBounds right)
    {
        int x = Math.Max(left.X, right.X);
        int y = Math.Max(left.Y, right.Y);
        int rightEdge = Math.Min(
            checked(left.X + left.Width),
            checked(right.X + right.Width));
        int bottomEdge = Math.Min(
            checked(left.Y + left.Height),
            checked(right.Y + right.Height));
        return rightEdge <= x || bottomEdge <= y
            ? null
            : new DisplayBounds(x, y, checked(rightEdge - x), checked(bottomEdge - y));
    }

    private static long Area(DisplayBounds? bounds) => bounds is null
        ? 0
        : checked((long)bounds.Width * bounds.Height);

    private static void Add(List<DisplayBounds> result, int x, int y, int width, int height)
    {
        if (width > 0 && height > 0)
        {
            result.Add(new DisplayBounds(x, y, width, height));
        }
    }
}

public sealed class DesktopTransitionGuardProbe : IAsyncDisposable
{
    public const string WindowClassPrefix = "LiveWall.TransitionGuardProbe";
    private readonly DesktopWindowDispatcher dispatcher = new();
    private readonly string className = $"{WindowClassPrefix}.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private readonly DesktopTransitionGuardPlan plan;
    private readonly DesktopAttachmentLease attachmentLease;
    private readonly ulong activeSurfaceWindowHandle;
    private readonly List<GuardWindowResources> resources = [];
    private nint instance;
    private bool registered;
    private bool disposed;
    private bool observed;
    private bool shellGenerationUnchanged = true;
    private bool targetReachedCandidateBounds;

    private DesktopTransitionGuardProbe(
        DesktopTransitionGuardPlan plan,
        DesktopAttachmentLease attachmentLease,
        ulong activeSurfaceWindowHandle)
    {
        this.plan = plan;
        this.attachmentLease = attachmentLease;
        this.activeSurfaceWindowHandle = activeSurfaceWindowHandle;
        windowProcedure = WindowProcedure;
    }

    public DesktopTransitionGuardPlan Plan => plan;

    public DesktopTransitionGuardProbeState State => disposed
        ? DesktopTransitionGuardProbeState.Retired
        : observed
            ? DesktopTransitionGuardProbeState.Observed
            : resources.Count > 0
                ? DesktopTransitionGuardProbeState.Armed
                : DesktopTransitionGuardProbeState.Created;

    public static async Task<DesktopTransitionGuardProbe> ArmAsync(
        WindowsDesktopHost desktopHost,
        DisplayTopology topology,
        DisplayId targetDisplayId,
        uint guardColorReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desktopHost);
        ArgumentNullException.ThrowIfNull(topology);
        DisplayDescriptor target = topology.Displays.SingleOrDefault(
                display => display.Id == targetDisplayId) ??
            throw new InvalidOperationException(
                $"Transition guard target '{targetDisplayId}' is not connected.");
        DesktopTransitionGuardPlan plan = DesktopTransitionGuardPlanner.Create(
            target,
            topology.Displays);
        if (plan.Availability != DesktopTransitionGuardAvailability.Available)
        {
            throw new DesktopAttachPointUnavailableException(
                $"GuardUnavailable: {plan.Reason}.");
        }

        DesktopTransitionGuardArmContext context =
            desktopHost.GetTransitionGuardArmContext(targetDisplayId);
        DesktopTransitionGuardProbe probe = new(
            plan,
            context.AttachmentLease,
            context.ActiveSurfaceWindowHandle);
        try
        {
            await probe.dispatcher.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await probe.dispatcher.InvokeAsync(
                    () => probe.ArmOnDispatcher(guardColorReference),
                    cancellationToken)
                .ConfigureAwait(false);
            return probe;
        }
        catch
        {
            await probe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Observe(DisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (observed)
        {
            throw new InvalidOperationException("The transition guard probe was already observed.");
        }

        shellGenerationUnchanged = attachmentLease.HasCurrentShellGeneration();
        DisplayDescriptor? target = topology.Displays.SingleOrDefault(
            display => display.Id == plan.TargetDisplayId);
        targetReachedCandidateBounds = target?.Bounds == plan.CandidateBounds;
        observed = true;
    }

    public DesktopTransitionGuardProbeResult GetResult() => new(
        plan,
        State,
        ArmedBeforeChange: resources.Count > 0 || disposed,
        PostArmWindowMutationCount: 0,
        shellGenerationUnchanged,
        targetReachedCandidateBounds,
        OwnedWindowsRemain: resources.Any(item =>
            item.Window != 0 && DesktopNativeMethods.IsWindow(item.Window)));

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await dispatcher.InvokeAsync(CleanupOnDispatcher, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            disposed = true;
            await dispatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ArmOnDispatcher(uint guardColorReference)
    {
        ValidateAttachment();
        instance = DesktopNativeMethods.GetModuleHandle(null);
        WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = windowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (instance == 0 || DesktopNativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Transition guard class registration failed.");
        }
        registered = true;

        nint parent = ToHandle(attachmentLease.ParentWindowHandle);
        nint insertAfter = ToHandle(activeSurfaceWindowHandle);
        if (!DesktopNativeMethods.GetWindowRect(parent, out NativeRect parentBounds))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the guard parent bounds.");
        }

        foreach (DisplayBounds region in plan.GuardRegions)
        {
            GuardWindowResources item = CreateGuardWindow(
                parent,
                insertAfter,
                parentBounds,
                region,
                guardColorReference);
            resources.Add(item);
            insertAfter = item.Window;
        }

        ValidateArmedZOrder(parent);

        int flushResult = DesktopNativeMethods.DwmFlush();
        if (flushResult != 0)
        {
            throw new InvalidOperationException(
                $"Transition guard DwmFlush failed (HRESULT=0x{flushResult:X8}).");
        }
    }

    private GuardWindowResources CreateGuardWindow(
        nint parent,
        nint insertAfter,
        NativeRect parentBounds,
        DisplayBounds region,
        uint colorReference)
    {
        nint window = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate |
                DesktopNativeMethods.WindowExStyleToolWindow |
                DesktopNativeMethods.WindowExStyleNoRedirectionBitmap,
            className,
            "LiveWall Transition Guard — diagnostic only",
            DesktopNativeMethods.WindowStyleChild |
                DesktopNativeMethods.WindowStyleDisabled |
                DesktopNativeMethods.WindowStyleClipChildren |
                DesktopNativeMethods.WindowStyleClipSiblings,
            checked(region.X - parentBounds.Left),
            checked(region.Y - parentBounds.Top),
            region.Width,
            region.Height,
            parent,
            0,
            instance,
            0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Transition guard HWND creation failed.");
        }

        RaisedDesktopDiagnosticWindowRegistry.Register(window);
        GuardWindowResources item = new(window);
        try
        {
            item.Device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            item.Context = item.Device.ImmediateContext;
            item.DxgiDevice = item.Device.QueryInterface<IDXGIDevice>();
            item.Factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(debug: false);
            SwapChainDescription1 description = new(
                checked((uint)region.Width),
                checked((uint)region.Height),
                Format.B8G8R8A8_UNorm,
                stereo: false,
                Usage.RenderTargetOutput,
                bufferCount: 2,
                Scaling.Stretch,
                SwapEffect.FlipSequential,
                AlphaMode.Ignore,
                SwapChainFlags.None);
            item.SwapChain = item.Factory.CreateSwapChainForComposition(
                item.Device,
                description,
                restrictToOutput: null);
            item.BackBuffer = item.SwapChain.GetBuffer<ID3D11Texture2D>(0);
            item.RenderTarget = item.Device.CreateRenderTargetView(item.BackBuffer);
            item.CompositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(item.DxgiDevice);
            item.CompositionDevice.CreateTargetForHwnd(
                window,
                true,
                out IDCompositionTarget? target).CheckError();
            item.Target = target;
            item.CompositionDevice.CreateVisual(out IDCompositionVisual? visual).CheckError();
            item.Visual = visual;
            item.Visual!.SetContent(item.SwapChain).CheckError();
            item.Target!.SetRoot(item.Visual).CheckError();
            item.Context.ClearRenderTargetView(item.RenderTarget, ToOpaqueColor(colorReference));
            item.SwapChain.Present(1, PresentFlags.None).CheckError();
            item.CompositionDevice.Commit().CheckError();
            if (!DesktopNativeMethods.SetWindowPos(
                    window,
                    insertAfter,
                    checked(region.X - parentBounds.Left),
                    checked(region.Y - parentBounds.Top),
                    region.Width,
                    region.Height,
                    DesktopNativeMethods.SetWindowPositionNoActivate |
                        DesktopNativeMethods.SetWindowPositionShowWindow))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Transition guard placement failed.");
            }
            return item;
        }
        catch
        {
            item.Dispose();
            RaisedDesktopDiagnosticWindowRegistry.Unregister(window);
            _ = DesktopNativeMethods.DestroyWindow(window);
            throw;
        }
    }

    private void CleanupOnDispatcher()
    {
        List<Exception> failures = [];
        foreach (GuardWindowResources item in resources.AsEnumerable().Reverse())
        {
            try
            {
                item.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            RaisedDesktopDiagnosticWindowRegistry.Unregister(item.Window);
            if (item.Window != 0 && DesktopNativeMethods.IsWindow(item.Window) &&
                !DesktopNativeMethods.DestroyWindow(item.Window))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Transition guard HWND cleanup failed."));
            }
        }
        resources.Clear();
        if (registered && !DesktopNativeMethods.UnregisterClass(className, instance))
        {
            failures.Add(new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Transition guard class cleanup failed."));
        }
        registered = false;
        if (failures.Count > 0)
        {
            throw new AggregateException("Transition guard cleanup failed.", failures);
        }
    }

    private void ValidateAttachment()
    {
        nint parent = ToHandle(attachmentLease.ParentWindowHandle);
        nint activeSurface = ToHandle(activeSurfaceWindowHandle);
        if (!attachmentLease.HasCurrentShellGeneration() ||
            !DesktopNativeMethods.IsWindow(parent) ||
            !DesktopNativeMethods.IsWindow(ToHandle(attachmentLease.ZOrderAnchorWindowHandle)) ||
            !DesktopNativeMethods.IsWindow(ToHandle(attachmentLease.BackdropWindowHandle)) ||
            !DesktopNativeMethods.IsWindow(activeSurface) ||
            DesktopNativeMethods.GetParent(activeSurface) != parent)
        {
            throw new DesktopAttachPointUnavailableException(
                "Transition guard attachment context is stale.");
        }
    }

    private void ValidateArmedZOrder(nint parent)
    {
        nint anchor = ToHandle(attachmentLease.ZOrderAnchorWindowHandle);
        nint activeSurface = ToHandle(activeSurfaceWindowHandle);
        nint backdrop = ToHandle(attachmentLease.BackdropWindowHandle);
        nint previous = activeSurface;
        if (!IsBeforeInSiblingZOrder(parent, anchor, activeSurface))
        {
            throw new DesktopAttachPointUnavailableException(
                "Transition guard active Surface is not behind DefView.");
        }

        foreach (GuardWindowResources item in resources)
        {
            if (DesktopNativeMethods.GetParent(item.Window) != parent ||
                !IsBeforeInSiblingZOrder(parent, previous, item.Window))
            {
                throw new DesktopAttachPointUnavailableException(
                    "Transition guard HWND did not enter the requested sibling Z-order.");
            }
            previous = item.Window;
        }

        if (!IsBeforeInSiblingZOrder(parent, previous, backdrop))
        {
            throw new DesktopAttachPointUnavailableException(
                "Transition guard HWND is not in front of the Raised Desktop backdrop.");
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

    private static Color4 ToOpaqueColor(uint colorReference) => new(
        (colorReference & 0xFF) / 255f,
        ((colorReference >> 8) & 0xFF) / 255f,
        ((colorReference >> 16) & 0xFF) / 255f,
        1f);

    private static nint ToHandle(ulong value) => unchecked((nint)(long)value);

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(window, message, wParam, lParam);

    private sealed class GuardWindowResources(nint window) : IDisposable
    {
        public nint Window { get; } = window;
        public ID3D11Device Device { get; set; } = null!;
        public ID3D11DeviceContext Context { get; set; } = null!;
        public IDXGIDevice DxgiDevice { get; set; } = null!;
        public IDXGIFactory2 Factory { get; set; } = null!;
        public IDXGISwapChain1 SwapChain { get; set; } = null!;
        public ID3D11Texture2D BackBuffer { get; set; } = null!;
        public ID3D11RenderTargetView RenderTarget { get; set; } = null!;
        public IDCompositionDevice CompositionDevice { get; set; } = null!;
        public IDCompositionTarget? Target { get; set; }
        public IDCompositionVisual? Visual { get; set; }

        public void Dispose()
        {
            if (Target is not null)
            {
                Target.SetRoot(null!).CheckError();
                CompositionDevice?.Commit().CheckError();
            }
            RenderTarget?.Dispose();
            BackBuffer?.Dispose();
            SwapChain?.Dispose();
            Visual?.Dispose();
            Target?.Dispose();
            CompositionDevice?.Dispose();
            Factory?.Dispose();
            DxgiDevice?.Dispose();
            Context?.ClearState();
            Context?.Flush();
            Context?.Dispose();
            Device?.Dispose();
        }
    }
}

internal sealed record DesktopTransitionGuardArmContext(
    DesktopAttachmentLease AttachmentLease,
    ulong ActiveSurfaceWindowHandle);
