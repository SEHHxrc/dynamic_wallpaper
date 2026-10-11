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

`ExplorerRestartedCommand` 已进入命令处理分支，Host 已实现重建 Surface、同一 Renderer 重新 Attach、等待第二次首帧后显式替换的恢复正向路径；独立 Renderer-child 跨 Shell generation、P1.7 局部失败整批重建和清理已经通过自动与代表性人工验收。P1.6 的物理单屏、双屏长驻、process-cold 与连续替换已通过；P1.8 的拓扑抢占、主屏切换、DPI/bounds+scale、位置/原点、旋转、热插拔和 Span 代表矩阵也已按条件性边界关闭。pure bounds-only 与物理零显示器保持 `EnvironmentInconclusive`，旋转/扩张保留 ADR-009 的 `TransientCoverageGap`。

P1.9.0 已接通真实 `Program`、唯一 Composition Root 和 `WindowsHostRuntime` 生命周期。启动会加载完整 Assignment、只对在线显示器恢复并等待 generation 终态；关停后命令循环拒绝新的 Apply/拓扑/Explorer 变更，迟到异步完成只能清理而不能 Replace，重复 Shutdown 保持幂等。P1.9.1 又关闭了正式 `TaskbarCreated` 事件链：10 次无变化 burst 为严格 No-op，真实 Explorer 重启只形成一次恢复事务和一次批量 Replace，本次双屏实机终态为 `Succeeded + Rebuilt`。Debug/Release 自动基线为 224/224，Release 组合根烟雾与两项事件链诊断均以退出码 0 完成。当前生产壁纸仓库与 Video/Web Renderer 仍是未实现边界，所以正式内容恢复尚未完成；P1.9.2–P1.9.5 的系统会话、并发恢复和退出竞态矩阵仍待执行。Stage B 继续为 `In progress / Not accepted`，两类生产 allowlist 继续为空。
P1.9.0 已接通真实 `Program`、唯一 Composition Root 和 `WindowsHostRuntime` 生命周期。启动会加载完整 Assignment、只对在线显示器恢复并等待 generation 终态；关停后命令循环拒绝新的 Apply/拓扑/Explorer 变更，迟到异步完成只能清理而不能 Replace，重复 Shutdown 保持幂等。P1.9.1 又关闭了正式 `TaskbarCreated` 事件链：10 次无变化 burst 为严格 No-op，真实 Explorer 重启只形成一次恢复事务和一次批量 Replace，本次双屏实机终态为 `Succeeded + Rebuilt`。P1.9.2 已接通 WTS/Power 完整快照、revision 去重和验证后恢复，Debug/Release 自动基线为 235/235；真实锁屏与睡眠入口待人工验收。当前生产壁纸仓库与 Video/Web Renderer 仍是未实现边界，所以正式内容恢复尚未完成；P1.9.3–P1.9.5 的会话切换、并发恢复和退出竞态矩阵仍待执行。Stage B 继续为 `In progress / Not accepted`，两类生产 allowlist 继续为空。
