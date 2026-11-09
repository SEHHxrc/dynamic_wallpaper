# LiveWall.Renderer.ContractTests

对 Host 和 Fake Renderer 运行同一套协议测试：握手、主/次版本、未知字段、重复 messageId、旧 Generation、超长/截断/畸形帧。

C0 另链接 Video 私有 Mpv 源码及原始 AVI 生成器，仅测试哈希/路径拒绝、输出证据不冒充桌面呈现、确定性运动帧和 AVI 索引。该组不加载真实 DLL、不初始化 GPU 或创建窗口；原协议测试保持不变。真实 libmpv 的无桌面检查与显式人工桌面实验由 `tools/VideoPresentationProbe` 独立记录，不能与自动协议基线混淆。

C1.0 另链接 Video 私有事务核与首帧门禁，新增 32 项测试：精确 Render/Present/Commit/同步结果、完整票据隔离、缺失/旧帧拒绝、同 Renderer 新 generation、Bounds 当前 binding 和各阶段退出。该组使用合成提交证据，不加载 DLL、GPU 或窗口，不代表正式 Video Pipe 客户端或实际桌面呈现通过。真实 D3D11 Render API 预检由工具独立记录，不计入本测试项目数量；原 Renderer Protocol DTO/Schema 保持不变。

C1.1 新增 37 项真实长度帧/Pipe + 受控后端测试：完整 Wire 顺序、暂停恢复新 binding、Initialize 前命令、Load 前 Attach、能力/Envelope/重复 ID、路径与强类型拒绝码、阶段超时、空闲引擎错误、Shutdown 抢占各准备阶段、迟到证明和清理失败禁止正常回执。该组不加载原生 DLL/创建窗口。独立工具的真实正式 Renderer 隐藏 child/DComp 原生结果另记，不能混入普通测试数或据此宣称桌面像素通过。

C1.3 新增 7 项：Pause/Resume/Suspend 的后端即使忽略取消并迟到完成，Shutdown 后也不能回传播放成功；未实现的 Throttle、SetProperties、SetAudioFrame、CapturePreview 明确拒绝。本项目当前 121 项，不加载真实 DLL/窗口；真实媒体时间、画幅/音量读回及 Host 失败收口由 VideoPresentationProbe 的独立隐藏案例记录。

C2.1 新增 1 项：Renderer 接受的视频入口扩展名必须与 Domain 的 `NativeVideoFormats` 完全一致。
