# Orchestration

协调布局计划、Desktop Surface、Renderer Provider/Session 和故障恢复。固定顺序是：创建隐藏 provisional Surface，等待全部新 Renderer 的 `FirstFramePresented`，回投 HostCommandLoop，再由单写者调用 `ReplaceSurfacesAsync` 并提交状态。

当前实现已按上述顺序显式替换 Surface；Explorer 恢复会重建 Surface、重新 Attach Renderer 并尽量保持播放位置，不把旧 HWND 直接换父级。这些路径已通过自动化契约测试，Host-owned DComp 的 Explorer generation 重建也已通过诊断验收；独立 Renderer-child DComp、产品恢复和完整显示矩阵仍未验收。Renderer 内部使用 DComp 不改变现有 `AttachSurface`，除非跨进程开始传递 HWND 之外的 GPU 资源。详细边界与状态见 `docs/windows-desktop-host.md` 和 `docs/implementation-status.md`。
