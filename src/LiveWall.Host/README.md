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

## 当前 Stage B 差距

产品入口尚未接线；`ExplorerRestartedCommand` 尚未进入命令处理分支；首帧后显式 Surface 替换端口尚未实现。上述能力不得因类或命令已经声明而标记为完成，具体整改以 ADR-007 和 `docs/windows-desktop-host.md` 为准。
