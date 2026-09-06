# Sessions

定义 Renderer Provider/Session、桌面 Surface、显示器事件和进程监管端口。Platform.Windows 拥有桌面容器 Surface，Renderer 在 Host 发送的容器 HWND 内创建自有子窗口；协议和 Win32 细节由外层适配。Renderer 可在自有 child HWND 内部使用 DirectComposition/交换链而不改变 Application 端口或协议 v1；只有跨进程 Surface 对象不再是容器 HWND 时才需要新的 binding。

`IDesktopHost` 契约规定：`CreateSurfaceAsync` 返回隐藏 provisional Surface；首帧准备完成后，由 Host 单写者调用批量 `ReplaceSurfacesAsync(SurfaceReplacement, CancellationToken)` 激活新 Surface 并隐藏旧 Surface；`DestroySurfaceAsync` 幂等且由平台封送到 HWND 创建线程。当前端口已包含替换操作；后续实现不得绕过端口或重新依赖隐式 Z 顺序。真实桌面验收状态见 `docs/implementation-status.md`。

`WindowsShellSnapshot` 分开携带基础 build 和 UBR，并提供完整 build 兼容键。生产适配器不得在缺少 UBR 时按基础 build 放行；显式诊断模式可以采样未知版本，但不能修改生产 allowlist。
