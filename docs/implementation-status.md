# 实现与验收状态

> 截止日期：2026-09-06。本文只记录可变的工程状态，不改变 `ARCHITECTURE.md`、已接受 ADR 或专题设计中的规范边界。

## 状态口径

- **已实现**：代码路径和公开端口已经存在。
- **自动验证通过**：可在测试进程或受控替身中验证实现契约，不等于 Windows Shell 真实兼容。
- **真实桌面验收通过**：已在声明支持的 Windows 完整 build、显示器和 DPI 矩阵中验证图标层级、恢复与清理。
- **未接受**：不得用于生产能力声明，也不得加入 build allowlist。

## Stage B 台账

| 能力 | 实现状态 | 验证状态 | 生产边界 |
|---|---|---|---|
| 专用 STA Window Dispatcher | 已实现 | 自动验证通过 | 仍需真实桌面生命周期矩阵 |
| hidden provisional Surface 与批量 `ReplaceSurfacesAsync` | 已实现 | 自动验证通过 | 仍需真实首帧/层级视觉验收 |
| `TaskbarCreated` 主信号、350 ms 防抖、2 秒后备验证 | 已实现 | 自动验证通过 | `TaskbarCreated` 只表示 Shell 拓扑可能变化 |
| Host 处理恢复命令并重建 Surface、重新 Attach Renderer | 已实现 | 自动验证通过；独立 DComp 诊断已在真实 Explorer generation 变化后重新发现并重建 Surface，第二色块人工验收通过 | 产品 Host/Renderer 接线的真实恢复仍待验收 |
| `--shell-topology` 诊断 | 已实现执行上下文、顶级窗口与 `GetShellWindow` 后代递归快照 | 当前机器已运行，已采集 session/window station/desktop、UBR、parent/owner、PID/TID、styles、bounds 与同级 Z-order | Shell 不可见时标记 `Inconclusive` 并返回退出码 3；只采集证据，不修改 allowlist |
| 限时 `--color-block` 诊断 | 已实现 | `26200.9168` 交互式 5 秒尝试因无合规附着点在 Surface 创建前失败关闭；前后拓扑一致 | 无合规附着点时退出码 4；真实色块矩阵尚未进入 |
| Legacy WorkerW 生产适配 | 代码存在，Experimental | 完整 build `26200.9168` 当前诊断为 0 个合规候选 | `ValidatedBuilds` 保持为空，生产失败关闭 |
| Raised Desktop 隔离诊断 | 已实现 `--raised-desktop-probe` 与限时 `--raised-desktop-color-block` | `0x0D/0x01` 生成结构合规的 Progman 子 WorkerW；单屏结构验证达到 `DefView > LiveWall > WorkerW`，但修正后的显式 GDI 色块人工验收仍不可见 | P1 / Stage B 关键路径；仅为 `StructurallyValidated`，不是生产 attach point；初次请求后 Shell WorkerW 未自行恢复 |
| 可渲染桌面附着能力 | 能力边界已定义，尚无生产实现 | Legacy 无结构候选；无边框 Host-owned Raised DirectComposition 已通过单屏完整覆盖、图标层级、任务栏和 Explorer generation 重建人工验收 | 诊断证据数量为 `1`；生产启用数量仍为 `0`，Renderer binding 与剩余矩阵通过前不得进入 allowlist |
| Raised Desktop 生产适配 | 占位并主动失败 | 未验收 | 禁用；不得由诊断成功自动写入 allowlist |
| 产品入口与完整运行链路 | 占位 | 未验收 | 尚不是可发布产品 |

因此 Stage B 的总体状态仍为 **`In progress / Not accepted`**。已经完成的机制不应再描述为“尚未实现”，但自动测试结果也不能替代真实桌面兼容性结论。

## 当前验证基线

2026-09-06 的验证结果：

| 验证项 | 结果 |
|---|---|
| Debug：架构边界、还原、构建、5 个测试工程 | 通过；`0` 警告 / `0` 错误，`118/118` 测试通过 |
| Release：架构边界、还原、构建、5 个测试工程 | 通过；`0` 警告 / `0` 错误，`118/118` 测试通过 |
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
| Renderer-child DirectComposition 探针 | 尚未实现 | Host-owned DComp 成功不能证明独立 Renderer child HWND 路径可见 | 下一项 P1 门禁；通过则保留协议 v1，失败且确认需要跨进程 GPU 资源时才设计协议 1.1 |
| Raised Desktop Surface Factory | 尚未实现 | 当前 Factory 只接收单 parent HWND，并以 `HWND_BOTTOM` 创建/激活通用 child | Renderer-child 探针通过后接入 Platform 内部 attachment lease；完成前 Raised Adapter 继续主动失败 |
| Raised Desktop 清理 | 诊断结果已拆分为 `OwnedResourcesCleanup` 与 `ShellMutationRecovery`；LiveWall 自有 HWND 已销毁，但初次 Raised 请求留下的 Shell WorkerW 仍存在，整体 shell mutation recovery 为 `CleanupIncomplete` |
| `26200.9168` 可渲染桌面附着能力 | 单屏 DirectComposition 诊断证据为 `1`；生产启用数量为 `0`，Legacy 与 Raised Desktop allowlist 均为空 |

自动验证证明代码和契约基线稳定；交互式采样证明第三方 `mpv` 已退出，并识别出 Explorer 重启前残留、重启后消失的 Progman 子 WorkerW。重启后的干净基线没有合规 Legacy WorkerW。Raised 的 layered/non-layered GDI 路径证明结构候选本身不能解释为可见附着；无边框 Host-owned DComp 则形成了 1 条单屏呈现及 Explorer generation 重建诊断证据。该证据尚未覆盖独立 Renderer binding、完整矩阵和 Raised 请求可撤销性，因此不能进入生产 allowlist。基础 build 26200 也不是可单独使用的永久兼容键，同一基础 build 会随累积更新获得不同 UBR。

## 下一步兼容探索边界

下一步可行，但必须留在显式诊断模式，且按以下顺序推进：

1. Shell 执行上下文、递归拓扑、完整 build/UBR、结构指纹和干净基线采样已完成；裸 HWND 仍只属于单次快照。Legacy 当前保持 0 个合规附着点。
2. Raised layered/non-layered GDI 已记录为失败反例；无边框 Host-owned DComp 已通过单屏真实像素、完整覆盖、图标层级、任务栏和自有资源清理验收。
3. Explorer generation 变化后的重新发现和 Host-owned DComp Surface 重建已通过自动与人工验收；旧 Shell HWND 没有复用。
4. 下一项 P1 门禁是隔离 Renderer-child DComp 探针，严格复用协议 v1 的容器 HWND 语义，并验证独立 PID、child parent/bounds、首帧、退出清理及重新附着；探针 Host Surface 必须显式使用 DefView anchor/WorkerW backdrop，而不是当前 Factory 的通用 `HWND_BOTTOM` 行为。
5. Renderer-child 路径通过则保留协议 v1，并把 Platform 内部 attachment lease 接入 Raised Surface Factory；Application/Renderer 只能看到 LiveWall 容器 HWND，不能获得 Shell HWND。只有 child 路径失败且证据证明必须跨进程共享 GPU 资源时，才设计协议 1.1 的资源所有权、handle/fence、Schema 和契约。
6. binding 与 Factory 决策关闭后，进入 DPI、`TaskbarCreated` 误触发、热插拔、多屏、异常退出和产品 Host/Renderer 恢复矩阵。
7. 能力判断继续使用“运行时结构与 binding 验证 + 完整 build revision/UBR allowlist”；在矩阵完成前不修改 Legacy 规则、不写入生产 allowlist、不接入生产 Raised Adapter。

该路径在技术上可行，已确认 `Progman` 子窗口 Z-order、无边框 Host-owned DirectComposition 内容提交和诊断级 Explorer generation 重建成立；当前仍需完成 Renderer binding、产品恢复与剩余显示矩阵。Windows 未提供公开的动态壁纸宿主契约，因此探索结果只能形成版本化、可撤销的兼容适配，不能升级为平台保证。

## Stage B 接受条件

至少在拟支持的每个完整 Windows build revision 上完成：

- 单屏/多屏、负坐标、100–200% DPI、显示器热插拔；
- Surface 位于桌面图标下方，真实测试像素进入当前桌面的可见合成路径，且不遮挡任务栏或普通应用；
- 生产 attachment 记录 adapter、完整 build/UBR、结构指纹、Shell generation、presentation kind 和通过的矩阵；结构候选本身不算 attachment；
- provisional/active/retired 生命周期、失败回滚和异常退出清理；
- Explorer 重启后重建 Surface 并重新附着 Renderer，不复用旧 HWND；
- 独立 Renderer 进程在 Host Surface 内创建自有 child HWND，并以选定内部后端产生可见首帧；若使用协议 v1，DComp target/交换链及其资源必须完全归 Renderer 所有；
- LiveWall 自有资源清理与 Shell mutation recovery 分别通过；
- 诊断证据、适配器规则、allowlist 条目和回退行为可追溯。

全部通过后才能更新本文、专题文档和 Stage B 状态；只完成代码或自动测试不得提前标记接受。
