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
| Raised Desktop build allowlist | 启用该适配器前 | 诊断记录 Shell build、窗口层级指纹，并通过色块/Explorer/DPI/热插拔矩阵 |
