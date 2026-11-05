using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Lifecycle;

public enum WindowsShutdownRole { Host, Renderer }

// Process-local session-end ordering only, not CPU priority or a global OS policy.
public sealed class WindowsProcessShutdownOrder : IDisposable
{
    public const uint HostLevel = 0x281;
    public const uint RendererLevel = 0x280;
    private readonly IProcessShutdownParameters native;
    private readonly uint previousLevel;
    private readonly uint previousFlags;
    private int disposed;

    public uint Level { get; }

    public static WindowsProcessShutdownOrder ForHost() => new(WindowsShutdownRole.Host, new NativeParameters());
    public static WindowsProcessShutdownOrder ForRenderer() => new(WindowsShutdownRole.Renderer, new NativeParameters());

    internal WindowsProcessShutdownOrder(WindowsShutdownRole role, IProcessShutdownParameters native)
    {
        this.native = native;
        Level = role switch
        {
            WindowsShutdownRole.Host => HostLevel,
            WindowsShutdownRole.Renderer => RendererLevel,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        if (!native.Get(out previousLevel, out previousFlags)) throw Error("Could not read this process's shutdown parameters.");
        // Flags=0: do not opt into SHUTDOWN_NORETRY or use system-reserved levels.
        if (!native.Set(Level, 0)) throw Error("Could not configure this process's shutdown order.");
        if (!native.Get(out uint appliedLevel, out uint appliedFlags) || appliedLevel != Level || appliedFlags != 0)
        {
            bool restored = native.Set(previousLevel, previousFlags);
            throw new InvalidOperationException($"Shutdown order readback failed; previous parameters restored={restored}.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && !native.Set(previousLevel, previousFlags))
            throw Error("Could not restore this process's shutdown parameters.");
    }

    private static Win32Exception Error(string detail) => new(Marshal.GetLastWin32Error(), detail);

    private sealed class NativeParameters : IProcessShutdownParameters
    {
        public bool Get(out uint level, out uint flags) => ProcessExitNativeMethods.GetProcessShutdownParameters(out level, out flags);
        public bool Set(uint level, uint flags) => ProcessExitNativeMethods.SetProcessShutdownParameters(level, flags);
    }
}

internal interface IProcessShutdownParameters
{
    bool Get(out uint level, out uint flags);
    bool Set(uint level, uint flags);
}
