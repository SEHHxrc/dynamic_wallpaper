# Orchestration

协调布局计划、Desktop Surface、Renderer Provider/Session 和故障恢复。目标顺序是：创建隐藏 provisional Surface，等待全部新 Renderer 的 `FirstFramePresented`，回投 HostCommandLoop，再由单写者调用 `ReplaceSurfacesAsync` 并提交状态。

当前实现虽然等待首帧，但 Surface 创建时已经显示且没有显式替换调用，因此尚不满足该边界。Explorer 恢复也必须重建 Surface 并重新 Attach Renderer，不能把旧 HWND 直接换父级。详细差距和目标顺序见 `docs/windows-desktop-host.md`。
