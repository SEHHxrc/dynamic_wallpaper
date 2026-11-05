# Protocol

校验 Renderer Envelope，将白名单命令映射为 Mpv/Windowing 操作，并把引擎事件映射回 DTO。旧 Generation 命令必须丢弃。

C1.0 增加 `VideoSessionTransaction` 和 `VideoFirstFrameGate`，C1.1 已接正式 `VideoRendererSession` 和 Program。Initialize、Attached、ContentLoaded 的完成动作与 Initial/Bounds/Reattach 票据分离；票据关联 session、generation、binding epoch、content revision、尺寸和操作序号。只接受当前目标真实 Render/Present/Commit 精确成功；内部 FlushResult 当前表示 WaitForCommitCompletion 结果，不是物理扫描/DwmFlush。FrameSequence 是目标提交序列，暂停时可重新提交当前帧。停机后的迟到结果不得放行。

Bounds 的旧已呈现帧变换遵守既有 v1：仍确认当前 Renderer generation，不因为 Host 发来较新 geometry generation 就提前推进 Attach generation。仅同一 binding 的 resident frame 可走 Commit-only；新目标/新 generation 必须重新 Render/Present，不能借此复用旧 HWND。首次/重附着的 Commit/同步缺失均拒绝。

32 项事务门禁与新增 37 项 Wire 会话门禁分别覆盖票据/HRESULT、错误阶段、暂停重附着、超时、迟到结果、各阶段 Shutdown 和清理失败。单接收泵验证 envelope、session、重复 messageId 后进入 32 项有界队列，只有控制 actor 写状态与事件；有效 Shutdown 取消当前准备，回收后才回执。普通 client 健康检查也由 actor 执行，空闲媒体错误不会无限等待下一条命令。

通用帧/本地 Pipe 代码从 Infrastructure 源码链接，仍只引用 Contracts 产品项目；1 MiB 限制、CurrentUserOnly、Identification 与远程拒绝保持一致。正式会话 JSON 使用严格数值类型与大小写，generation/版本等整数不能由字符串强转，未知附加字段仍可忽略。每连接最多 65536 个不同命令 ID，达到上限明确失败关闭，不淘汰旧 ID 后允许重放。Initialize/执行阶段预算为 10 秒，人工操作等待不在本组件。native 调用不合作时不能由 CTS 强制打断，最终由 Host 进程预算结束自有 Renderer。

只接受已验证本地内容根内的 Video 入口，拒绝绝对 entry、URL、ADS、父路径、歧义段、未知扩展与 reparse traversal；不解析外部包。C1.1 只接受空 properties，暂不宣称完整属性、Throttle 或遥测支持。实际隐藏 child/DComp 原生验证独立记录，不替代桌面人工验收。

C1.3 新增 7 项 Wire 门禁：Pause/Resume/Suspend 后端忽略取消并迟到返回时，不得在 Shutdown 后发送成功；未实现的 Throttle、SetProperties、SetAudioFrame、CapturePreview 明确返回 message_unsupported。Fit/Volume 合法值实际读回后才完成，无新增命令或伪成功事件。支持范围见模块根 README；只读原生观测位于 Mpv 私有诊断，不扩展 Envelope、DTO、Schema 或协议版本。

