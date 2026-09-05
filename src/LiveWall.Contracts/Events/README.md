# Events

定义 Host 推送给 UI 的通知 DTO。事件可丢失，不能作为权威状态日志；UI 重连后必须通过 `GetAppState` 恢复。

