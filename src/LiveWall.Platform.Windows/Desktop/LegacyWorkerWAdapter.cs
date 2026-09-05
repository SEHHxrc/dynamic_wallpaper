using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Application.Sessions;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

public sealed class LegacyWorkerWAdapter : IDesktopHostAdapter
{
    public const string AdapterId = "legacy-workerw-v1";

    public bool IsSupported(WindowsShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return true;
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
        nint worker = FindWallpaperWorkerWindow();
        if (worker == 0 || !DesktopNativeMethods.IsWindow(worker))
        {
            throw new DesktopAttachPointUnavailableException(
                "A WorkerW window behind the desktop icons was not found.");
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

    private static nint FindWallpaperWorkerWindow()
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

    private static ulong ToPublicHandle(nint handle) =>
        unchecked((ulong)handle.ToInt64());
}
