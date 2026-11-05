# ADR-010：Raised Shell mutation 的生产门禁

- 状态：Accepted
- 日期：2026-10-24
- 范围：P1.10.0 策略收口；不启用生产适配器，不改变桌面接口或 Renderer 协议

## 背景

Raised `0x052C / 0x0D / 0x01` 请求可能产生 Shell-owned WorkerW。LiveWall 销毁自己的 HWND 与恢复请求前 Shell 状态是不同义务。已有成功呈现、恢复、退出证据只证明各自声明的范围；P1.9.5 关闭不能证明未公开请求可撤销。

## 决定

每次显式诊断保留请求前、请求后、自有资源退出后三个拓扑快照。临时 PID/HWND 只用于同一运行、同一 Shell generation 内比较；持久兼容键只使用完整 Build.UBR、无句柄结构指纹、presentation kind、binding 与验收矩阵版本。

| 基线与退出后事实 | OwnedResourcesCleanup | ShellMutationRecovery |
|---|---|---|
| 请求前已存在合规 WorkerW，退出后同 generation、结构未变 | 按实际自有资源独立判定 | `NotRequired`，不宣称撤销历史请求 |
| 本次新增 WorkerW，退出后新增结构仍在 | 按实际自有资源独立判定 | `CleanupIncomplete` |
| 本次新增结构消失且请求前结构可验证地恢复 | 按实际自有资源独立判定 | `Recovered`，仅限本次样本 |
| generation 改变、采样失败或结构有无法解释的变化 | 按实际自有资源独立判定 | `Inconclusive`，不猜测成功 |

正常退出只能释放 LiveWall 自有资源。不得销毁、换父级或隐藏 Shell-owned WorkerW，不得把 `(0x0D, 0)` 当作未经验证的逆操作，不得静默重启 Explorer、结束第三方进程或改系统壁纸设置。用户另行授权的 Explorer 重启只恢复诊断环境，不能证明原请求有生产可撤销性。

任何 `OwnedResourcesCleanup=Complete` 均不能覆盖 `ShellMutationRecovery=CleanupIncomplete/Inconclusive`。在安全、重复可验证的撤销方式完成验收前，或另一份 ADR 明确接受某种 Shell-owned mutation 的产品后果与兼容边界前，Raised 生产 selector 继续失败关闭，两类生产 allowlist 均为空。

若未来决定容许特定残留，必须明确写为 `AcceptedMutation` 一类独立产品策略，而非 `Recovered`；须包含系统壁纸/图标/任务栏影响、竞争软件、后续启动、Explorer 重建、异常退出、版本回归及灰度回滚证据。该决定需要单独审查，不由诊断通过或本 ADR 自动授权。

## 备选方案

- 销毁全部 WorkerW：拒绝，窗口非 LiveWall 所有。
- 退出时自动重启 Explorer：拒绝，破坏用户桌面且不是请求的逆操作。
- 自有资源清理成功即整体成功：拒绝，丢失 Shell 副作用证据。
- 无限阻塞全部开发：拒绝；隔离 Video Renderer C0/C1 可并行，但不能借此开放生产 attachment。

## 影响、迁移与回退

本次只冻结既有失败关闭边界，不改 `IDesktopHost`、Factory、Renderer Protocol v1、DTO/Schema 或退出预算。既有原始证据和失败分类保持不变；新诊断必须分开报告两种清理语义。后续代码消费现有快照和当前 generation，不创建持久 Shell 句柄注册表。若验证能力不足，回退为 `Inconclusive` 并保留生产禁用，而非放宽窗口规则。

P1.10.0 设计完成；Shell mutation 生产接受门禁尚未关闭，Stage B 仍为 `In progress / Not accepted`。本决定不需要现在运行桌面实验。
