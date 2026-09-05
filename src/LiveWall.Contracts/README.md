# LiveWall.Contracts

## 职责

定义 UI ↔ Host 和 Host ↔ Renderer 的稳定线协议。这里只允许可序列化 DTO、消息名称、协议版本和错误代码，不含业务服务、I/O 或进程启动逻辑。

## 公开格式

- 编码：UTF-8 JSON，前置 4 字节 little-endian 无符号消息长度。
- 默认最大帧：1 MiB；超过上限必须在反序列化前拒绝。
- Envelope：`protocol`、`messageId`、`sessionId`、`generation`、`type`、`payload`。
- 兼容：主版本不一致拒绝；次版本通过能力协商；未知 JSON 字段应忽略。
- 标识：跨进程一律使用字符串；原生句柄使用无符号 64 位整数，只能由 Host/Renderer 产生。

权威 JSON Schema 位于 `packages/schemas`，人类可读协议位于 `docs/renderer-protocol.md` 与 `docs/ui-rpc.md`。

## 依赖规则

不引用 Domain、Application、Host、UI 或 Renderer。协议 DTO 不得公开 `Exception`、`Stream`、委托、任意命令行或实现库类型。

## 目录

| 目录 | 功能 | 开放对象 |
|---|---|---|
| `Rpc` | UI RPC 信封和方法名 | `RpcRequest`、`RpcResponse`、`UiRpcMethods` |
| `Ipc` | 本地端点命名契约 | `IpcEndpointNames` |
| `RendererProtocol` | Renderer 信封、握手和载荷 | `RendererEnvelope`、命令/事件类型 |
| `Commands` | Host 接收的强类型请求 DTO | Apply、Import、Policy 等 |
| `Events` | Host 推送给 UI 的事件 DTO | 状态、进度、故障、警告 |
| `Dtos` | 跨消息共享的数据快照 | 壁纸、显示器、诊断、错误 |
