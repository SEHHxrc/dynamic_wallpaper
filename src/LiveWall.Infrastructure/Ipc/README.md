# Ipc

实现当前用户 Named Pipe ACL、4-byte little-endian 长度帧、1 MiB 上限与 System.Text.Json 序列化。业务方法分派属于 Host。

- `LengthPrefixedJsonChannel` 固定帧层行为：长度在分配内存前校验，截断和畸形 JSON 统一报告为协议帧错误，并串行化并发读写。
- `LocalNamedPipeFactory` 强制 `LOCAL\livewall.*`、本机客户端、当前用户/提权级别和 byte mode。
- `RendererConnectionCredentials` 为每个 Renderer 生成随机端点和 256-bit 一次性握手密钥。
