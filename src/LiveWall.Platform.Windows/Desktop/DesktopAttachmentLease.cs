using LiveWall.Application.Sessions;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

internal interface IDesktopHostAdapter
{
    bool IsSupported(WindowsShellSnapshot snapshot);

    Task<DesktopAttachmentLease> DiscoverAsync(CancellationToken cancellationToken);
}

internal enum DesktopSurfacePlacementKind
{
    ParentBottom,
    BetweenAnchorAndBackdrop,
}

internal readonly record struct DesktopShellGeneration(
    ulong ShellWindowHandle,
    uint ShellProcessId);

internal sealed record DesktopAttachmentLease
{
    private DesktopAttachmentLease(
        DesktopAttachmentCapability capability,
        DesktopSurfacePlacementKind placementKind,
        ulong parentWindowHandle,
        ulong zOrderAnchorWindowHandle,
        ulong backdropWindowHandle,
        DesktopShellGeneration shellGeneration,
        string structuralFingerprint)
    {
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        if (string.IsNullOrWhiteSpace(capability.AdapterId) ||
            string.IsNullOrWhiteSpace(capability.PresentationKind) ||
            string.IsNullOrWhiteSpace(capability.RendererBinding))
        {
            throw new ArgumentException("Desktop attachment capability values must be non-empty.", nameof(capability));
        }
        ArgumentOutOfRangeException.ThrowIfZero(parentWindowHandle);
        ArgumentOutOfRangeException.ThrowIfZero(shellGeneration.ShellWindowHandle);
        ArgumentOutOfRangeException.ThrowIfZero(shellGeneration.ShellProcessId);
        if (placementKind == DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop &&
            (zOrderAnchorWindowHandle == 0 || backdropWindowHandle == 0))
        {
            throw new ArgumentException("Raised placement requires both a Z-order anchor and backdrop window.");
        }

        if (placementKind == DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop &&
            string.IsNullOrWhiteSpace(structuralFingerprint))
        {
            throw new ArgumentException(
                "Raised placement requires a structural fingerprint.",
                nameof(structuralFingerprint));
        }

        PlacementKind = placementKind;
        ParentWindowHandle = parentWindowHandle;
        ZOrderAnchorWindowHandle = zOrderAnchorWindowHandle;
        BackdropWindowHandle = backdropWindowHandle;
        ShellGeneration = shellGeneration;
        StructuralFingerprint = structuralFingerprint ?? string.Empty;
    }

    public DesktopAttachmentCapability Capability { get; }

    public DesktopSurfacePlacementKind PlacementKind { get; }

    public ulong ParentWindowHandle { get; }

    public ulong ZOrderAnchorWindowHandle { get; }

    public ulong BackdropWindowHandle { get; }

    public DesktopShellGeneration ShellGeneration { get; }

    public string StructuralFingerprint { get; }

    public ulong ValidationWindowHandle => ParentWindowHandle;

    public static DesktopAttachmentLease CreateLegacy(
        string adapterId,
        ulong parentWindowHandle,
        ulong shellWindowHandle,
        uint shellProcessId) =>
        new(
            new DesktopAttachmentCapability(adapterId, "legacy-workerw-parent", "hwnd-child-v1"),
            DesktopSurfacePlacementKind.ParentBottom,
            parentWindowHandle,
            0,
            0,
            new DesktopShellGeneration(shellWindowHandle, shellProcessId),
            string.Empty);

    public static DesktopAttachmentLease CreateRaised(
        string adapterId,
        ulong parentWindowHandle,
        ulong zOrderAnchorWindowHandle,
        ulong backdropWindowHandle,
        ulong shellWindowHandle,
        uint shellProcessId,
        string structuralFingerprint) =>
        new(
            new DesktopAttachmentCapability(
                adapterId,
                "raised-progman-no-redirection",
                "hwnd-child-v1"),
            DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop,
            parentWindowHandle,
            zOrderAnchorWindowHandle,
            backdropWindowHandle,
            new DesktopShellGeneration(shellWindowHandle, shellProcessId),
            structuralFingerprint);

    internal bool HasCurrentShellGeneration()
    {
        nint shell = DesktopNativeMethods.GetShellWindow();
        if (shell == 0 || ToPublicHandle(shell) != ShellGeneration.ShellWindowHandle)
        {
            return false;
        }

        _ = DesktopNativeMethods.GetWindowThreadProcessId(shell, out uint processId);
        return processId != 0 && processId == ShellGeneration.ShellProcessId;
    }

    private static ulong ToPublicHandle(nint handle) => unchecked((ulong)handle.ToInt64());
}
