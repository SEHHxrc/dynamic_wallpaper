using System.Runtime.InteropServices;

namespace LiveWall.Platform.Windows.NativeMethods;

internal static class ProcessExitNativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessShutdownParameters(out uint level, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessShutdownParameters(uint level, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool ConsoleControlHandler(uint kind);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(ConsoleControlHandler handler,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}
