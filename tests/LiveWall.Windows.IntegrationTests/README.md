# LiveWall.Windows.IntegrationTests

覆盖 Windows 平台适配器的纯映射、稳定身份、拓扑刷新和真实桌面会话集成测试。无需真实硬件的测试使用注入快照；需要真实桌面层级的验收只能在交互式 Windows 会话运行，纯探针/适配器单元测试不得标记为真实桌面验收。单/多屏、DPI、HDR、睡眠、锁屏、RDP、Explorer 重启及全屏检测必须在真实交互式 Windows 会话运行，CI 必须显式标记环境能力。

当前自动化测试已覆盖窗口创建/销毁线程一致性、消息循环退出、provisional 首帧前不可见、批量替换与失败回滚、`TaskbarCreated` 信号桥接、Explorer 恢复后重建而非重挂旧 HWND，以及 Shell 快照 build/UBR 身份、完整兼容键和 Shell 不可见状态。窗口访问已抽象为可注入只读探针；合成四层窗口树会实际验证 depth、parent/owner、DefView→SysListView32 和同级 Z-order，而不是依赖空集合上的 vacuous assertion。Raised Desktop 合成测试进一步覆盖错误 desktop、Progman/DefView styles、WorkerW direct-parent/owner/process/bounds/Z-order、稳定性、不含 HWND/PID 的结构指纹、“请求被拒但引入 WorkerW”必须升级为 `CleanupIncomplete`，以及 layered GDI、non-layered GDI 与 DirectComposition swap chain 必须匹配声明的窗口样式。生产形态 Factory 测试另覆盖空 allowlist 失败关闭、内部 lease/无句柄 capability、no-redirection Raised profile、`DefView > LiveWall > WorkerW`、hidden provisional 和跨 generation 不触碰旧 HWND。当前 Windows 集成测试为 `46/46`，Debug 与 Release 全解决方案回归均为 `122/122`。

这些测试使用受控窗口、替身和合成快照，不能替代真实 Shell 可见性。Renderer-child 与 Lease/Factory 设计门禁已经关闭；下一步应验证真实 Adapter → Lease → Native Factory → Renderer v1 全链，并在交互式 Windows 会话执行实际像素、DPI 误触发、多屏/负坐标/热插拔、异常退出和 Shell mutation recovery 矩阵。完成前 Stage B 保持 `Not accepted`。
