# LiveWall.Host

## 职责

唯一长期常驻核心与唯一组合根。负责单实例、依赖装配、托盘、UI RPC、系统事件汇聚、Renderer 监管和会话编排。Host 是运行状态的权威来源。

## 对外接口

- UI：只开放 `docs/ui-rpc.md` 中的 Named Pipe RPC。
- Renderer：只开放 `docs/renderer-protocol.md` 中的会话 Pipe。
- Windows：接收平台事件后转换为 HostCommand 投递到命令循环。

只有 `CommandLoop` 能修改活动 Session、Surface、Assignment 和 Generation。所有回调只投递不可变命令；后台任务完成后携带原 Generation 回投。

## 依赖规则

Host 可引用所有非 UI/Renderer 产品模块并负责装配，但不得让这些实现类型反向渗入 Application。禁止在命令循环中做长时 I/O，禁止在 RPC 回调直接写运行状态。

## 目录

| 目录 | 功能 | 对外边界 |
|---|---|---|
| `Bootstrap` | 单实例、配置、依赖装配、启动恢复 | 唯一 Composition Root |
| `CommandLoop` | 单写者 Channel 与状态转换 | 唯一状态写入点 |
| `Orchestration` | Session/Surface/Renderer 协调 | 等首帧后切换旧壁纸 |
| `Rpc` | DTO 校验、授权与应用模型映射 | 不暴露实现异常 |
| `Tray` | 最小托盘命令入口 | 只投递 HostCommand |

## 当前 Stage B 状态

`ExplorerRestartedCommand` 已进入命令处理分支，Host 已实现重建 Surface、同一 Renderer 重新 Attach、等待第二次首帧后显式替换的恢复正向路径；独立 Renderer-child 跨 Shell generation 的功能恢复已通过自动与人工验收。Platform.Windows 在成功替换时不会触碰或复用旧 generation HWND。Host 组合根现已具备正式 Renderer v1 进程 Provider、Hello 认证、Initialize/Initialized、Attach/Load 阶段门禁、单接收泵和超时/退出错误映射；每个 Apply generation 还会发布不可变单调时间线、强类型终态和按 Surface 数量推导的统一 deadline，诊断入口不再固定等待 15 秒。Apply deadline lease 在终态立即释放，活动 Session 不持有 operation；正常替换、Shutdown 和 Host Dispose 使用独立逐 Renderer retirement budget，并聚合 `ShutdownTimeout`、Dispose 与 Surface cleanup 结果。显式产品候选在双屏交互式环境的主屏单 Surface 上已提交真实首帧，并在可观测性接线后复测为约 1.052 秒首帧、约 2.111 秒清理完成。恢复失败时 Host 不再附回 previous Surface，而是失效活动会话、保留 Assignment、放弃旧 generation 记录，并以全新 Renderer/Surface 限次退避重建，预算耗尽后以 `CircuitBroken` 失败关闭。首次历史产品候选超时仍不可事后归因，且冷启动、物理单屏、双屏覆盖、真实故障、DPI、热插拔矩阵仍未验收，因此 Stage B 仍为 `In progress / Not accepted`。具体边界与状态以 ADR-007、ADR-008、`docs/windows-desktop-host.md` 和 `docs/implementation-status.md` 为准。
