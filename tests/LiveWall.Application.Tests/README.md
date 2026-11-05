# LiveWall.Application.Tests

使用 Fake Repository/Renderer/Desktop/Clock 验证 Apply/Pause/Stop/Crash 状态和 Provider 能力选择，不依赖真实 Windows 会话。

C1.2 的 `VideoProductCandidateTests` 新增 26 项，覆盖显式呈现确认/只读分离、时限和参数、Video fixture 与权限、Shell mutation 三基线、阶段失败零提交和提交后取消清理。不加载 libmpv 或启动真实视频；实际原生隐藏产品链由 VideoPresentationProbe 独立记录，不混入测试数或桌面可见结论。

2026-11-02 补充 7 项 `RendererAcknowledgedExitTests`：回执后进程延迟正常返回、回执后卡住的有界终止、未回执直接清理、忽略取消的等待、重复/并发 Dispose、已退出进程不再终止。另补 6 项清理分类门禁，避免把关闭回执、实际退出码与资源销毁混为一个结论。它们不创建可见桌面 Surface，不等同于视频实机验收。

C1.3 再补 4 项：ActiveFailure 通知不消费阶段事件；已知在途播放回执到达 Bounds/新 binding 时不能冒充阶段完成；未发送对应命令的播放回执拒绝。可见阶段入口另补 3 项，覆盖确认门禁、只读分离与固定 20 秒（四段各 5 秒）计划。本项目当前 309 项；实际隐藏/可见原生证据独立记录，不混入普通自动测试数。

