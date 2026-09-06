using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Application.Sessions;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

public sealed class LegacyWorkerWAdapter : IDesktopHostAdapter
{
    public const string AdapterId = "legacy-workerw-v1";
    private static readonly string[] ValidatedBuilds = [];
    private readonly bool allowUnvalidatedBuild;

    public LegacyWorkerWAdapter()
    {
    }

    internal LegacyWorkerWAdapter(bool allowUnvalidatedBuild)
    {
        this.allowUnvalidatedBuild = allowUnvalidatedBuild;
    }

    public bool IsSupported(WindowsShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return allowUnvalidatedBuild ||
            (snapshot.HasCompleteBuildIdentity &&
                ValidatedBuilds.Contains(snapshot.FullBuild, StringComparer.Ordinal));
    }

    public Task<DesktopAttachPoint> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint progman = DesktopNativeMethods.FindWindow("Progman", null);
        if (progman == 0)
        {
            throw new DesktopAttachPointUnavailableException("The Progman desktop window was not found.");
        }

        RequestWorkerWindow(progman);
        nint worker = FindWallpaperWorkerWindow(progman);
        if (worker == 0)
        {
            throw new DesktopAttachPointUnavailableException(
                "A WorkerW window behind the desktop icons did not satisfy the process, ownership, DefView, and desktop-bounds rules.");
        }

        return Task.FromResult(new DesktopAttachPoint(ToPublicHandle(worker), AdapterId));
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        _ = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequestWorkerWindow(nint progman)
    {
        nint result = DesktopNativeMethods.SendMessageTimeout(
            progman,
            DesktopNativeMethods.WorkerWMessage,
            0x0D,
            0,
            DesktopNativeMethods.SendMessageTimeoutAbortIfHung,
            1000,
            out _);
        if (result == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "Progman did not respond while creating WorkerW.");
            }
        }
    }

    private static nint FindWallpaperWorkerWindow(nint progman)
    {
        nint wallpaperWorker = 0;
        DesktopNativeMethods.EnumWindowsProcedure callback = (topLevel, _) =>
        {
            nint defView = DesktopNativeMethods.FindWindowEx(
                topLevel,
                0,
                "SHELLDLL_DefView",
                null);
            if (defView == 0)
            {
                return true;
            }

            wallpaperWorker = DesktopNativeMethods.FindWindowEx(
                0,
                topLevel,
                "WorkerW",
                null);
            if (!IsValidWallpaperWorker(wallpaperWorker, progman))
            {
                wallpaperWorker = 0;
            }

            return wallpaperWorker == 0;
        };

        if (!DesktopNativeMethods.EnumWindows(callback, 0) && wallpaperWorker == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "EnumWindows failed while locating WorkerW.");
            }
        }

        return wallpaperWorker;
    }

    private static bool IsValidWallpaperWorker(nint worker, nint progman)
    {
        if (worker == 0 ||
            !DesktopNativeMethods.IsWindow(worker) ||
            DesktopNativeMethods.GetWindow(
                worker,
                DesktopNativeMethods.GetWindowOwner) != 0 ||
            DesktopNativeMethods.FindWindowEx(
                worker,
                0,
                "SHELLDLL_DefView",
                null) != 0)
        {
            return false;
        }

        _ = DesktopNativeMethods.GetWindowThreadProcessId(progman, out uint progmanProcessId);
        _ = DesktopNativeMethods.GetWindowThreadProcessId(worker, out uint workerProcessId);
        if (progmanProcessId == 0 || workerProcessId != progmanProcessId)
        {
            return false;
        }

        return DesktopNativeMethods.GetWindowRect(progman, out NativeRect progmanBounds) &&
            DesktopNativeMethods.GetWindowRect(worker, out NativeRect workerBounds) &&
            HasPositiveArea(workerBounds) &&
            HasEqualBounds(workerBounds, progmanBounds);
    }

    private static bool HasPositiveArea(NativeRect bounds) =>
        bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;

    private static bool HasEqualBounds(NativeRect left, NativeRect right) =>
        left.Left == right.Left &&
        left.Top == right.Top &&
        left.Right == right.Right &&
        left.Bottom == right.Bottom;

    private static ulong ToPublicHandle(nint handle) =>
        unchecked((ulong)handle.ToInt64());
}
