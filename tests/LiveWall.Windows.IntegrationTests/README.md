# LiveWall.Windows.IntegrationTests

覆盖 Windows 平台适配器的纯映射、稳定身份、拓扑刷新和真实桌面会话集成测试。无需真实硬件的测试使用注入快照；需要真实桌面层级的验收只能在交互式 Windows 会话运行，纯探针/适配器单元测试不得标记为真实桌面验收。单/多屏、DPI、HDR、睡眠、锁屏、RDP、Explorer 重启及全屏检测必须在真实交互式 Windows 会话运行，CI 必须显式标记环境能力。

当前自动化测试已覆盖窗口创建/销毁线程一致性、消息循环退出、provisional 首帧前不可见、批量替换与失败回滚、`TaskbarCreated` 信号桥接、Explorer 恢复后重建而非重挂旧 HWND，以及 Shell 快照 build/UBR 身份、完整兼容键和 Shell 不可见状态。窗口访问已抽象为可注入只读探针；合成四层窗口树会实际验证 depth、parent/owner、DefView→SysListView32 和同级 Z-order，而不是依赖空集合上的 vacuous assertion。Raised Desktop 合成测试进一步覆盖错误 desktop、Progman/DefView styles、WorkerW direct-parent/owner/process/bounds/Z-order、稳定性、不含 HWND/PID 的结构指纹、“请求被拒但引入 WorkerW”必须升级为 `CleanupIncomplete`，以及 layered GDI、non-layered GDI 与 DirectComposition swap chain 必须匹配声明的窗口样式，并拒绝残留顶层非客户区样式或可交互应用窗口语义的 Surface。候选稳定性只使用 Progman 子树，不再因无关顶级窗口 Z-order 变化误拒绝。当前 Windows 集成测试为 `42/42`，Debug 与 Release 全解决方案回归均为 `118/118`。

这些测试使用受控窗口、替身和进程内场景，只能证明 `StructuralCandidate`、状态转换和清理契约，不等价于真实桌面呈现验收，也不得把 `StructurallyValidated` 提升为生产 attachment 或 allowlist 条目。Raised GDI 实验已提供“结构成立但不可见”的反例；Host-owned DComp 与 Explorer generation 重建则已通过一次真实诊断验收。仍缺少的关键覆盖是独立 Renderer 进程通过现有 `AttachSurface` 创建自有 child HWND 和 DComp target/交换链，以及同一 Renderer 在新 Shell generation 上重新附着并再次报告首帧。后续测试应分别覆盖结构判定、attachment presentation、Renderer binding、LiveWall 自有资源清理和 Shell mutation recovery；实际像素可见、DPI 误触发、多屏/负坐标/热插拔仍必须在交互式 Windows 会话验证。完成前 Stage B 保持 `Not accepted`。
