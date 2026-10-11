# LiveWall.Platform.Windows

## 职责

实现 Windows 专属端口：桌面附着点、显示器枚举、电源/会话/前台窗口事件、输入、音频采集、Job Object 和受控 P/Invoke。它提供事实与机制，不决定“何时暂停”等产品策略。

## 对外接口

实现 `LiveWall.Application` 中的 `IDesktopHost`、`IDisplayTopologySource`、`ISystemStateSource` 和 `IProcessSupervisor`；`IDesktopHostAdapter` 与 `DesktopAttachmentLease` 是本程序集内部机制。对外只返回 Application/Domain 模型和不含 Shell 句柄的 attachment capability，不暴露 COM 对象或裸原生资源所有权。

`IDesktopHost` 已提供批量 `ReplaceSurfacesAsync`，`CreateSurfaceAsync` 已按约定只创建隐藏 provisional Surface；`ResizeSurfaceAsync` 在显示几何过渡期间调整既有活动容器，Renderer v1 `SetBounds` 同步驱动 child/swap-chain resize。端口与线程机制已有自动化覆盖，但尚未通过真实 Windows Shell 兼容矩阵；以 `docs/windows-desktop-host.md`、`docs/implementation-status.md`、ADR-007 和 ADR-008 为边界。

Shell 兼容诊断已采集完整 build/UBR 和递归窗口结构；生产适配器只允许使用完整 build 键并在每次发现时重新验证结构与 presentation kind。无边框 Host-owned DComp、Renderer-child v1 binding、Explorer generation、P1.6/P1.7 与 P1.8 显示拓扑代表矩阵已经通过。Raised lease 坐标门禁及活动 Host/Renderer 快速 resize 把适配缩短到约 14–34 ms；TG0 证明 DWM 不保证预测区域原子揭示，Guard 不生产化，旋转/扩张保留 `TransientCoverageGap`。Native display snapshot 会重采样暂态不一致，运行期空集合需相隔 350 ms 的连续两次确认。Span 使用物理像素 union、单 Renderer/Surface，position、scale、成员 `2→1→2` 和 `PerDisplay→Span` 实机通过；自有 HWND 使用精确生命周期注册，未削弱第三方竞争检测。P1.9.2 的 WTS/Power 聚合与验证后恢复已通过自动门禁，实机仍待验收。两类生产 allowlist 继续为空；RDP/用户切换、并发恢复和 Shell mutation 矩阵仍未完成。

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
