using System.Collections.Concurrent;

namespace LiveWall.Platform.Windows.Desktop;

/// <summary>
/// Exact-HWND registry for LiveWall-owned Raised Desktop host Surfaces. Entries exist only
/// for the lifetime of the corresponding HWND and are never inferred from process identity.
/// </summary>
internal static class RaisedDesktopOwnedWindowRegistry
{
    private static readonly ConcurrentDictionary<nint, byte> Windows = new();

    public static void Register(nint window)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        if (!Windows.TryAdd(window, 0))
        {
            throw new InvalidOperationException("The owned Raised Desktop HWND is already registered.");
        }
    }

    public static void Unregister(nint window) => Windows.TryRemove(window, out _);

    public static bool Contains(nint window) => Windows.ContainsKey(window);
}
