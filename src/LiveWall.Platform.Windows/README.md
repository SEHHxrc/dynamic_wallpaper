# LiveWall.Platform.Windows

## 职责

实现 Windows 专属端口：桌面附着点、显示器枚举、电源/会话/前台窗口事件、输入、音频采集、Job Object 和受控 P/Invoke。它提供事实与机制，不决定“何时暂停”等产品策略。

## 对外接口

实现 `LiveWall.Application` 中的 `IDesktopHost`、`IDesktopHostAdapter`、`IDisplayTopologySource` 和 `IProcessSupervisor`；对外只返回 Application/Domain 模型，不暴露 COM 对象或裸原生资源所有权。

Stage B 目标要求 `IDesktopHost` 额外提供批量 `ReplaceSurfacesAsync`，并让 `CreateSurfaceAsync` 只创建隐藏 provisional Surface。当前代码尚未实现这两个语义；以 `docs/windows-desktop-host.md` 和 ADR-007 为后续修改依据。

## 依赖规则

只引用 Application 和 Domain。不得引用 Host、Infrastructure、Importers、UI 或 Renderer。所有未公开 Shell 行为只能出现在 `Desktop` 适配器中；所有 P/Invoke 声明只能出现在 `NativeMethods`。

## 目录

| 目录 | 功能 | 边界 |
|---|---|---|
| `Desktop` | 实验性 WorkerW、Raised Desktop 占位、Explorer 监视、Surface 骨架 | 未公开 Shell 细节不得外泄；当前未通过 Stage B 验收 |
| `Displays` | DisplayConfig 枚举、稳定 ID、拓扑防抖 | 禁止使用显示器数组序号持久化 |
| `Power` | 电源、屏幕开关和电池事件 | 只报告状态 |
| `Sessions` | 锁屏、解锁和 RDP 事件 | 只报告状态 |
| `Foreground` | 全屏/最大化窗口检测 | 不直接暂停 Renderer |
| `Input` | 桌面指针与交互路由 | 权限控制由 Application 决定 |
| `Audio` | WASAPI Loopback 和 FFT | 壁纸未声明时不得捕获 |
| `Processes` | Job Object 与子进程监管 | 唯一允许持有 Renderer 进程句柄处 |
| `NativeMethods` | P/Invoke、COM interop 和 SafeHandle | 必须审计所有所有权与错误码 |

所有自有 Surface HWND 的窗口类注册、创建、定位、显隐、换父级和销毁最终必须由同一个专用 STA Window Dispatcher 执行。当前实现仍可能在调用线程创建/销毁 HWND，属于已知待整改项，不是允许长期保留的实现方式。
