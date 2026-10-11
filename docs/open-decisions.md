# 尚未冻结的决策

以下事项不影响当前模块边界，但在对应实现进入主分支前必须明确。开发者不得把临时选择写成事实标准。

首版支持范围已于 2026-10-11 调整：RDP 动态壁纸呈现为 `OutOfScope`，RDP 实机矩阵为 `WaivedForV1`，不得再作为下表系统会话矩阵的首版阻塞项，也不得写成测试通过。通用会话状态机、远程会话检测/暂停和失败关闭保留；本地快速用户切换为 `NotValidated`，有现成安全环境时补测。P1.9.2 单屏代表场景已通过，双屏系统恢复独立待补；P1.9.3.0 代码/自动门禁和 P1.9.3.1 诊断入口已完成，当前开发转向 P1.9.4 并发恢复。生产内容入口、P1.9.4/P1.9.5 和 Shell mutation 策略仍为接受门禁，两类 allowlist 继续为空。

| 决策 | 最迟阶段 | 完成条件 |
|---|---|---|
| 开源/商业许可证 | 首次对外发布前 | 根目录加入正式 `LICENSE`，并与依赖许可证兼容 |
| Windows App SDK 精确版本 | UI 实现开始前 | 选择当时 Stable，记录最低 OS、部署方式和回退 |
| WebView2 SDK 精确版本 | Web Renderer 开始前 | 选择 Evergreen 兼容版本并完成生命周期验证 |
| libmpv/FFmpeg 发行构建 | Video Renderer 发布前 | 固定来源、编解码能力、哈希和第三方声明 |
| JSON 日志组件与轮转参数 | Host 日志实现前 | 完成依赖审计、磁盘上限和脱敏测试 |
| `CapturePreview` 返回事件 | 命令实现前 | 协议 minor 升级，新增强类型结果事件与 Schema |
| Raised Desktop / Legacy WorkerW build allowlist | 启用对应适配器前（P1 / Stage B 关键路径） | 在与 Explorer 相同的交互式 desktop 中记录完整 build revision/UBR、窗口层级指纹、attachment presentation 和 Renderer binding，运行时重新验证结构与呈现，并通过正式 Renderer v1 会话及实际产品候选全链、DPI/拓扑、异常退出、系统会话与 Shell mutation 矩阵；不得只使用 `CurrentBuildNumber`。Legacy 在现有参数下无合规候选；Raised 的单/双屏 DComp、Renderer-child、P1.6、P1.7 和 P1.8 已按既定实机/条件性边界关闭，包括 Span 代表矩阵。pure bounds-only 与物理零显示器明确为 `EnvironmentInconclusive`，未伪写实机通过；P1.9.0 生命周期与 P1.9.1 正式事件链已关闭，P1.9.2 系统状态代码/自动门禁完成但实机待验收。生产内容仓库/Renderer、P1.9.3–P1.9.5、P1.9.2 实机及 Shell mutation 尚未闭环，因此两类生产 allowlist 均保持为空 |
