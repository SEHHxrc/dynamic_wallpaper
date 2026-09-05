# UI RPC v1

## 原则

UI 是可随时退出和重建的客户端，Host 是系统状态的权威来源。RPC 使用 `LOCAL\LiveWall.<installationScope>.p1.host`，与 Renderer 相同采用长度前缀 UTF-8 JSON 和 `CurrentUserOnly`，但使用稳定、独立的 Pipe 名称。

## 请求与响应

```json
{
  "protocolVersion": 1,
  "requestId": "01J00000000000000000000000",
  "method": "GetAppState",
  "parameters": {}
}
```

```json
{
  "protocolVersion": 1,
  "requestId": "01J00000000000000000000000",
  "success": true,
  "result": {},
  "error": null
}
```

失败时 `success=false`、`result=null`、`error` 必填。UI 必须把未知事件安全忽略，并在重连后调用 `GetAppState` 重建快照。

## 允许方法

| 分组 | 方法 | 核心输入 |
|---|---|---|
| 状态 | `GetAppState`、`ListWallpapers`、`GetDiagnostics` | 查询/分页参数 |
| 导入 | `ProbeImport`、`ImportWallpaper`、`CancelImport` | 用户选择的源路径、operationId |
| 壁纸 | `ApplyWallpaper`、`RemoveWallpaper`、`SetWallpaperProperty` | wallpaper/display ID、受限属性值 |
| 分配 | `SetAssignment` | displayId、wallpaperId、layout/fit/preset |
| 策略 | `SetPlaybackPolicy` | 强类型策略字段 |
| 播放 | `Play`、`Pause`、`Stop` | 可选目标显示器/会话 |
| 运维 | `OpenLogFolder`、`ShutdownHost` | 无任意路径或命令参数 |

### v1 请求/结果 DTO

| 方法 | parameters | result |
|---|---|---|
| `GetAppState` | `EmptyRequestDto` | `AppStateDto` |
| `ListWallpapers` | `ListWallpapersRequestDto` | `ListWallpapersResultDto` |
| `ProbeImport` | `ProbeImportRequestDto` | `ProbeImportResultDto` |
| `ImportWallpaper` | `ImportWallpaperRequestDto` | `OperationAcceptedDto` |
| `CancelImport` | `CancelImportRequestDto` | `CommandAcceptedDto` |
| `ApplyWallpaper` | `ApplyWallpaperRequestDto` | `CommandAcceptedDto` |
| `RemoveWallpaper` | `RemoveWallpaperRequestDto` | `CommandAcceptedDto` |
| `SetWallpaperProperty` | `SetWallpaperPropertyRequestDto` | `CommandAcceptedDto` |
| `SetAssignment` | `SetAssignmentRequestDto` | `CommandAcceptedDto` |
| `SetPlaybackPolicy` | `PlaybackPolicyDto` | `CommandAcceptedDto` |
| `Play/Pause/Stop` | `PlaybackTargetRequestDto` | `CommandAcceptedDto` |
| `GetDiagnostics` | `EmptyRequestDto` | `DiagnosticsDto` |
| `OpenLogFolder` | `EmptyRequestDto` | `CommandAcceptedDto` |
| `ShutdownHost` | `EmptyRequestDto` | `CommandAcceptedDto` |

DTO 定义位于 `LiveWall.Contracts/Commands` 和 `Dtos`。`StateRevision` 是 Host 快照修订号，不是 Renderer Generation。

## 推送事件

`AppStateChanged`、`LibraryChanged`、`ImportProgressChanged`、`AssignmentChanged`、`PlaybackStateChanged`、`DisplayTopologyChanged`、`RendererFaulted`、`CompatibilityWarningRaised`。

事件信封包含 `protocolVersion`、唯一 `eventId`、`stateRevision`、`type`、`payload`。事件是通知而非权威日志，UI 丢失连接后不得靠补放事件恢复状态。

事件信封和 `AppStateDto` 同时包含单调递增的 `stateRevision`。UI 只应用不小于当前本地修订号的快照或事件；重连后调用 `GetAppState`，以完整快照替换本地状态。

## 禁止

- 任意 executable、命令行、环境变量或 Pipe 名称；
- UI 指定正式 library 目标路径；
- UI 直接传原生窗口/进程句柄；
- 以自由文本替代枚举/错误码；
- 返回未脱敏异常堆栈。

机器可读基线见 `packages/schemas/ui-rpc-request.schema.json`。
