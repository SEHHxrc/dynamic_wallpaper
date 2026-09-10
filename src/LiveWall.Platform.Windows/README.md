# LiveWall.Platform.Windows

## 职责

实现 Windows 专属端口：桌面附着点、显示器枚举、电源/会话/前台窗口事件、输入、音频采集、Job Object 和受控 P/Invoke。它提供事实与机制，不决定“何时暂停”等产品策略。

## 对外接口

实现 `LiveWall.Application` 中的 `IDesktopHost`、`IDisplayTopologySource` 和 `IProcessSupervisor`；`IDesktopHostAdapter` 与 `DesktopAttachmentLease` 是本程序集内部机制。对外只返回 Application/Domain 模型和不含 Shell 句柄的 attachment capability，不暴露 COM 对象或裸原生资源所有权。

`IDesktopHost` 已提供批量 `ReplaceSurfacesAsync`，`CreateSurfaceAsync` 已按约定只创建隐藏 provisional Surface。端口与线程机制已有自动化覆盖，但尚未通过真实 Windows Shell 兼容矩阵；以 `docs/windows-desktop-host.md`、`docs/implementation-status.md`、ADR-007 和 ADR-008 为边界。

Shell 兼容诊断已采集完整 build/UBR 和递归窗口结构；生产适配器只允许使用完整 build 键并在每次发现时重新验证结构与 presentation kind，基础 build 号、一次采样的裸 HWND 或 `StructurallyValidated` 结果都不能作为放行依据。`26200.9168` 的两种 GDI 路径不可见；无边框 Host-owned DComp、独立 Renderer-child v1 binding 和跨 Explorer generation 功能恢复已通过。内部 Lease/Factory 与正式 Renderer v1 产品候选入口已接线；交互式主屏单 Surface 首帧已连续两次通过，但首次冷启动超时、物理单屏和双屏覆盖仍待复测。两类生产 allowlist 继续为空，Shell mutation 和剩余显示矩阵未完成。

## 依赖规则

只引用 Application 和 Domain。不得引用 Host、Infrastructure、Importers、UI 或 Renderer。所有未公开 Shell 行为只能出现在 `Desktop` 适配器中；所有 P/Invoke 声明只能出现在 `NativeMethods`。

## 目录

| 目录 | 功能 | 边界 |
|---|---|---|
| `Desktop` | 版本化 WorkerW/Raised Adapter、内部 attachment lease、Explorer 监视、Surface 生命周期 | 未公开 Shell 细节不得外泄；生产 allowlist 为空，当前未通过 Stage B 真实验收 |
| `Displays` | DisplayConfig 枚举、稳定 ID、拓扑防抖 | 禁止使用显示器数组序号持久化 |
| `Power` | 电源、屏幕开关和电池事件 | 只报告状态 |
| `Sessions` | 锁屏、解锁和 RDP 事件 | 只报告状态 |
| `Foreground` | 全屏/最大化窗口检测 | 不直接暂停 Renderer |
| `Input` | 桌面指针与交互路由 | 权限控制由 Application 决定 |
| `Audio` | WASAPI Loopback 和 FFT | 壁纸未声明时不得捕获 |
| `Processes` | Job Object 与子进程监管 | 唯一允许持有 Renderer 进程句柄处 |
| `NativeMethods` | P/Invoke、COM interop 和 SafeHandle | 必须审计所有所有权与错误码 |

所有自有 Surface HWND 的窗口类注册、创建、定位、显隐、换父级和销毁必须由同一个专用 STA Window Dispatcher 执行。当前实现已遵守该所有权边界；后续代码不得重新引入调用线程直接操作 HWND 的路径。
