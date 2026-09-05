# Sessions

定义 Renderer Provider/Session、桌面 Surface、显示器事件和进程监管端口。Platform.Windows 拥有桌面容器 Surface，Renderer 在 Host 发送的容器 HWND 内创建子窗口；协议和 Win32 细节由外层适配。

Stage B 的目标 `IDesktopHost` 契约规定：`CreateSurfaceAsync` 返回隐藏 provisional Surface；首帧准备完成后，由 Host 单写者调用批量 `ReplaceSurfacesAsync(SurfaceReplacement, CancellationToken)` 激活新 Surface 并隐藏旧 Surface；`DestroySurfaceAsync` 幂等且由平台封送到 HWND 创建线程。当前代码端口尚未加入替换操作，ADR-007 已接受该变更，后续实现不得继续依赖隐式 Z 顺序。
