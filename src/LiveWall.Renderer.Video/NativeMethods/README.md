# NativeMethods

ADR-011 的 Renderer 私有原生边界，仅管理自有 child HWND、消息泵、Pipe server PID 校验和本进程退出顺序。不访问 Progman/WorkerW/DefView、不提供 Shell API、不引用 Platform.Windows。声明限于 VideoNativeMethods.cs，架构检查继续拒绝其他位置的 Win32 声明。
