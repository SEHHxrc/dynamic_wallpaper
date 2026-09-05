# Renderer Protocol v1

## 传输与会话

Host 为每个 Renderer 会话创建仅当前用户可访问的 `LOCAL\` Named Pipe。名称包含随机 128-bit 后缀，每帧为 `uint32 little-endian length` 加 UTF-8 JSON；长度必须在读取载荷前验证，默认上限 `1,048,576` 字节。

Renderer 启动后先发带一次性认证密钥的 `Hello`，Host 以固定时间比较校验密钥、主版本和能力，再发 `Initialize`。握手未完成前除 `Shutdown` 外的控制命令均应拒绝；认证失败或握手超时直接关闭会话。

## Envelope

```json
{
  "protocol": { "major": 1, "minor": 0 },
  "messageId": "01J00000000000000000000000",
  "sessionId": "01J00000000000000000000001",
  "generation": 7,
  "type": "LoadWallpaper",
  "payload": {}
}
```

必填字段不得为 `null`。`messageId` 在一个连接内唯一；重复消息由接收方幂等处理或返回已缓存结果。未知 `type` 返回 `protocol.message.unsupported`，不得反序列化为任意 CLR 类型。

## Host → Renderer

| 类型 | 关键载荷 | 成功事件 |
|---|---|---|
| `Initialize` | rendererId、requestedCapabilities、locale | `Initialized` |
| `AttachSurface` | windowHandle、width、height、scaleFactor | `SurfaceAttached` |
| `LoadWallpaper` | wallpaperId、kind、contentRoot、entryPoint、properties | `ContentLoaded`，随后 `FirstFramePresented` |
| `Play/Pause/Suspend/Resume` | 空对象 | `PlaybackStateChanged` |
| `Throttle` | framesPerSecond、quality | `PlaybackStateChanged` |
| `SetBounds` | x、y、width、height、scaleFactor | 无强制确认 |
| `SetFit` | fitMode | 无强制确认 |
| `SetVolume` | volume `[0,1]`、muted | 无强制确认 |
| `SetProperties` | 名称到受限 JSON 值 | 无强制确认 |
| `SetAudioFrame` | 版本化频域数据 | 无强制确认；后续可迁共享内存 |
| `Shutdown` | reason | `ShutdownCompleted` |

## Renderer → Host

`Hello`、`Initialized`、`SurfaceAttached`、`ContentLoaded`、`FirstFramePresented`、`PlaybackStateChanged`、`TelemetryUpdated`、`RecoverableError`、`FatalError`、`ShutdownCompleted`。

只有收到新会话的 `FirstFramePresented` 后，Host 才能移除旧壁纸。`FatalError` 不得导致 Host 进程退出；Host 按 60 秒窗口执行 0/2/8 秒退避并最终熔断。

## 权限与校验

- `contentRoot` 必须是已验证 library 目录；Renderer 不接受包文件。
- `entryPoint` 必须位于 contentRoot 内。
- `windowHandle` 只接受 Host 创建或验证的窗口。
- `type`、Wallpaper kind、fitMode、quality 使用白名单，不允许任意命令名。
- Pipe 使用 `LOCAL\` 登录会话作用域，客户端只允许 server name `.`，并以 `CurrentUserOnly` 限定同一用户和提权级别。

机器可读基线见 `packages/schemas/renderer-envelope.schema.json`。
