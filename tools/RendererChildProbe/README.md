# RendererChildProbe

`DesktopHostDiagnostics --raised-desktop-renderer-child-probe` 启动的独立辅助进程。它只用于显式 Raised Desktop 诊断，不是 Video/Web Renderer，也不进入生产 allowlist。

父进程通过当前 Renderer Protocol v1 的 `AttachSurfacePayload` 传入 LiveWall 自有 Host 容器 HWND。辅助进程在该容器内创建自有 child HWND，并独占 D3D11 device、DirectComposition target/visual 与 composition swap chain。它不接收 Progman、DefView、WorkerW 或任何 Shell HWND。

事件保证：child 和 DComp target 成功后才发送 `SurfaceAttached`；`Present + Commit + DwmFlush` 成功后才发送 `FirstFramePresented`。正常 Shutdown、父进程取消和故意崩溃模式均由父诊断工具检查自有窗口清理。该工具不能单独运行；连接端点与一次性认证令牌由父进程提供。

## 协议覆盖边界

该工具仍是测试 Renderer，并非 Video/Web Renderer。它现已覆盖一次性令牌 `Hello`、`Initialize → Initialized`、`AttachSurface → SurfaceAttached`、`LoadWallpaper → ContentLoaded → FirstFramePresented`、严格递增 generation 的再次附着和 `Shutdown → ShutdownCompleted`。

进程启动后、连接 Pipe/发送 Hello 前，会设置并读回当前进程的 Windows shutdown level `0x280`；Host 使用 `0x281`，以声明先让 Host 发起清理、再结束 Renderer 的相对顺序。设置失败不进入握手，正常退出恢复原参数。该设置不改变全局策略，也不能保证强制终止时一定收到 ShutdownCompleted；真实注销仍必须独立核验协议回执与资源清理。

测试 Renderer 会拒绝 Attach 前的 Load、重复/倒退 generation、重复 messageId 和未知命令。Host 正式会话另行验证握手/阶段超时、错误令牌、未知事件与重复事件。现在支持加载后的 Play/Resume、Pause、Suspend，并发送真实协议 `PlaybackStateChanged` 回执。静态合成色块没有自主渲染循环，暂停与播放在视觉上相同；回执只证明此测试 Renderer 接受播放控制，不能替代 Video/Web 动态内容的帧计数验收或显示矩阵。
