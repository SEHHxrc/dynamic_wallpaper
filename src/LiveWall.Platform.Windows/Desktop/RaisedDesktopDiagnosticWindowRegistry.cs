using System.Collections.Concurrent;

namespace LiveWall.Platform.Windows.Desktop;

/// <summary>
/// Exact-HWND allowlist for diagnostic resources created in this process. Production adapters
/// never register windows here, and registration never survives process or HWND lifetime.
/// </summary>
internal static class RaisedDesktopDiagnosticWindowRegistry
{
    private static readonly ConcurrentDictionary<nint, byte> Windows = new();

    public static void Register(nint window)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        if (!Windows.TryAdd(window, 0))
        {
            throw new InvalidOperationException("The diagnostic HWND is already registered.");
        }
    }

    public static void Unregister(nint window) => Windows.TryRemove(window, out _);

    public static bool Contains(nint window) => Windows.ContainsKey(window);
}
