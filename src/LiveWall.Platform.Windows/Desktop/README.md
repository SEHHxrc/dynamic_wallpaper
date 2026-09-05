# Desktop

封装 WorkerW/Progman/Raised Desktop 的版本化适配边界、Explorer/Shell 失效信号和 Desktop Surface 生命周期。所有未公开 Shell 消息和窗口发现策略只允许存在于此目录。

## 当前实现状态

- `RaisedDesktopAdapter` 是占位实现，发现时主动报告未验证并由 Selector 尝试 Legacy 回退；不得描述为“已支持 Raised Desktop”。
- Legacy WorkerW 已有发现逻辑，但仍是 Experimental，尚未完成跨 Windows build 的真实图标层级验收。
- Surface 当前在调用线程创建并立即显示，缺少专用消息循环、同线程销毁保证、hidden provisional 状态和显式替换端口。
- `ExplorerMonitor` 当前以 1 秒轮询检测 Progman/句柄变化；HostCommandLoop 尚未处理 `ExplorerRestartedCommand`。
- 当前 `RecoverAsync` 重挂已有 HWND，与目标设计的“重建 Surface 并重新 Attach Renderer”不符。

因此本目录当前是 Stage B 骨架，不是已完成实现。

## 目标边界

- 建立唯一专用 STA Window Dispatcher；全部自有 HWND 操作封送到该线程并运行稳定消息循环。
- Dispatcher 使用隐藏顶级窗口接收 `TaskbarCreated`，后备探针只提供失效提示；信号统一 350 ms 防抖。
- `CreateSurfaceAsync` 只创建隐藏 provisional Surface；Host 等待全部首帧后调用批量 `ReplaceSurfacesAsync`。
- Explorer/Shell 恢复时重新发现并验证 attach point、重建 Surface、发送 `SetBounds/AttachSurface`，不得依赖旧 HWND。
- Raised Desktop 只有在 build allowlist、类名/进程/父子/Z 顺序验证和色块矩阵通过后才能启用。

完整接口、顺序、失败语义和验收门槛见 `docs/windows-desktop-host.md` 与 ADR-007。
