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
| Raised Desktop / Legacy WorkerW build allowlist | 启用对应适配器前（P1 / Stage B 关键路径） | 在与 Explorer 相同的交互式 desktop 中记录完整 build revision/UBR、窗口层级指纹、attachment presentation 和 Renderer binding，运行时重新验证结构与呈现，并通过正式 Renderer v1 会话及实际 Host command loop / SessionCoordinator → Adapter → Lease → Native Factory → Renderer 全链、DPI、热插拔、多屏、异常退出与 Shell mutation 矩阵；不得只使用 `CurrentBuildNumber`。`26200.9168` 在现有 Legacy 参数下无合规候选；Raised 的单屏/双屏 DComp、Renderer-child `HwndChild`、正式 v1 会话、generation 恢复、P1.5/P1.5.1 与 P1.6 长驻/process-cold/连续替换已经通过相应自动或人工验收。正式局部故障、双屏 Explorer、热插拔、主屏切换、完整 DPI 和剩余生产矩阵尚未闭环；两类生产 allowlist 均保持为空 |
