# 尚未冻结的决策

以下事项不影响当前模块边界，但在对应实现进入主分支前必须明确。开发者不得把临时选择写成事实标准。

| 决策 | 最迟阶段 | 完成条件 |
|---|---|---|
| 开源/商业许可证 | 首次对外发布前 | 根目录加入正式 `LICENSE`，并与依赖许可证兼容 |
| Windows App SDK 精确版本 | UI 实现开始前 | 选择当时 Stable，记录最低 OS、部署方式和回退 |
| WebView2 SDK 精确版本 | Web Renderer 开始前 | 选择 Evergreen 兼容版本并完成生命周期验证 |
| libmpv/FFmpeg 发行构建 | Video Renderer 发布前 | 固定来源、编解码能力、哈希和第三方声明 |
| JSON 日志组件与轮转参数 | Host 日志实现前 | 完成依赖审计、磁盘上限和脱敏测试 |
| `CapturePreview` 返回事件 | 命令实现前 | 协议 minor 升级，新增强类型结果事件与 Schema |
| Raised Desktop / Legacy WorkerW build allowlist | 启用对应适配器前（P1 / Stage B 关键路径） | 在与 Explorer 相同的交互式 desktop 中记录完整 build revision/UBR、窗口层级指纹、attachment presentation 和 Renderer binding，运行时重新验证结构与呈现，并通过 Renderer-child、Explorer、DPI、热插拔和多屏矩阵；不得只使用 `CurrentBuildNumber`。`26200.9168` 在现有 Legacy 参数下无合规候选；无边框 Host-owned Raised DComp 已通过单屏和 Explorer generation 重建诊断验收，但生产 binding 与剩余矩阵未闭环。两类生产 allowlist 均保持为空 |
| Raised Desktop Renderer binding | 启用 Raised Adapter 或开始 Stage C Renderer 集成前 | 先实现独立 Renderer-child DirectComposition 探针：Host 只传容器 HWND，Renderer 创建自有 child HWND、DComp target 和交换链。若可见，冻结为协议 v1 `HwndChild`，不新增协议类型；只有不可见且证据证明 Host 必须接收/合成共享纹理、交换链句柄或 fence 时，才设计强类型 `SurfaceBinding`、升级协议 minor 并补齐资源所有权、Schema 和契约测试 |
