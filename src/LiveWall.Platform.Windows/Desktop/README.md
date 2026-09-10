# Desktop

封装 WorkerW/Progman/Raised Desktop 的版本化适配边界、Explorer/Shell 失效信号和 Desktop Surface 生命周期。所有未公开 Shell 消息和窗口发现策略只允许存在于此目录。

## 当前实现状态

- `RaisedDesktopAdapter` 已实现内部 lease 发现，但生产 `ValidatedBuilds` 为空，正常 Selector 会跳过；显式诊断可验证未知 build，但不得把结果描述为“已支持 Raised Desktop”。
- Legacy WorkerW 已有结构验证逻辑，但仍是 Experimental；生产 `ValidatedBuilds` 为空，当前完整 build `26200.9168` 的诊断没有发现符合既定规则的候选窗口。
- `DesktopWindowDispatcher` 已提供专用 STA 窗口线程和消息循环；Surface 的注册、创建、定位、显隐、换父级与销毁均封送到该线程。
- `CreateSurfaceAsync` 已创建隐藏 provisional Surface，`ReplaceSurfacesAsync` 在 Dispatcher 上批量激活新 Surface 并隐藏同 generation 的被替换 Surface；跨 generation 的旧 HWND 只退休记录，不再被显隐或复用。
- 隐藏顶级信号窗口已接收 `TaskbarCreated`；`ExplorerMonitor` 使用 350 ms 防抖，并保留 2 秒句柄/Progman 验证作为后备。
- Host 事件桥已把验证后的失效投递为 `ExplorerRestartedCommand`；恢复路径会重建 Surface、重新发送 `SetBounds/AttachSurface` 并进行替换，不再把旧 HWND 换父级。
- `DesktopHostDiagnostics --shell-topology` 已记录 session/window station/desktop，使用 `GetShellWindow` 和 `EnumChildWindows` 采集递归后代，并记录完整 build/UBR、parent/owner、PID/TID、styles、bounds、visibility 与同级 Z-order；Shell 不可见时返回无结论状态和退出码 3。Legacy `--color-block` 在无合规附着点时以退出码 4 失败关闭。
- 隔离 Raised Desktop 诊断已提供结构探针，以及 layered GDI、non-layered GDI、DirectComposition composition swap chain 三种 presentation probe。预检要求 `WinSta0\Default`、Shell anchor Progman、直接 DefView、同 Explorer 进程和 350 ms 稳定指纹；显式请求使用 `0x0D/0x01`。当前 build 已验证单屏结构顺序 `DefView > LiveWall > WorkerW` 和 LiveWall 自有 HWND 清理；两种 GDI 路径不可见。无边框 Host-owned DComp、独立 Renderer-child v1 binding、正常/取消/崩溃清理及同 Renderer 跨 generation 功能恢复已经通过；竞争桌面 Surface 以 `CompetingDesktopSurface` 失败关闭。
- `DesktopAttachmentLease`, Adapter Selector, WindowsDesktopHost 与 `NativeDesktopSurfaceFactory` 已完成生产形态接线。Raised Factory 消费 Progman parent、DefView anchor、WorkerW backdrop、结构指纹与 generation，创建 no-redirection hidden container 并显式维持 Z-order；Application 只接收无句柄 capability，Renderer 只接收 LiveWall 容器 HWND。

因此本目录的核心机制已经落地并有自动化覆盖，但桌面附着兼容性仍未完成真实验收；Stage B 保持 `In progress / Not accepted`。

## 持续边界

- 保持唯一专用 STA Window Dispatcher；全部自有 HWND 操作封送到该线程并运行稳定消息循环。
- Dispatcher 使用隐藏顶级窗口接收 `TaskbarCreated`，后备探针只提供失效提示；信号统一 350 ms 防抖。
- `CreateSurfaceAsync` 只创建隐藏 provisional Surface；Host 等待全部首帧后调用批量 `ReplaceSurfacesAsync`。
- Explorer/Shell 恢复时重新发现并验证 attach point、重建 Surface、发送 `SetBounds/AttachSurface`，不得依赖旧 HWND。
- Raised Desktop 只有在 build allowlist、类名/进程/父子/Z 顺序验证、真实像素 presentation probe 和完整色块矩阵均通过后才能启用；结构候选不是生产 attach point。
- Shell HWND、Z-order anchor 和 backdrop 只属于 Platform.Windows 当前 Shell generation 的内部 attachment lease，不得进入 Application 持久模型或跨进程协议。
- Renderer Protocol v1 的 `AttachSurface.windowHandle` 仅表示 `HwndChild` binding。Renderer 在自有 child HWND 内绑定自有 DirectComposition target/交换链仍属于 v1；只有需要跨进程传递共享纹理、交换链句柄、fence 或其他非容器 HWND 对象时，才引入强类型 Surface binding 和协议版本变更。
- Renderer-child 与 Lease/Factory 设计门禁已经关闭；下一项门禁是显式诊断下的实际 Adapter → Lease → Native Factory → Renderer v1 全链，以及 DPI、热插拔、多屏、Shell mutation、异常退出和恢复体验矩阵。

完整接口、顺序、失败语义和验收门槛见 `docs/windows-desktop-host.md`、`docs/implementation-status.md`、ADR-007 与 ADR-008。
