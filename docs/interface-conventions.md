# 接口设计约定

## C# 公共接口

- 所有可取消异步操作最后一个参数为 `CancellationToken cancellationToken`；不提供隐藏取消的重载。
- 查询使用 `IReadOnlyList<T>` / `IReadOnlyDictionary<TKey,TValue>`，不得暴露可变集合。
- 值身份使用 `readonly record struct`；进程边界 DTO 使用字符串，再由适配层校验并映射。
- 跨边界时间使用 UTC `DateTimeOffset`；持续时间使用 `TimeSpan` 或明确的 `...Milliseconds` 字段。
- 所有尺寸字段明确单位；窗口句柄用 `ulong` 在线协议中传输，不使用带符号 JSON number。
- `null` 只表达“明确可缺省/不存在”；成功、失败、部分兼容必须有显式状态。

## 错误

跨进程错误统一为：

```json
{
  "code": "renderer.content.unsupported",
  "message": "Human-readable summary",
  "retryable": false,
  "details": { "feature": "scene.particles" }
}
```

- `code` 是稳定、点分、小写的机器标识；UI 不按 `message` 分支。
- `message` 可本地化且不得包含密钥、完整用户路径或敏感 URL 参数。
- 未识别错误映射为模块级 `*.internal`，原始异常只进入脱敏日志。
- `retryable` 只说明同一输入稍后重试可能成功，不等于 Host 必须自动重试。

## 版本

- Renderer 协议使用 `{major, minor}`；破坏性变化增加 major，向后兼容字段增加 minor。
- UI RPC 首版使用整数 `protocolVersion: 1`；破坏性变化创建新方法/协议版本。
- `.lwpkg` manifest 使用 `schemaVersion`；未知主 Schema 版本必须拒绝。
- JSON 新字段默认可选；不得改变既有字段含义或复用已删除字段名。

## JSON 序列化

- 统一使用 `System.Text.Json` 的 Web 默认设置：属性名 camelCase、大小写不敏感读取；
- 协议枚举在线上使用架构规定的字符串值，不依赖 CLR 枚举整数；
- 读取时允许未知字段以支持 minor 前向兼容，写出时不得包含未声明的调试对象；
- `JsonElement` 只允许存在于 Contracts 的受限 payload/属性值处，进入 Application 前必须映射和校验；
- 禁止启用基于 CLR 类型名的多态反序列化。

## 幂等与并发

- 每条请求/命令带唯一 `requestId` 或 `messageId`。
- Renderer 消息必须带 `sessionId + generation`；小于活动 Generation 的消息直接丢弃并记录调试日志。
- Apply/Stop/Shutdown 等命令必须可安全重复处理。
- Host 状态修改只发生在 CommandLoop；接口实现不得从回调线程反向修改 Host 状态。

## 路径

- UI 只可提交用户选择得到的导入源路径，不能提交目标 library 路径或命令行。
- Renderer 收到的 `contentRoot` 必须由 Host 产生，入口规范化后必须位于该根目录下。
- 包内路径统一 `/`，拒绝绝对路径、盘符、UNC、设备路径、`..` 和重解析点。
