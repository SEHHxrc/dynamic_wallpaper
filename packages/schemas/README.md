# Schemas

- `wallpaper.schema.json`：原生壁纸 manifest。
- `renderer-envelope.schema.json`：Renderer 命令/事件信封。
- `ui-rpc-*.schema.json`：UI 请求、响应和事件信封。
- `playback-policy.schema.json`、`assignments.schema.json`：Host 可恢复配置文件。

这里的 Schema 是跨进程/包格式的机器可读基线。UI RPC 请求、响应与事件分别拥有 Schema；Renderer 信封及其白名单 payload 由契约测试共同约束。文件名保持稳定；破坏性版本使用新文件名或新 Schema major，不原地改写既有语义。
