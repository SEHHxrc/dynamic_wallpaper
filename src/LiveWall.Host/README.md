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

2026-10-24 的最新进度：**P1.9.5 按统一台账 E 节有限条件关闭。** 停止事务、资源排空、原生通知与双回执、受控进程、单屏活动 Close、无 Renderer 真实注销均通过；退出顺序修正后的活动 Renderer 单屏真实注销也已通过，收到 ShutdownCompleted，Renderer ExitCode=0、自有 Surface=0，配置保留及人工确认完整。最近一次 Debug/Release `428/428`、合成证据 25/25、受控 Host 5/5、无 Surface Renderer 2/2 通过。首轮缺少回执的失败不追改；Main/实际 Host 退出码仍未观察，不与 Renderer 或核验脚本退出码混淆，不泛化为真实关机/重启、双屏退出或登录自启动已验收。P1.9.2 双屏系统恢复及 P1.9.4 真实 Shell/display 并发和双屏实机为独立延期项，Stage B/allowlist 不变。RDP 为 OutOfScope/WaivedForV1，本地用户切换 NotValidated。下文历史数字不代表最新基线。

`ExplorerRestartedCommand` 已进入命令处理分支，Host 已实现重建 Surface、同一 Renderer 重新 Attach、等待第二次首帧后显式替换的恢复正向路径；独立 Renderer-child 跨 Shell generation、P1.7 局部失败整批重建和清理已经通过自动与代表性人工验收。P1.6 的物理单屏、双屏长驻、process-cold 与连续替换已通过；P1.8 的拓扑抢占、主屏切换、DPI/bounds+scale、位置/原点、旋转、热插拔和 Span 代表矩阵也已按条件性边界关闭。pure bounds-only 与物理零显示器保持 `EnvironmentInconclusive`，旋转/扩张保留 ADR-009 的 `TransientCoverageGap`。

P1.9.0 已接通真实 `Program`、唯一 Composition Root 和 `WindowsHostRuntime` 生命周期；Stopping 后拒绝外部变更，迟到完成只能清理，Shutdown 幂等。P1.9.1 正式 TaskbarCreated 事件链已验收，真实双屏 Explorer 恢复为 `Succeeded + Rebuilt`。P1.9.2 自动及物理单屏代表场景通过，双屏系统恢复待补。P1.9.3.0 已接通会话断开提交门禁、环境延后处理、revision 关联验证与可等待 Resume/Reconfigure/Rebuild 终态，当时 Debug/Release 基线 `262/262`；后续首版范围调整与当前状态以上文和统一台账为准。P1.9.5 已关闭，但生产壁纸仓库与 Video/Web Renderer、并发恢复等剩余门禁仍未完成，Stage B 为 `In progress / Not accepted`，两类生产 allowlist 为空。

2026-10-11 补齐实际播放命令/Renderer 回执证据、交叠系统状态恢复、用户暂停意图、最后关停回执竞态及显式 suspend/resume 通知注册/释放后，最新 Debug/Release 自动基线为 `246/246`。物理单屏基线、锁解 3/3、用户暂停锁解 1/1、S0 Modern Standby 睡眠恢复 2/2 已通过，首个未完成睡眠样本保留。睡眠后环境变化可走已有恢复/重规划终态，不要求所有样本身份保持；双屏系统恢复待补测，P1.9.2 整体未全部关闭。
