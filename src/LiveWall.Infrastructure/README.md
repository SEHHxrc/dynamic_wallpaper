# LiveWall.Infrastructure

## 职责

实现持久化和通用技术能力：JSON 配置、壁纸仓库、受限文件系统、原生 `.lwpkg`、Named Pipe 帧传输与结构化日志。

## 对外接口

主要实现 Application 端口，并向 Host 提供组合根注册扩展。IPC 层读写 `LiveWall.Contracts` DTO，但不得把 DTO 当作业务状态存储。所有文件提交遵循“临时写入 → flush → 校验 → 原子替换”。

## 依赖规则

可引用 Application、Domain、Contracts；不得引用 UI、Renderer 或 Platform.Windows。不得决定播放策略，不得启动 Renderer，不得解析 Wallpaper Engine 私有格式。

## 目录

| 目录 | 功能 | 对外格式 |
|---|---|---|
| `Configuration` | 设置、播放策略和 Assignment | 带 `schemaVersion` 的 UTF-8 JSON |
| `Persistence` | 壁纸仓库、Preset 和最后会话状态 | Application Repository 端口 |
| `FileSystem` | 安全路径、原子文件和临时目录 | 规范化绝对路径 |
| `Packages` | `.lwpkg` ZIP 与 manifest 校验 | `wallpaper.json` + Schema |
| `Ipc` | Pipe ACL、长度帧和 JSON 序列化 | 4-byte LE + UTF-8 JSON |
| `Logging` | 脱敏滚动 JSON 与 EventSource | 关联 message/session/generation |
