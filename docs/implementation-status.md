# 实现与验收状态

> 截止日期：2026-09-09。本文只记录可变的工程状态，不改变 `ARCHITECTURE.md`、已接受 ADR 或专题设计中的规范边界。

## 状态口径

- **已实现**：代码路径和公开端口已经存在。
- **自动验证通过**：可在测试进程或受控替身中验证实现契约，不等于 Windows Shell 真实兼容。
- **真实桌面验收通过**：已在声明支持的 Windows 完整 build、显示器和 DPI 矩阵中验证图标层级、恢复与清理。
- **未接受**：不得用于生产能力声明，也不得加入 build allowlist。

## Stage B 台账

| 能力 | 实现状态 | 验证状态 | 生产边界 |
|---|---|---|---|
| 专用 STA Window Dispatcher | 已实现 | 自动验证通过 | 仍需真实桌面生命周期矩阵 |
| hidden provisional Surface 与批量 `ReplaceSurfacesAsync` | 已实现 | 自动验证通过；单屏与双屏正式候选均在全部 FirstFrame 后提交，P1.6 双屏基础、长驻、process-cold 和 20 轮连续替换通过 | 局部故障、Explorer 恢复异常和显示拓扑切换仍需矩阵验收 |
| `TaskbarCreated` 主信号、350 ms 防抖、2 秒后备验证 | 已实现 | 自动验证通过 | `TaskbarCreated` 只表示 Shell 拓扑可能变化 |
| Host 处理恢复命令并重建 Surface、重新 Attach Renderer | 正向路径已实现 | 自动验证通过；Host-owned DComp 与独立 Renderer-child 诊断均已在真实 Explorer generation 变化后重新发现并重建 Surface；后者保持同一 Renderer PID并通过第二代 Attach/FirstFrame、10 秒稳定呈现和人工层级验收 | 正向功能恢复通过；显式终止 Explorer 时的短暂黑屏不属于产品级无缝恢复承诺，禁止通过复用旧 HWND 或提前激活 provisional Surface 隐藏 |
| Explorer recovery 失败处理 | 已删除 previous Surface 回附；Host 单写者先失效全部活动会话但保留 Assignment，Renderer 状态不确定时直接终止，旧及 provisional Surface 按 generation-aware abandon 清除记录，再以 350 ms / 1 s / 2 s 退避创建全新 Renderer/Surface，三次失败后熔断并失败关闭 | 自动覆盖 Attach 写入失败、SurfaceAttached 超时、Attached 后 Renderer 失败、FirstFrame 超时、Replace 失败、双屏部分失败、重复恢复信号、旧 HWND 禁止操作、重试预算及用户显式 Apply 解锁 | P1 代码门禁已关闭；真实 Explorer 各阶段故障、并发 Apply/热插拔与 Shell mutation 仍属于后续人工/组合矩阵，不构成 Stage B 接受 |
| `--shell-topology` 诊断 | 已实现执行上下文、顶级窗口与 `GetShellWindow` 后代递归快照 | 当前机器已运行，已采集 session/window station/desktop、UBR、parent/owner、PID/TID、styles、bounds 与同级 Z-order | Shell 不可见时标记 `Inconclusive` 并返回退出码 3；只采集证据，不修改 allowlist |
| 限时 `--color-block` 诊断 | 已实现 | `26200.9168` 交互式 5 秒尝试因无合规附着点在 Surface 创建前失败关闭；前后拓扑一致 | 无合规附着点时退出码 4；真实色块矩阵尚未进入 |
| Legacy WorkerW 生产适配 | 代码存在，Experimental | 完整 build `26200.9168` 当前诊断为 0 个合规候选 | `ValidatedBuilds` 保持为空，生产失败关闭 |
| Raised Desktop 隔离诊断 | 已实现 `--raised-desktop-probe` 与限时 `--raised-desktop-color-block` | `0x0D/0x01` 生成结构合规的 Progman 子 WorkerW；单屏结构验证达到 `DefView > LiveWall > WorkerW`，但修正后的显式 GDI 色块人工验收仍不可见 | P1 / Stage B 关键路径；仅为 `StructurallyValidated`，不是生产 attach point；初次请求后 Shell WorkerW 未自行恢复 |
| 可渲染桌面附着能力 | 能力边界与生产形态 Platform 接线已实现，尚未启用生产能力 | Legacy 无结构候选；无边框 Host-owned Raised DirectComposition、独立 Renderer-child `HwndChild`、物理单屏短时和 P1.6 双屏基础/90 秒长驻/10 次 process-cold/20 轮连续替换均通过真实像素、层级与清理验收 | 诊断证据成立；生产启用数量仍为 `0`，局部故障、双屏 Explorer、热插拔、主屏切换、完整 DPI 与剩余矩阵通过前不得进入 allowlist |
| Renderer-child DirectComposition 隔离探针 | 已实现独立辅助进程、协议 v1 `HwndChild` Attach、Renderer-owned child/DComp/swap chain、事件与清理 | 不同 PID、parent/bounds、`DefView > Host > WorkerW`、`SurfaceAttached → FirstFramePresented`、正常退出、父进程取消和故意崩溃清理自动验证通过；人工确认真实像素完整可见，图标、普通桌面程序和任务栏均未被遮挡 | 已形成单屏 v1 `HwndChild` binding 诊断证据；同 Renderer 跨 Explorer generation 的自动与人工重附着均通过；仍不自动启用生产 Raised attachment |
| Renderer Protocol v1 正式会话 | 已实现 Host 侧进程 Provider、一次性凭据、正式握手、DTO 映射、状态机、单接收泵与事件缓冲 | 自动验证完整顺序、恢复 generation、阶段/握手超时、错误令牌、未知事件和重复 messageId；P1.6 双屏 10/10 次 process-cold 与 20 轮同 Host 连续会话通过 | 不修改 DTO、Schema 或协议版本；历史固定 15 秒样本仍为 `HistoricalUnclassifiedTimeout`，下一门禁是正式双屏局部故障与恢复矩阵 |
| 产品候选阶段可观测性与 deadline | 已实现 generation 终态任务、不可变单调时间线和按实际顺序 Surface 数量推导的统一 deadline；诊断等待 `Succeeded / Failed / Cancelled / CircuitBroken`，不再轮询活动 Session | 自动覆盖成功、FirstFrame deadline、Superseded、Host 取消、恢复熔断以及单/双 Surface 预算；默认单 Surface 为 52 秒、双 Surface 为 82 秒。P1.6 的双屏 90 秒、10 次 process-cold 与 20 轮连续替换均输出逐 Surface 时间线和独立 retirement 结果 | 不记录认证令牌，不将 HWND 作为持久身份；此前固定 15 秒样本保留为历史 `UnclassifiedTimeout`，后续故障必须归因到强类型 phase/reason |
| Apply deadline 与活动 Session 生命周期 | P1.5.1 已实现：Apply deadline 使用独立 lease，generation 进入任一终态即释放；活动 `PreparedSession` 不再保存 operation。正常替换、显式 Shutdown 和 Host Dispose 使用独立的逐 Renderer retirement policy/result，Surface 最终销毁使用不可取消路径；Host 保存有界 retirement journal | 自动覆盖成功后等待原 Apply deadline 过期再替换或退出，仍发送 Shutdown 并等待 `ShutdownCompleted`；覆盖不响应 Shutdown 时记录 `ShutdownTimeout` 后强制 Dispose、双 Session 独立预算与批次聚合、普通替换/Host Dispose 结果入 journal、32 条 generation journal 裁剪及 Host Dispose 清空。退出 Lively 后在无外接显示器的物理单屏环境补测通过：首帧 1.233 秒、提交 1.257 秒、独立退休 108.6 ms，ShutdownCompleted/Dispose/Surface cleanup 全部成功且无新增 WorkerW；人工确认主桌面橙色正常显示约 1 秒且图标/任务栏层级正确 | Apply journal 与长期 Session retirement 已解耦；P1.5.1 代码、交互式自动与物理单屏人工门禁关闭。非优雅 Shutdown 仍不会伪装为成功；双屏、热插拔和真实长时产品运行保留在后续矩阵 |
| P1.6 产品候选可靠性驱动 | 已实现 `--result-json` schema 1.0、Release process-cold 批次脚本、产品候选专用 `--soak-seconds`，以及支持 `primary/all/display-id` 的单 Host 串行 `--raised-desktop-product-candidate-loop`；循环逐 generation 等待 Apply 终态和上一批全部 Session retirement | Debug/Release 共 161/161 通过。`WinSta0\Default` 双屏 90 秒 soak、10/10 次 process-cold 和同一 Host 20 轮循环均通过。20 轮每代两个 FirstFrame、一次原子 Replace，共 40 个 Renderer 全部 graceful retirement（38 Replacement + 2 Shutdown），ShutdownCompleted/Dispose/Surface cleanup 全部成功，无自有 HWND/新增 WorkerW；用户确认两屏全程同步橙黄交替、无黑屏或覆盖并正常恢复 | P1.6 代码、自动与当前双屏人工门禁关闭；历史固定 15 秒失败继续保留。该结论不覆盖 Explorer、局部故障注入、热插拔、主屏切换、会话切换或 Shell mutation recovery，仍不是 Stage B 接受或生产 allowlist 依据 |
| 双屏产品候选选择与原子提交 | 已实现 `--target-display primary / all / <display-id>`，默认保持 primary；候选结果和 JSON 改为逐 Session/显示器，批次 retirement 改为逐 Renderer 受控并行 | 自动验证 primary/all/精确 ID 选择、未知 ID 拒绝、两屏两个 FirstFrame 后仅一次 Replace、任一屏 FirstFrame 失败时零 Replace 且整批清理，以及并行 Shutdown/独立 retirement 结果。`26200.9168 / session=7 / WinSta0\Default` 已完成 5 秒、90 秒、10 次 process-cold 和 20 轮连续替换；40/40 Renderer 全部优雅退休 | P1.6 双屏 PerDisplay、原子提交与当前可靠性切片通过；尚不覆盖正式进程局部故障、Explorer generation 恢复、热插拔和主屏切换，仍不得加入生产 allowlist |
| P1.7 双屏故障与恢复矩阵 | 待实现诊断级、按 generation/Surface/phase 定点的正式 Renderer 故障计划；现有 fake 测试已覆盖部分 FirstFrame、Replace、恢复和 retirement 异常。Apply 已有可等待终态，但 Explorer 正向恢复当前没有独立 recovery terminal/journal | 应依次验证：首次 Apply 的第二 Surface 在 Attach/ContentLoaded/FirstFrame 失败时零提交；新 Apply 局部失败时旧双屏保持；Replace 后旧 Renderer 退休超时不回滚新双屏；补齐 recovery 终态后再验证双屏 Explorer generation 正向恢复及 Attach/FirstFrame 局部失败后的整批重建 | 故障开关只允许进入 `RendererChildProbe`/诊断 composition root，不进入 Renderer Protocol DTO、Application 公共端口或生产配置；恢复终态使用 `Succeeded / Failed / Cancelled / CircuitBroken`，成功模式另分 `Reattached / Rebuilt`，不得重新依赖 `Sessions.Count` 轮询；热插拔和主屏切换另属 P1.8 |
| Platform.Windows attachment lease | 已实现 | 自动验证 Platform 内部 parent/anchor/backdrop/generation、无句柄能力描述及跨 generation 旧 HWND 不再被显隐/复用 | Shell HWND/PID 不进入 Application、Renderer、持久化或 allowlist；每个新 generation 必须重新发现 |
| Raised Desktop Surface Factory | 已实现生产形态接线 | 受控 HWND 测试验证 hidden provisional、`WS_EX_NOREDIRECTIONBITMAP`、`DefView > LiveWall > WorkerW`、显式批量激活和清理 | 只消费当前 generation 的内部 lease；不等于当前 build 已获生产支持 |
| Raised Desktop 生产适配 | 已实现发现和 Factory 调用路径，但 allowlist 关闭 | 自动结构/Factory 契约通过；尚未进行允许生产选择后的完整真实矩阵 | `ValidatedBuilds` 为空，生产 Selector 跳过；不得由诊断成功自动写入 allowlist |
| 产品入口与完整运行链路 | 已增加显式单次、soak、process-cold 批次和同 Host 循环入口，均复用 HostCommandLoop、SessionCoordinator、生产 Surface Factory 与正式 RendererProtocolSession | 物理单屏短时以及 `session=7 / WinSta0\Default / 26200.9168` 双屏 P1.6 基础、长驻、冷启动和连续替换通过 | 当前入口仍只用于显式诊断；故障、Explorer 和显示拓扑重规划矩阵完成前不改变 allowlist 或 Stage B 状态；首次历史样本不得事后定性 |

因此 Stage B 的总体状态仍为 **`In progress / Not accepted`**。已经完成的机制不应再描述为“尚未实现”，但自动测试结果也不能替代真实桌面兼容性结论。

## 当前验证基线

截至 2026-09-09 的最近一次完整验证结果：

| 验证项 | 结果 |
|---|---|
| Debug：架构边界、还原、构建、5 个测试工程 | 通过；`0` 警告 / `0` 错误，`160/160` 测试通过 |
| Release：架构边界、还原、构建、5 个测试工程 | 通过；`0` 警告 / `0` 错误，`160/160` 测试通过 |
| Visual Studio 18.9 原生测试平台：Domain | `12/12` |
| `git diff --check` | 通过 |
| 当前 Windows 完整 build | `26200.9168`（Windows 11 25H2） |
| `--shell-topology` 的既定 Legacy WorkerW 候选 | `0` |
| 干净交互式基线 | `session=2 / WinSta0 / Default`；Explorer 重启后连续 3 次均为 `Progman → DefView → ListView → Header` |
| Explorer 重建 | PID `19776 → 35652`，Shell 核心 HWND 全部重建 |
| 5 秒 `--color-block` | 无合规附着点，Surface 创建前失败关闭，前后拓扑一致；未进入视觉矩阵 |
| Raised Desktop 请求 | `0x052C / 0x0D / 0x01` 唯一新增全屏、同 Explorer、无 owner 的 Progman 直接子 WorkerW；初次请求退出码 6（`CleanupIncomplete`） |
| Raised Desktop 5 秒色块 | 自动结构验证通过：`DefView(z0) > LiveWall(z1) > WorkerW(z2)`；显式 GDI 填充、刷新并等待 DWM 提交后人工观察仍完全不可见；状态只能是 `StructurallyValidated` |
| Raised Desktop non-layered GDI | 已实现并完成一次 5 秒 `PresentationProbeCompleted`；窗口明确不带 `WS_EX_LAYERED`，owned-resource cleanup 通过且本次未观察到新增 Shell mutation | 人工观察仍完全没有桌面变化，判定 `PresentationFailed`，不能产生 `RenderableDesktopAttachment` |
| Raised Desktop DirectComposition swap chain | 已实现并完成两次 5 秒 `PresentationProbeCompleted`；D3D11 composition swap chain 绑定 LiveWall 自有 HWND，活动结构为 `DefView > LiveWall > WorkerW`，owned-resource cleanup 通过 | 首次实验因残留 `WS_CAPTION` 导致上、左未覆盖；直接创建无边框 Progman 子窗口后，第二次已人工确认完整覆盖，桌面图标和任务栏符合预期 |
| Raised Desktop Explorer 重建诊断 | 已实现双重显式确认入口；仅终止当前 Progman 所属 Explorer PID | 自动确认 Shell PID/核心 HWND generation 全部变化，在新 Progman 上重新发现结构并创建全新 DComp Surface；`generationChanged=True`、`rebuilt=True`、owned-resource cleanup 通过 | 人工观察到“旧洋红短闪 → Shell 黑屏重建 → 新黄色完整显示 → 自动还原”，任务栏恢复且未被遮挡；不等同于产品 Host/Renderer 恢复验收 |
| Renderer-child DirectComposition 探针 | 已实现独立 `RendererChildProbe` 进程；Host 仅发送现有 `AttachSurfacePayload` 容器 HWND | 既有独立 PID、parent/bounds、Z-order、正常/取消/崩溃与人工呈现证据保持有效；测试 Renderer 已补齐完整正式会话顺序并由产品候选链实际调用 | `HwndChild` binding、跨 generation 重附着及主屏产品候选证据成立；仍不自动启用生产 Raised attachment |
| Renderer-child Explorer 重附着 | 已实现双重确认诊断入口；辅助进程接受严格递增 generation 的多次 Attach | 完全退出 Lively 后的干净重测通过：Explorer PID `34796 → 7192`，Renderer PID 保持 `6516`；旧/新 Host `0x303A2 → 0x70194`、child `0x4007A → 0x40236` 均不复用；第二代 Attached/FirstFrame 后完成 10 秒单调计时和每秒 Z-order 复验，正常 Shutdown、自有资源清理通过 | 人工确认“橙色约 2 秒 → 黑屏 → Explorer/任务栏恢复 → 黄色完整约 10 秒”，黄色未遮挡图标和任务栏；恢复门禁通过。短暂黑屏是显式 Shell 重启过程，不等同于产品无缝恢复；竞争程序场景继续失败关闭 |
| Raised Desktop 产品接线 | Platform 内部 Lease、Adapter Selector、Host 和 Native Surface Factory 已贯通 | 自动测试覆盖能力描述、Raised profile/Z-order、生产 allowlist 关闭和跨 generation 旧 HWND 不再被触碰；物理单屏短时及 P1.6 双屏完整候选链通过 | 当前双屏基础可靠性通过；正式局部故障、双屏 Explorer、完整 DPI/热插拔/主屏切换矩阵尚未执行，仍不是 Stage B 接受 |
| Raised Desktop 清理 | 诊断结果已拆分为 `OwnedResourcesCleanup` 与 `ShellMutationRecovery`；LiveWall 自有 HWND 已销毁，但初次 Raised 请求留下的 Shell WorkerW 仍存在，整体 shell mutation recovery 为 `CleanupIncomplete` |
| `26200.9168` 可渲染桌面附着能力 | DirectComposition 诊断及正式产品候选的主屏单 Surface 证据为 `1`；生产启用数量为 `0`，Legacy 与 Raised Desktop allowlist 均为空 |

自动验证证明代码和契约基线稳定；干净交互式采样确认 `26200.9168` 没有合规 Legacy WorkerW。Raised 的 layered/non-layered GDI 路径保留为“结构不等于呈现”的失败反例，无边框 Host-owned DComp、Renderer-child v1 `HwndChild`、Explorer generation 诊断重建和物理单屏短时产品候选已经通过。2026-09-10，P1.6 又在 `session=7 / WinSta0\Default` 的 200% 主屏与 150% 负坐标外接屏完成 5 秒基础、90 秒长驻、10/10 次 process-cold 和同一 Host 20 轮连续替换；20 代均为两个 FirstFrame 后一次 Replace，40/40 Renderer 全部优雅退休，双屏同步、层级、清理和 Shell mutation 检查通过。因此冷启动、当前双屏长驻和连续替换切片已经关闭；历史固定 15 秒超时仍不可事后归因。正式局部故障、双屏 Explorer、热插拔、主屏切换、完整 DPI 和 Raised 请求可撤销性仍未关闭，不能进入生产 allowlist。基础 build 26200 不是可单独使用的永久兼容键，同一基础 build 会随累积更新获得不同 UBR。

## 下一步兼容探索边界

下一步可行，但必须留在显式诊断模式，且按以下顺序推进：

1. Shell 执行上下文、递归拓扑、完整 build/UBR、结构指纹和干净基线采样已完成；裸 HWND 仍只属于单次快照。Legacy 当前保持 0 个合规附着点。
2. Raised layered/non-layered GDI 已记录为失败反例；无边框 Host-owned DComp 已通过单屏真实像素、完整覆盖、图标层级、任务栏和自有资源清理验收。
3. Explorer generation 变化后的重新发现和 Host-owned DComp Surface 重建已通过自动与人工验收；旧 Shell HWND 没有复用。
4. 隔离 Renderer-child DComp 探针已严格复用协议 v1 的容器 HWND 语义，并通过独立 PID、child parent/bounds、首帧事件顺序、正常退出、父进程取消与故意崩溃清理的自动及人工验证；完全退出竞争程序后，同一 Renderer 进程在新 Shell generation 的第二次 Attach/FirstFrame、10 秒每秒层级复验及人工恢复序列也已通过。
5. Renderer Protocol v1 `HwndChild` 已冻结为当前 binding；Platform 内部 attachment lease 与 Raised Surface Factory 已接线，Application/Renderer 只看到无 Shell 句柄的能力描述和 LiveWall 容器 HWND。只有后续证据证明必须跨进程共享 GPU 资源时，才设计协议 1.1 的资源所有权、handle/fence、Schema 和契约。
6. 正式 Renderer Protocol v1 会话已实现并自动验证：复用现有 Named Pipe/Envelope/一次性令牌，严格执行 `Hello → Initialize → Initialized → AttachSurface → SurfaceAttached → LoadWallpaper → ContentLoaded → FirstFramePresented`，且未修改 DTO、协议版本或 JSON Schema。
7. 显式 `--raised-desktop-product-candidate` 已接通实际 Host command loop / SessionCoordinator → Adapter → Lease → Native Factory → Renderer v1 候选全链；generation 终态、单调时间线和按 Surface 数量推导的统一 deadline 已接线。Apply 时间线覆盖准备到 `SurfacesReplaced`，提交前失败时补记 `ShutdownCompleted / CleanupVerified`；成功激活后的正常退休改由独立 Session retirement result 输出，不再延长或复用 Apply deadline。
8. recovery 失败分支的 generation-safe abandon、全新会话重建、三次退避预算与失败关闭已经实现并自动验证；下一步在真实 Explorer 中覆盖 Attach/FirstFrame/Replace 故障、恢复期间再次触发、并发 Apply 和清理退出，不得让 Host 状态继续把旧 Surface 当作有效活动桌面。
9. P1.5.1 生命周期边界已关闭：Apply deadline 只覆盖准备、首帧、Replace 与提交前失败回收；成功激活后不再约束长期 Session。正常替换/退出使用独立 Renderer Shutdown/Dispose budget 和不可取消 Surface 最终清理；长驻、超时、双 Session 聚合与 operation 释放已有自动测试。
10. P1.6 驱动、双屏 90 秒 soak、10/10 次 process-cold 和同一 Host 20 轮串行 Apply/Replace/retirement 均已完成。历史固定 15 秒失败继续保留为 `HistoricalUnclassifiedTimeout`；当前结果关闭本切片，但不得被解释为后续异常矩阵或生产兼容性已经通过。
11. 显式候选入口和双屏 PerDisplay 已通过 5 秒基础、90 秒长驻、10 次 process-cold 和 20 轮同 Host 连续替换；两个独立 Session/Surface 全部首帧后仅一次批量 Replace，不同 DPI、负坐标和两组资源清理均已覆盖。下一步补齐局部故障、Explorer generation、热插拔与主屏切换，状态不得因 P1.6 通过而提前升级为生产支持。
12. P1.7-A/B：在诊断 composition root 与 `RendererChildProbe` 增加按 generation、目标 Surface 和阶段定点的故障计划；优先让第二 Surface 分别在 Attach、ContentLoaded、FirstFrame 失败，验证首次 Apply 零提交，以及已有双屏时新 generation 失败仍保持旧双屏、清理全部 provisional 资源。故障控制不得进入 Renderer Protocol DTO 或生产配置。
13. P1.7-C：让旧 generation 的某一 Renderer 在新双屏已经 Replace 后不返回 `ShutdownCompleted`，验证新双屏不回滚，退休结果明确记录 `ShutdownTimeout`，随后仍 Dispose Renderer、清理旧 Surface，并允许最终新会话正常 Shutdown。
14. P1.7-D：先增加 Host 级可等待的 recovery terminal/journal，记录 generation、逐 Surface Attach/FirstFrame、单次 Replace、abandon、重建 attempt，以及 `Succeeded / Failed / Cancelled / CircuitBroken` 终态；成功时另记 `Reattached / Rebuilt` completion mode，不得重新用 `Sessions.Count` 推断成功。随后完成双屏真实 Explorer generation 正向恢复，并覆盖恢复 Attach/FirstFrame 局部失败后的整批 abandon、全新 Renderer/Surface 限次重建和熔断。恢复路径不发送 Load/ContentLoaded，因此不得把 ContentLoaded 列为 recovery 阶段；显式 Explorer 终止仍需用户双重确认。
15. P1.8 单独设计显示拓扑重规划：当前 `DisplayTopologyChangedCommand` 只更新快照，不会重建 Session。必须先冻结新增/移除显示器、主屏切换、坐标/DPI 变化时的 Assignment 意图、活跃映射、原子提交、stale topology 取消和失败回退，再执行热插拔验收。
16. 随后扩展 Span、完整 DPI、`TaskbarCreated` 误触发、会话切换和恢复延迟矩阵。能力判断继续使用“运行时结构与 binding 验证 + 完整 build revision/UBR allowlist”；矩阵和 Shell mutation 策略完成前不修改 Legacy 规则、不写入生产 allowlist，Raised 生产 Selector 继续失败关闭。

该路径在技术上可行，已确认 `Progman` 子窗口 Z-order、无边框 Host-owned DirectComposition 内容提交、Renderer-child `HwndChild` binding、诊断级 Explorer generation 重建、物理单屏短时候选以及 P1.6 双屏长驻/process-cold/连续替换成立；当前仍需完成正式局部故障、双屏 Explorer、显示拓扑重规划及剩余显示/异常矩阵。Windows 未提供公开的动态壁纸宿主契约，因此探索结果只能形成版本化、可撤销的兼容适配，不能升级为平台保证。

## Stage B 接受条件

至少在拟支持的每个完整 Windows build revision 上完成：

- 单屏/多屏、负坐标、100–200% DPI、显示器热插拔；
- Surface 位于桌面图标下方，真实测试像素进入当前桌面的可见合成路径，且不遮挡任务栏或普通应用；
- 生产 attachment 记录 adapter、完整 build/UBR、结构指纹、Shell generation、presentation kind 和通过的矩阵；结构候选本身不算 attachment；
- provisional/active/retired 生命周期、失败回滚和异常退出清理；
- Explorer 重启后重建 Surface 并重新附着 Renderer，不复用旧 HWND；
- 独立 Renderer 进程在 Host Surface 内创建自有 child HWND，并以选定内部后端产生可见首帧；若使用协议 v1，DComp target/交换链及其资源必须完全归 Renderer 所有；
- 正式 Renderer 会话遵守 v1 握手、初始化、附着、加载和首帧顺序，并对超时、乱序、重复消息、取消及崩溃失败关闭；
- LiveWall 自有资源清理与 Shell mutation recovery 分别通过；
- 诊断证据、适配器规则、allowlist 条目和回退行为可追溯。

全部通过后才能更新本文、专题文档和 Stage B 状态；只完成代码或自动测试不得提前标记接受。
