# LiveWall.Windows.IntegrationTests

覆盖 Windows 平台适配器的纯映射、稳定身份、拓扑刷新和真实桌面会话集成测试。无需真实硬件的测试使用注入快照；需要真实桌面层级的验收只能在交互式 Windows 会话运行，纯探针/适配器单元测试不得标记为真实桌面验收。单/多屏、DPI、HDR、睡眠、锁屏、RDP、Explorer 重启及全屏检测必须在真实交互式 Windows 会话运行，CI 必须显式标记环境能力。

Stage B 必须增加：窗口创建/销毁线程一致性、消息循环退出、provisional 首帧前不可见、批量替换失败回滚、`TaskbarCreated` 的 DPI 误触发、Explorer 重启后重建而非重挂旧 HWND，以及色块窗口位于桌面图标下方的真实视觉/层级验证。完成前 Stage B 保持 `Not accepted`。
