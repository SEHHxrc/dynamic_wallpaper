# 实现与验收状态

> 截止日期：2026-09-16。本文只记录可变的工程状态，不改变 `ARCHITECTURE.md`、已接受 ADR 或专题设计中的规范边界。

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
| 可渲染桌面附着能力 | 能力边界与生产形态 Platform 接线已实现，尚未启用生产能力 | Legacy 无结构候选；无边框 Host-owned Raised DirectComposition、独立 Renderer-child `HwndChild`、物理单屏、P1.6 双屏可靠性、P1.7 故障/Explorer 代表矩阵以及当前双屏断开/重连与主屏切换均通过真实像素、层级与清理验收 | 诊断证据成立；生产启用数量仍为 `0`，完整 DPI/拓扑/会话及 Shell mutation 矩阵通过前不得进入 allowlist |
| Renderer-child DirectComposition 隔离探针 | 已实现独立辅助进程、协议 v1 `HwndChild` Attach、Renderer-owned child/DComp/swap chain、事件与清理 | 不同 PID、parent/bounds、`DefView > Host > WorkerW`、`SurfaceAttached → FirstFramePresented`、正常退出、父进程取消和故意崩溃清理自动验证通过；人工确认真实像素完整可见，图标、普通桌面程序和任务栏均未被遮挡 | 已形成单屏 v1 `HwndChild` binding 诊断证据；同 Renderer 跨 Explorer generation 的自动与人工重附着均通过；仍不自动启用生产 Raised attachment |
| Renderer Protocol v1 正式会话 | 已实现 Host 侧进程 Provider、一次性凭据、正式握手、DTO 映射、状态机、单接收泵与事件缓冲 | 自动验证完整顺序、恢复 generation、阶段/握手超时、错误令牌、未知事件和重复 messageId；P1.6 双屏 10/10 次 process-cold 与 20 轮同 Host 连续会话通过 | 不修改 DTO、Schema 或协议版本；历史固定 15 秒样本仍为 `HistoricalUnclassifiedTimeout`，下一门禁是正式双屏局部故障与恢复矩阵 |
| 产品候选阶段可观测性与 deadline | 已实现 generation 终态任务、不可变单调时间线和按实际顺序 Surface 数量推导的统一 deadline；诊断等待 `Succeeded / Failed / Cancelled / CircuitBroken`，不再轮询活动 Session | 自动覆盖成功、FirstFrame deadline、Superseded、Host 取消、恢复熔断以及单/双 Surface 预算；默认单 Surface 为 52 秒、双 Surface 为 82 秒。P1.6 的双屏 90 秒、10 次 process-cold 与 20 轮连续替换均输出逐 Surface 时间线和独立 retirement 结果 | 不记录认证令牌，不将 HWND 作为持久身份；此前固定 15 秒样本保留为历史 `UnclassifiedTimeout`，后续故障必须归因到强类型 phase/reason |
| Apply deadline 与活动 Session 生命周期 | P1.5.1 已实现：Apply deadline 使用独立 lease，generation 进入任一终态即释放；活动 `PreparedSession` 不再保存 operation。正常替换、显式 Shutdown 和 Host Dispose 使用独立的逐 Renderer retirement policy/result，Surface 最终销毁使用不可取消路径；Host 保存有界 retirement journal | 自动覆盖成功后等待原 Apply deadline 过期再替换或退出，仍发送 Shutdown 并等待 `ShutdownCompleted`；覆盖不响应 Shutdown 时记录 `ShutdownTimeout` 后强制 Dispose、双 Session 独立预算与批次聚合、普通替换/Host Dispose 结果入 journal、32 条 generation journal 裁剪及 Host Dispose 清空。退出 Lively 后在无外接显示器的物理单屏环境补测通过：首帧 1.233 秒、提交 1.257 秒、独立退休 108.6 ms，ShutdownCompleted/Dispose/Surface cleanup 全部成功且无新增 WorkerW；人工确认主桌面橙色正常显示约 1 秒且图标/任务栏层级正确 | Apply journal 与长期 Session retirement 已解耦；P1.5.1 代码、交互式自动与物理单屏人工门禁关闭。非优雅 Shutdown 仍不会伪装为成功；双屏、热插拔和真实长时产品运行保留在后续矩阵 |
| P1.6 产品候选可靠性驱动 | 已实现 `--result-json` schema 1.0、Release process-cold 批次脚本、产品候选专用 `--soak-seconds`，以及支持 `primary/all/display-id` 的单 Host 串行 `--raised-desktop-product-candidate-loop`；循环逐 generation 等待 Apply 终态和上一批全部 Session retirement | Debug/Release 共 161/161 通过。`WinSta0\Default` 双屏 90 秒 soak、10/10 次 process-cold 和同一 Host 20 轮循环均通过。20 轮每代两个 FirstFrame、一次原子 Replace，共 40 个 Renderer 全部 graceful retirement（38 Replacement + 2 Shutdown），ShutdownCompleted/Dispose/Surface cleanup 全部成功，无自有 HWND/新增 WorkerW；用户确认两屏全程同步橙黄交替、无黑屏或覆盖并正常恢复 | P1.6 代码、自动与当前双屏人工门禁关闭；历史固定 15 秒失败继续保留。该结论不覆盖 Explorer、局部故障注入、热插拔、主屏切换、会话切换或 Shell mutation recovery，仍不是 Stage B 接受或生产 allowlist 依据 |
| 双屏产品候选选择与原子提交 | 已实现 `--target-display primary / all / <display-id>`，默认保持 primary；候选结果和 JSON 改为逐 Session/显示器，批次 retirement 改为逐 Renderer 受控并行 | 自动验证 primary/all/精确 ID 选择、未知 ID 拒绝、两屏两个 FirstFrame 后仅一次 Replace、任一屏 FirstFrame 失败时零 Replace 且整批清理，以及并行 Shutdown/独立 retirement 结果。`26200.9168 / session=7 / WinSta0\Default` 已完成 5 秒、90 秒、10 次 process-cold 和 20 轮连续替换；40/40 Renderer 全部优雅退休。`26200.9445` 又通过真实断开/重连整批重规划 | P1.6 双屏 PerDisplay、原子提交与当前热插拔切片通过；尚不覆盖主屏切换、快速反复插拔、零显示器和完整 DPI，仍不得加入生产 allowlist |
| P1.7 双屏故障与恢复矩阵 | 已实现诊断级正式 Renderer 定点故障计划、首次/替换/退休故障入口，以及最多保留 32 项、可等待的 recovery terminal/journal；故障可限定到目标进程批次、Surface、阶段和第 N 次阶段事件，不进入 Renderer Protocol | A 的 9 项正式进程矩阵全部验证零提交与全量清理；B 人工/自动确认新 generation 第二屏 FirstFrame 失败时旧双屏持续显示；C 确认旧 Renderer `ShutdownTimeout` 不回滚新双屏；D 正向恢复得到 `Succeeded + Reattached`；恢复期第二屏 FirstFrame 与 SurfaceAttached Fatal 两个实机样本均得到 `Succeeded + Rebuilt`，无半屏提交、身份复用、残留或视觉不同步 | P1.7 最低完成条件关闭；Replace 失败、重复信号和三次熔断由自动测试覆盖，正向 Reattached 与两个局部失败 Rebuilt 代表场景通过人工验收。该结论不覆盖热插拔、主屏切换、锁屏、睡眠或 Shell mutation recovery；后续显示拓扑变化另属 P1.8 |
| P1.8 显示拓扑重规划 | 已实现 `DisplayReconciliationPlan` 并接入 `DisplayTopologyChangedCommand` 单写者状态机；每个 topology revision 有独立、有界、可等待的 `Succeeded/Failed/Cancelled` 终态，区分 NoChange/Reconfigure/Suspend；`UnexpectedTopologyMutation` 在准备或 Replace 阶段按 350 ms、1 s、2 s 有界重试。Raised lease 携带捕获时的虚拟桌面坐标空间，Host 接受拓扑、Surface 创建及提交前要求 Display bounds、Progman bounds 与 lease 一致 | 自动矩阵以及真实断开/重连、快速 churn、主屏切换、refresh-only、scale、bounds+scale、位置/原点、双向旋转和 Span 代表矩阵通过。Span 覆盖 position、scale、`2→1→2` 与同 Host `PerDisplay→Span`；最后一项从 2 Session/2 Surface 原子切换为 1 Session/1 Surface，三个 Renderer 全部优雅退休且无残留 | **P1.8 按既定边界关闭。** 旋转和 bounds 扩张保留 `TransientCoverageGap`；pure bounds-only 因 Windows 联动 scale、物理零显示器因当前硬件无法安全产生，均登记 `EnvironmentInconclusive`，对应自动门禁已通过。该关闭不等于 Stage B 接受或生产 allowlist |
| P1.8.1 拓扑抢占与零显示器安全门禁 | 任何更高 topology revision 一经接受即取消旧 reconciliation 绑定的精确 Apply generation，不再依赖新计划是否创建 Surface；旧 preparation、Renderer/provisional Surface 和 350 ms/1 s/2 s retry delay 均使用同一可取消边界。新增非串行 topology-churn 诊断和 Host 内部 `DisplayTopologyEventForwarder`，关闭 Channel 投递失败可查询并发布失败事件 | 自动覆盖 `Reconfigure→NoChange`、`Reconfigure→Suspend`、retry delay 抢占、FirstFrame 与更高 revision 竞态、旧 provisional 全量清理、原提交 Session 保留和双屏 `Suspend→相同 DisplayIds Resume`。`26200.9445 / WinSta0\Default` 实机 `2→1→2→1→2` 发布 4 个 revision：前三个均 `Cancelled/Superseded`、中间 Replace 为 0，最终 revision 单次 Replace、2 个 Session、graceful/clean、无自有 HWND；用户确认旧双屏橙色全程保持且无覆盖，最终统一恢复。当前 Debug/Release 基线为 0 警告构建和 194/194 测试通过 | P1.8.1 代码、自动及真实快速 revision 抢占门禁关闭。工具不主动禁用最后显示器；零显示器实机登记为条件性 `EnvironmentInconclusive`，自动 Suspend/Resume 门禁已通过。Renderer Protocol/DTO/Schema 和 allowlist 不变 |
| P1.8-TG0 有限 Transition Guard 探针 | 已实现单目标显示器 90°/270°有限候选区域规划、相邻显示器扣除、有界矩形/面积门禁、`Created→Armed→Observed→Retired` 诊断生命周期和显式命令；Armed 后不存在窗口移动、resize 或再次 Commit 路径 | 规划算法及边界自动验证通过；Debug/Release 为 204/204。`26200.9445 / session=3 / WinSta0\Default` 重复矩阵中，纵→横 `5/5` 完整 GuardVisible；横→纵 4 次完整、1 次 Inconclusive，最终补测明确出现“上方小部分洋红、其余黑色 → 完全洋红 → 黄色”。全部运行均无静态壁纸暴露、相邻屏侵入、层级错误或资源残留 | **TG0 决策关闭：部分可行但严格目标失败。** DWM 能自动揭示 Guard，却不保证整个预测区域原子进入第一可见帧；不进入 TG1、不生产化 Guard、不修改 Host/Application Surface 契约、Renderer Protocol、正式 Replace 或 allowlist。旋转保留 `TransientCoverageGap` 已知限制 |
| P1.8.2 拓扑语义与 Span 布局安全 | Windows 拓扑源将运行期首个空快照暂存为 `EmptyCandidate`，相隔 350 ms 的第二个连续空快照才发布 Suspend；非空或采样失败取消候选，启动即为空使用独立初始 revision。Span 使用物理像素 union、单 Renderer 和 `scaleFactor=1`；union 与非成员显示器相交时返回强类型布局错误。Raised 竞争检测使用精确 HWND 生命周期注册表排除当前 Host 自有 Surface，不按进程名/PID 放宽 | 自动覆盖空拓扑稳定性、`2→0→2`、Span 几何/成员/失败回退和交集拒绝；Release 基线 `218/218`。实机 Span position、scale-only、`2→1→2` 及同 Host `PerDisplay→Span` 均通过。模式切换 generation 1 为 2 Session/2 Surface、generation 2 为 1 Session/1 Surface，两个 Replacement retirement 与最终 Shutdown 全部 graceful/clean | **P1.8.2 关闭。** pure bounds-only 与物理零显示器保持条件性 `EnvironmentInconclusive`；协议、DTO、Schema 和 allowlist 不变 |
| P1.9.0 真实 Host 组合根与生命周期 | 已实现同步 `Program`、同线程单实例 Mutex、`WindowsHostCompositionRoot`、`WindowsHostRuntime` 状态机、启动恢复终态等待、启动后环境补采、幂等 Shutdown 和反序释放。Restore 先载入完整持久 Assignment，再只 Apply 在线显示器；失败恢复或离线显示器不会在关停持久化时丢失。Stopping 后拒绝外部变更，迟到 prepared completion 只能终态化并清理 provisional 资源 | 自动覆盖重复 Shutdown、停止后 Apply/拓扑/Explorer 拒绝、FirstFrame 后迟到完成零 Replace、完整 Assignment 保存、Runtime 启停/补采/持久化/一次性释放；随 P1.9.1 合并后的 Debug/Release 总基线为 `224/224`。Release 正式组合根 `--validate-startup` 返回 0 并生成 schema v1 配置 | **生命周期骨架与停止门禁完成。** 生产壁纸仓库和 Video/Web Renderer 产品入口仍未实现，默认组合根只允许加载/保留 Assignment 并失败关闭内容恢复；P1.9.2–P1.9.5 尚未完成，Stage B 与 allowlist 不变 |
| P1.9.1 `TaskbarCreated` 门禁与真实事件链恢复 | 已实现内部 Explorer 监视端口、350 ms 验证与 2 秒后备链路、验证/转发失败强类型结果、Stopping 丢弃分类，以及只供诊断组合根使用的真实隐藏窗口信号注入和 Explorer 重启入口；默认 `LiveWall.Host` 不暴露实验开关 | 自动覆盖 10 次 burst 严格 No-op、验证异常、fallback outage 去重、真实 Bridge 转发、关停后丢弃和 Dispose 后零投递；Debug/Release 总基线 `224/224`。`26200.9457 / WinSta0\Default` 双屏 No-op 得到 `10 raw / 0 validated changes / 0 recovery / 0 Replace`。真实 Explorer PID `11908→37580` 后得到 `1 raw / 1 validated change / 1 recovery / 1 Replace / 0 duplicate`、终态 `Succeeded + Rebuilt`，退出码 0且无自有 HWND；人工确认两屏同步黑屏、Shell 恢复、同步橙色并还原，全程无半屏或覆盖 | **P1.9.1 关闭。** 本次为整批 `Rebuilt`，不能记作同 Renderer `Reattached`；黑屏属于显式终止 Explorer 的允许窗口。P1.9.2–P1.9.5、生产内容入口和 Shell mutation 策略仍未完成，Stage B 与两类生产 allowlist 不变 |
| P1.9.2 系统锁定、显示电源与睡眠恢复 | 已实现完整状态源、单调 revision、WTS/Power 聚合及显式 suspend/resume 通知注册/释放；解锁、点亮和唤醒验证环境后才恢复完整播放策略 | P1.9.2 修复基线 Debug/Release `246/246`，随 P1.9.3.0 最新整体基线 `262/262`。物理单屏基线、锁解 3/3、用户暂停锁解 1/1、S0 Modern Standby 睡眠恢复 2/2 通过，实际命令/回执及清理完整；首个未完成睡眠样本保留 | **自动及物理单屏代表场景通过；双屏系统恢复待补。** 不宣称 P1.9.2 全部关闭，不代表 RDP/用户切换或 Video/Web 动态帧循环通过 |
| P1.9.3.0 会话状态机与正式事件链 | 已实现 Session 匹配、断开提交门禁、准备取消、最新环境延后处理、revision 关联验证、独立验证预算及可等待会话环境终态 | Debug/Release `262/262`；自动覆盖重复、抢占、迟到结果、Shell/拓扑延后处理、FirstFrame 后零提交、暂停意图、失败/超时、Stopping、恢复取消及离线 Assignment | **本代码/自动切片完成；真实会话实机未验收。** 后续 P1.9.3.1 才进行 RDP/用户切换诊断与人工实验，协议和 allowlist 不变 |
| Platform.Windows attachment lease | 已实现 | 自动验证 Platform 内部 parent/anchor/backdrop/generation、无句柄能力描述及跨 generation 旧 HWND 不再被显隐/复用 | Shell HWND/PID 不进入 Application、Renderer、持久化或 allowlist；每个新 generation 必须重新发现 |
| Raised Desktop Surface Factory | 已实现生产形态接线 | 受控 HWND 测试验证 hidden provisional、`WS_EX_NOREDIRECTIONBITMAP`、`DefView > LiveWall > WorkerW`、显式批量激活和清理 | 只消费当前 generation 的内部 lease；不等于当前 build 已获生产支持 |
| Raised Desktop 生产适配 | 已实现发现和 Factory 调用路径，但 allowlist 关闭 | 自动结构/Factory 契约通过；尚未进行允许生产选择后的完整真实矩阵 | `ValidatedBuilds` 为空，生产 Selector 跳过；不得由诊断成功自动写入 allowlist |
| 产品入口与完整运行链路 | 已增加显式单次、soak、process-cold 批次、同 Host 循环和真实拓扑变化入口，均复用 HostCommandLoop、SessionCoordinator、生产 Surface Factory 与正式 RendererProtocolSession | 物理单屏、`26200.9168` 双屏 P1.6/P1.7，以及 `26200.9445` 双屏断开/重连、快速抢占和主屏切换通过 | 当前入口仍只用于显式诊断；剩余 DPI/bounds、会话和 Shell mutation 矩阵完成前不改变 allowlist 或 Stage B 状态；首次历史样本不得事后定性 |

因此 Stage B 的总体状态仍为 **`In progress / Not accepted`**。已经完成的机制不应再描述为“尚未实现”，但自动测试结果也不能替代真实桌面兼容性结论。

### P1.9.3.1 真实会话诊断入口（2026-10-11）

首版范围决定：**RDP 实机为 `OutOfScope / WaivedForV1`，不支持远程桌面动态壁纸呈现，不再阻塞首版或 P1.9.4。** 这是范围豁免，不是测试通过。通用会话状态机、WTS 事件、暂停/失败关闭策略和显式诊断入口保留；本地快速用户切换实机仍为 `NotValidated`，有现成安全环境时补测，不创建账户或更改远程服务来获取样本。

已实现 `--raised-desktop-host-session-reconnect`，复用正式 Host Runtime 和事件桥，等待与 reconnect revision 匹配的会话环境终态；不通过轮询 Session 数量推断恢复完成。armed 后人工预算独立为十分钟，工具不主动断开、切换用户、注销或配置 RDP。通知、验证终态、恢复路径、实际 Renderer 播放回执及清理结果可保存 JSON；锁屏、错误 Session、倒序通知、未知路径或失败终态不能作为成功样本。异常保留 failure phase、已获得的验证终态与通知，不假定清理成功。

该入口完成时的 Debug/Release 自动基线为 `277/277`，Release 工具零警告/错误，架构边界检查通过。**入口和自动证据门禁完成；真实会话样本未执行，不能写为实机通过。** 本机 `26200.9457 / EditionID=CoreCountrySpecific`；RDP 按首版范围豁免，本地用户切换保留 `NotValidated`，不再要求先取得 RDP 样本才推进 P1.9.4。所有人工实验仍需显式授权。P1.9.2 双屏系统恢复独立待补；协议、Schema、两个生产 allowlist 和 Stage B 状态不变。

### P1.9.4-A Shell 抢占未提交准备（2026-10-11）

确认 Shell generation 改变后，Host 立即取消尚未终态的 Apply preparation，并将进行中的拓扑 reconciliation 终态化为 `Cancelled/Superseded`；Shell 恢复使用最新已接受拓扑。自动覆盖普通 Apply 和拓扑准备已经完成 FirstFrame、内部完成尚未投递时的抢占：取消终态立即可等待；恢复完成后迟到 provisional 结果只能清理，不能 Replace 或改变已提交 Session/Assignment。

此项只是 P1.9.4 的第一条自动边界，不等价于完整环境恢复仲裁。反向顺序（Shell 恢复中接受新 topology revision）、NoChange/Suspend 抢占、旧恢复终态与新事务隔离、恢复期重复信号及真实并发实验仍待收口。未执行 Explorer 或显示变化实机实验。

本次 Debug/Release 全量自动基线 `279/279`（新增两条受控的 FirstFrame 后迟到结果测试），Release 诊断工具构建及架构边界检查通过；不修改 Renderer Protocol v1、DTO/Schema 或生产 allowlist。

### P1.9.4-C 显式并发诊断入口（2026-10-11）

已实现 `--raised-desktop-host-environment-recovery`，必须同时指定 `--confirm-display-change` 和 `--confirm-explorer-restart`。复用正式 Runtime、事件桥、拓扑发布源和 Host recovery/reconciliation 终态，不直接投递 Explorer 恢复命令。`ENVIRONMENT_RECOVERY_ARMED` 后给用户独立的 180 秒手动改变一次缩放；只有拓扑 preparation 仍在途才显式重启 Explorer 一次，不自动重试 Shell mutation。

真实重叠需同时证明：重启前 topology terminal 未完成、该 revision 被 confirmed Shell generation 以 `Cancelled/Superseded` 抢占、一个恢复事务成功且只有一次 Replace、最终已观察 revision 与活动 Surface 几何一致。成功的串行样本记 `EnvironmentInconclusive`（退出码 9），不算并发通过；采集溢出为 `HarnessFailure`，超时/异常保存阶段和原因为 `DiagnosticIncomplete`，外部取消为 `Cancelled`，不伪造清理证据。结果记录逐 revision 和 recovery 时间线、Session retirement、Renderer 实际退出与句柄释放、自有 Surface 清理、执行上下文及 Shell WorkerW 增量；不持久化认证令牌、Pipe 名称或 Shell HWND/PID。

新增 15 条纯合成证据/进程清理测试，Debug/Release 全量基线均为 **302/302**，Release 工具零警告/错误，架构边界及差异检查通过。**代码与自动门禁完成，实机尚未执行，P1.9.4 未关闭。** 人工实验须另行确认，预期恢复后仍为橙色，不以黄色出现为通过条件。Renderer Protocol v1、DTO、Schema、默认产品入口、两个生产 allowlist 和 Stage B 状态保持不变。

### P1.9.4-B Shell/display 共享恢复仲裁（2026-10-11）

已接通反向顺序：Shell 恢复进行中接受新 topology revision，只保存最新拓扑并取消旧 reattach、rebuild preparation 或退避；不对旧 Shell Surface 做活动 resize，不另起部分显示器替换。所有合并 revision 均有可等待 reconciliation terminal，中间 revision 为 `Cancelled/Superseded`；最新 revision 与共享 recovery 终态关联，成功重建后记录一次真实 Replace，零显示器 Suspend 不伪造 Replace。

单独看属于 NoChange 的拓扑，在 Shell 已失效时仍按 Reconfigure 收口，不能保留旧 Shell HWND。空显示器按 Suspend，保留 Assignment、不启动新 Renderer，原 DisplayId 返回后走既有恢复规划。旧准备完成结果用不可取消的内部投递交给单写者清理，后台阶段记录绑定原 recovery operation；迟到 retry/failure 不得终态化较新 attempt。被新拓扑取消的旧 rebuild 明确记录 `Cancelled/Superseded`，全批清理后再准备最新计划；布局拒绝有明确失败终态。

新增自动覆盖：reattach 首帧后遇到同布局/几何变化/零显示器及多 revision、准备好的 rebuild 被抢占、在途 rebuild 取消和迟到重试/失败、双屏两首帧屏障与暂停意图、合并事务关停、合并后重建耗尽预算失败关闭。**此为代码与受控自动证据，不是实机验收；P1.9.4 尚未关闭。** 下一步 P1.9.4-C 需独立的显式并发实机诊断与用户授权，观察真实 Explorer/显示事件交错、最终全屏覆盖、层级和资源/Shell 清理。P1.9.5 完整退出矩阵也未被本轮一条 Shutdown 测试替代。

本轮新增 8 条自动样本，Debug/Release 最新全量基线均为 `287/287`；Release 诊断工具零警告/错误，10 个项目的架构边界及 `git diff --check` 通过。没有修改协议 v1、DTO/Schema、生产 allowlist 或默认产品入口；没有执行真实 Explorer/显示变更。

### P1.9.3.0 会话状态机、正式事件接线与自动门禁（2026-10-11）

代码与自动门禁通过，Debug/Release 最新基线 `262/262`；该切片不包括真实 RDP 或快速用户切换。P1.9.2 单屏代表场景保持通过，双屏系统恢复独立待补。

- 原生 WTS 保留通知 SessionId，事件桥记录有界会话序号、当前 Session 匹配结果、系统 revision 与单调耗时；非当前 Session 不进入本 Host 状态。
- 断开立即 Suspend 并取消未提交 Apply/reconciliation/recovery；所有 prepared 提交入口重新检查会话及被取消 generation，FirstFrame 后迟到批次也不能 Replace。已提交 Assignment 和用户暂停意图保留。
- 断开期间只保存最新拓扑与 Shell 变化；验证通过后稳定环境走 Resume，几何变化走 Reconfigure，Shell 失效或无有效 Session 走完整 Rebuild。断开中被取消的恢复先完成 abandon，再创建新会话，不复用旧 HWND。离线 Assignment 不迁移到陌生 DisplayId。
- Shell 验证成功和异常都携带请求 revision；再次断开立即取消本次可等待终态，旧成功/失败均不能放行新重连。重连验证预算为独立 10 秒，异常/超时输出强类型 Failed，保持暂停；Shutdown/Dispose 输出 Cancelled。
- Host 内部 `WaitForSessionEnvironmentAsync` 返回有界保留的验证终态、Resume/Reconfigure/Rebuild 路径、强类型原因和单调耗时；Reconfigure/Rebuild 关联既有事务终态，不轮询 Session 数量。默认生产事件输入继续开启，自动状态机测试隔离真实电源通知，防止实机初始通知污染合成事件顺序。

新增自动覆盖重复通知、重连中再断开、迟到成功和失败、断开期间 Shell 变化/拓扑 churn、FirstFrame 后禁止提交、验证异常/超时、暂停意图、非当前 Session、Stopping、恢复准备取消、refresh-only 恢复和陌生显示器不继承。Renderer Protocol v1、DTO/Schema、两个生产 allowlist 均未修改；Stage B 保持 `In progress / Not accepted`。下一实机切片为 P1.9.3.1，仍需用户授权和逐步操作标记。

### P1.9.2 验收证据补强（2026-10-11）

第二个有效单屏睡眠样本通过：`artifacts/diagnostics/p1.9.2-single-sleep-resume-20261011-03.json` 为 ValidSuccess、退出码 0，完整 suspend/resume、显示电源和锁解事件齐备；仍锁定/显示关闭时保持暂停，全部恢复条件满足后才 Play，实际写入及独立回执通过。generation `1→1`、Replace `1→1`、运行中 retirement `0→0`、Session/Renderer identity 保持，无额外 recovery/reconciliation。正常退休 25.6720 ms、issues 为空、全部资源清理，无自有 HWND，直属 WorkerW `1→1`、新增 0。用户观察登录后约 3 秒还原，其余正常。**最新状态：自动门禁及物理单屏代表场景通过（基线、锁解 3/3、用户暂停锁解 1/1、睡眠恢复 2/2）；双屏系统恢复待补测，P1.9.2 整体不宣称全部关闭。** 首个未完成样本保留；P1.9.3.0 可继续开发，不升级协议或开放生产 allowlist。

睡眠通知订阅修复后的首个物理单屏样本通过：结果 `artifacts/diagnostics/p1.9.2-single-sleep-resume-20261011-02.json` 为 `ValidSuccess`，退出码 0；真实 SystemSuspending/SystemResumed、显示电源及锁解事件齐备，恢复后在显示关闭/仍锁定阶段继续 Suspend，最终环境验证和解锁完成后才 Play，实际写入及独立回执验证通过。generation `1→1`、Replace `1→1`、运行中 retirement `0→0`、Session/Renderer identity 保持，无额外 recovery/reconciliation。正常退休 25.7830 ms、所有清理项通过、issues 为空，无自有 HWND，直属 WorkerW `1→1`、新增 0。用户观察“重新登陆后还是3秒左右就还原了，其他仍然正常”，不记作可见满 5 秒。最新有效睡眠样本 `1/2`，原 -01 未完成样本保留；双屏系统恢复待补测，不能因此关闭全部 P1.9.2 或接受 Stage B。

单屏睡眠恢复第一个样本未完成：用户实际选择睡眠，S0 Modern Standby 能力及 Kernel-Power 506/507 日志确认进入/退出待机；探针超时，退出码 1，分类 `TransitionIncomplete`、清理验证 `Unavailable`，不记作通过。用户观察唤醒登录后橙色保持较久，其余层级正常。原始结果 `artifacts/diagnostics/p1.9.2-single-sleep-resume-20261011-01.json` 保留。代码发现缺少显式 suspend/resume 通知订阅，已补充注册/释放及生命周期测试；Debug/Release 均为 `246/246`，Release 诊断构建零警告/错误，架构边界通过。订阅修复仍待独立实机复测，不认定为全部异常的唯一原因，不改变 Windows 电源设置。最新状态：单屏基线、普通锁解 3/3、用户暂停锁解 1/1 通过；睡眠有效通过样本仍为 0/2，双屏系统恢复待补测。协议、Schema、生产 allowlist 与 Stage B 状态不变。

物理单屏用户暂停→锁屏→解锁 `1/1` 通过：锁屏前实际 Pause/Paused，锁屏中 Suspend/Suspended，解锁验证后实际再次 Pause/Paused、策略原因 UserRequest，未发送解锁后的 Play；`userPausePreserved=True`。generation、Session/Renderer identity 和活动 Surface 保持，无额外 Replace/恢复事务。正常退休 27.9745 ms，issues 为空，无自有 HWND或新增直属 WorkerW，退出码 0。用户观察与普通锁解相同：“都是重新登陆后3秒左右还原原壁纸”；静态色块无自主渲染循环，外观相同不作为暂停证据，此项以真实 Pipe 写入及独立回执验收，不等于 Video/Web 动态帧循环已验证。结果 `artifacts/diagnostics/p1.9.2-single-user-pause-lock-unlock-20261011-01.json`。单屏基线、普通锁解 3/3、用户暂停锁解 1/1 已通过；睡眠恢复 0/2 和双屏系统恢复仍待验收。

物理单屏正常锁屏/解锁 `3/3` 通过。第 3 轮真实事件与前两轮一致，实际 Suspend/Suspended、验证后 Play/Playing 齐备，generation `1→1`、Replace `1→1`、运行中 retirement `0→0`、Session/Renderer identity 保持，未触发恢复或拓扑重规划。最后正常退休 28.1993 ms、全部清理项通过、issues 为空，无自有 HWND，新增直属 WorkerW 为 0，退出码 0。用户观察“和上次一样，3秒左右恢复原壁纸”，不将其写作可见满 5 秒。独立结果 `artifacts/diagnostics/p1.9.2-single-lock-unlock-20261011-03.json`。该结论只关闭单屏正常锁解三轮；用户暂停锁解、两轮睡眠恢复与双屏系统恢复尚未验收。

物理单屏锁屏/解锁第 2 轮通过：`SessionLocked → SessionUnlockRequested → SessionUnlocked`，Suspend/Suspended 与验证后 Play/Playing 的实际协议证据齐备；generation `1→1`、Replace `1→1`、运行中 retirement `0→0`、Session/Renderer 身份保持。最终正常 Shutdown/Dispose/Surface cleanup，issues 为空，无自有 HWND，新增直属 WorkerW 为 0，退出码 0。用户再次观察“重新登陆后两三秒左右恢复原壁纸”，保留为实际可见时长，不记作可见满 5 秒。独立结果 `artifacts/diagnostics/p1.9.2-single-lock-unlock-20261011-02.json`；锁解累计 `2/3`，用户暂停锁解、睡眠与双屏系统恢复仍待验收。

物理单屏锁屏/解锁第 1 轮通过：真实系统事件为 `SessionLocked → SessionUnlockRequested → SessionUnlocked`，实际发送 Suspend 并收到 Suspended 回执；验证后发送 Play 并收到 Playing 回执。Host generation `1→1`、Replace `1→1`、运行中 retirement `0→0`，Session/Renderer 身份保持；正常退出 retirement 27.5545 ms、全部清理项通过，issues 为空，无自有 HWND/新增直属 WorkerW，退出码 0。用户观察为“锁屏后打开仍然是橙色，过两秒左右恢复”；该约 2 秒是人工可见时长，不改写为实测 5 秒。独立结果 `artifacts/diagnostics/p1.9.2-single-lock-unlock-20261011-01.json`。锁解累计 `1/3`；用户暂停锁解、睡眠及双屏系统恢复尚未验收。

单屏启动/退出基线已完成：`26200.9457 / session=7 / WinSta0\Default / 2880×1800@200%`，5 秒 primary 候选首帧约 1097.588 ms、Replace 约 1118.598 ms，终态 Succeeded、退出码 0；retirement 34.631 ms，ShutdownSent/ShutdownCompleted/RendererDisposed/SurfacesCleaned 全部为 True、issues 为空，无 LiveWall HWND，直属 Shell WorkerW 前后均为 1且无新增。用户确认完整橙色、图标/任务栏/普通窗口层级及正常还原均无异常。结果文件为 `artifacts/diagnostics/p1.9.2-single-baseline-20261011-01.json`；该项自动与人工通过，不代表锁屏、睡眠或双屏系统恢复已验收。

在原 `235/235` 基线上补齐播放策略决定、实际 Pipe 写入与独立 Renderer 回执的有界 journal，测试 Renderer 支持 Play/Resume/Pause/Suspend；静态色块不作为暂停证据。修正不同系统状态相互取消待恢复事实的边界，以及 Renderer 退出先于最后 ShutdownCompleted 接收的竞态。最新 Debug/Release 均为 `245/245`。诊断支持逐样本 JSON、用户暂停锁解，睡眠环境变化等待恢复/重规划终态。代码与自动门禁通过；物理单屏基线、锁解三轮、用户暂停锁解一轮和睡眠两轮仍待验收，双屏系统恢复待补测。P1.9.3.0 会话状态机开发不以双屏可用为前置；协议 v1、DTO/Schema、两个生产 allowlist 不变。

2026-09-14 旋转补充状态：纵屏→横屏最终 `Reconfigure`、黄色全覆盖、外接屏保持、层级与清理均通过，但新几何提交前主屏右侧短暂露出静态壁纸，因此旧实现的旋转连续性为 `TransientCoverageGap`。代码随后在 provisional preparation 前通过协议 v1 `SetBounds` resize 旧 Renderer child/swap chain、重建 RTV 并重绘最后一帧，再 resize 旧活动 Host Surface；快速 revision 回到原布局时也会恢复活动几何。修复后的横屏→纵屏实机复测中，主屏橙色过渡完整、未再露出静态壁纸，保留更改后完整变黄；外接屏始终保持橙色，最终两屏一起恢复。自动结果为 `Reconfigure/Succeeded`、`ActiveSurfacesResized=Success`、一次 Replace、只退休主屏旧 Session、`valid/finalGraceful/finalClean=True` 且无 HWND 残留。完整基线仍为 198/198；反向纵屏→横屏尚待同修复版本复测。

反向纵屏→横屏复测的最终 `Reconfigure`、黄色全覆盖、外接屏保持和清理同样通过，但新增右侧区域仍在不到 0.5 秒后才被橙色填满，因此第一版 active transition resize 只缩短了缺口，没有关闭连续性门禁。根因是 Host 只等待 `SetBounds` 写入 Named Pipe，未等待 Renderer 实际完成 `ResizeBuffers + Present`。现已在不修改 DTO、Schema 或协议版本的前提下，将既有 `FirstFramePresented` 定义为活动 bounds 重绘完成确认：Renderer 先在旧容器裁剪下完成 resize/present，Host 收到当前 Renderer generation 的确认后才 resize 容器；状态机拒绝确认前开始下一次 Attach。自动基线更新为 199/199，仍需再次执行纵屏→横屏人工复测。

后续双向实测进一步定位了过渡时序。同步 `ResizeBuffers/DwmFlush` 和重复 Raised lease 发现分别会把活动适配阻塞在约 0.74 秒；当前实现改为原生显示信号到达后立即采样、复用当前 Shell generation 的 parent/anchor、对既有 DComp 最后一帧提交临时拉伸，并从 Progman 读取实时坐标调整活动 Host Surface，正式 provisional 创建/Replace 仍执行完整 350 ms 结构稳定性和精确 bounds 门禁。自动基线为 200/200，纵屏→横屏样本的 `ActiveSurfacesResized` 已降至 20.587 ms、`SurfacesReplaced` 为 1043.298 ms，最终清理完整；人工仍观察到 Windows 黑屏后极短的原静态壁纸闪现。故准确状态为：最终几何/原子提交通过，响应式 resize 已接近单帧，但旋转连续性仍为 `TransientCoverageGap`。若要求绝对零暴露，必须另行设计变化前已存在的有限 overscan/transition guard；不得把多个 per-display Surface 直接设为超大矩形而破坏显示器隔离。

2026-09-18 的 `26200.9457` bounds 实机探索受 Windows 自动缩放策略影响，尚未形成纯 bounds-only 样本。主屏缩小 `2880×1800@200% → 2560×1600@175%` 时仅主屏橙→黄、外接屏持续橙色且无露底；反向扩大 `2560×1600@200% → 2880×1800@225%` 时事务仍为单主屏 `Reconfigure/Succeeded`，活动适配 24.880 ms、Replace 1034.568 ms、最终 graceful/clean，但橙→黄之间右侧和下侧短暂露出静态壁纸。第三次把两个模式预先设为相同 scale 的尝试仍被 Windows 联动为 `2560×1600@225% → 2880×1800@250%`；人工未再看到露底，但终态输出句柄在读取前被系统回收，只保留视觉记录，不计自动样本。故本机 pure bounds-only 实机登记为 `EnvironmentInconclusive`，停止通过反复切换模式追求样本；bounds 扩张继续保留 `TransientCoverageGap`，外接屏隔离、最终 attachment 安全和原子 Host 提交仍通过。该反例不重新开启 TG1。

## 当前验证基线

截至 2026-09-16 的最近一次完整验证结果：

| 验证项 | 结果 |
|---|---|
| Debug：架构边界、还原、构建、5 个测试工程 | 通过；10 个项目边界检查、`0` 警告 / `0` 错误，`217/217` 测试通过 |
| Release：架构边界、还原、构建、5 个测试工程 | 通过；10 个项目边界检查、`0` 警告 / `0` 错误，`217/217` 测试通过 |
| Visual Studio 18.9 原生测试平台：Domain | `12/12` |
| `git diff --check` | 通过 |
| 当前 Windows 完整 build | `26200.9445`（Windows 11 25H2）；此前 `26200.9168` 证据继续按原 UBR 独立保留 |
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
| Raised Desktop 产品接线 | Platform 内部 Lease、Adapter Selector、Host 和 Native Surface Factory 已贯通 | 自动测试覆盖能力描述、Raised profile/Z-order、生产 allowlist 关闭和跨 generation 旧 HWND 不再被触碰；物理单屏、P1.6、P1.7、P1.8 拓扑/Span 代表矩阵及 P1.9.1 正式事件链通过 | P1.8 已按条件性边界关闭，P1.9.0 生命周期与 P1.9.1 Shell 事件门禁已接线；P1.9.2–P1.9.5 系统会话及 Shell mutation 矩阵尚未执行，仍不是 Stage B 接受 |
| Raised Desktop 清理 | 诊断结果已拆分为 `OwnedResourcesCleanup` 与 `ShellMutationRecovery`；LiveWall 自有 HWND 已销毁，但初次 Raised 请求留下的 Shell WorkerW 仍存在，整体 shell mutation recovery 为 `CleanupIncomplete` |
| `26200.9168` 可渲染桌面附着能力 | DirectComposition 诊断及正式产品候选的主屏单 Surface 证据为 `1`；生产启用数量为 `0`，Legacy 与 Raised Desktop allowlist 均为空 |

自动验证证明代码和契约基线稳定；干净交互式采样确认 `26200.9168` 没有合规 Legacy WorkerW。Raised 的 layered/non-layered GDI 路径保留为“结构不等于呈现”的失败反例，无边框 Host-owned DComp、Renderer-child v1 `HwndChild`、Explorer generation、物理单屏、P1.6、P1.7 与 P1.8 已按各自边界通过。历史固定 15 秒超时和早期失败样本仍不可事后改写；pure bounds-only、物理零显示器以及 ADR-009 的 `TransientCoverageGap` 均保留准确分类。P1.9 会话生命周期和 Raised 请求可撤销性仍未关闭，不能进入生产 allowlist。基础 build 26200 不是可单独使用的永久兼容键，同一基础 build 会随累积更新获得不同 UBR。

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
11. 显式候选入口和双屏 PerDisplay 已通过 5 秒基础、90 秒长驻、10 次 process-cold 和 20 轮同 Host 连续替换；两个独立 Session/Surface 全部首帧后仅一次批量 Replace，不同 DPI、负坐标和两组资源清理均已覆盖。P1.7 局部故障/Explorer 代表矩阵和 P1.8 当前负坐标双屏断开/重连也已通过；状态不得因此提前升级为生产支持。
12. P1.7-A/B：在诊断 composition root 与 `RendererChildProbe` 增加按 generation、目标 Surface 和阶段定点的故障计划；优先让第二 Surface 分别在 Attach、ContentLoaded、FirstFrame 失败，验证首次 Apply 零提交，以及已有双屏时新 generation 失败仍保持旧双屏、清理全部 provisional 资源。故障控制不得进入 Renderer Protocol DTO 或生产配置。
13. P1.7-C：让旧 generation 的某一 Renderer 在新双屏已经 Replace 后不返回 `ShutdownCompleted`，验证新双屏不回滚，退休结果明确记录 `ShutdownTimeout`，随后仍 Dispose Renderer、清理旧 Surface，并允许最终新会话正常 Shutdown。
14. P1.7-D：先增加 Host 级可等待的 recovery terminal/journal，记录 generation、逐 Surface Attach/FirstFrame、单次 Replace、abandon、重建 attempt，以及 `Succeeded / Failed / Cancelled / CircuitBroken` 终态；成功时另记 `Reattached / Rebuilt` completion mode，不得重新用 `Sessions.Count` 推断成功。随后完成双屏真实 Explorer generation 正向恢复，并覆盖恢复 Attach/FirstFrame 局部失败后的整批 abandon、全新 Renderer/Surface 限次重建和熔断。恢复路径不发送 Load/ContentLoaded，因此不得把 ContentLoaded 列为 recovery 阶段；显式 Explorer 终止仍需用户双重确认。
15. P1.8 显示拓扑重规划已按既定边界关闭：真实断开/重连、快速反复插拔、主屏切换、DPI/bounds+scale、位置/原点、旋转和 Span 代表矩阵通过；pure bounds-only 与物理零显示器为条件性 `EnvironmentInconclusive`，自动门禁通过。
16. P1.9.0 已完成真实 Host 生命周期骨架和 Stopping 门禁；P1.9.1 又关闭 `TaskbarCreated` burst 严格 No-op 与真实 Explorer 事件链单次恢复。P1.9.2 的统一系统状态源、暂停/验证后恢复和诊断入口已通过自动门禁，下一步先完成锁屏/解锁与睡眠/恢复实机验收，再进入 P1.9.3 RDP/用户切换、P1.9.4 Shell/display 并发恢复和 P1.9.5 退出竞态。生产内容仓库/Renderer 接线必须单独完成，不能借生命周期或诊断组合根结果宣称正式内容恢复。能力判断继续使用“运行时结构与 binding 验证 + 完整 build revision/UBR allowlist”；系统会话和 Shell mutation 策略完成前不修改 Legacy 规则、不写入生产 allowlist，Raised 生产 Selector 继续失败关闭。

该路径在技术上可行，已确认 `Progman` 子窗口 Z-order、无边框 Host-owned DirectComposition 内容提交、Renderer-child `HwndChild` binding、诊断级 Explorer generation 重建、P1.6 双屏可靠性、P1.7 局部故障/恢复和 P1.8 当前拓扑样本成立；TG0 已按部分可行/严格目标失败关闭，当前仍需完成剩余显示、系统会话与 Shell mutation 矩阵。Windows 未提供公开的动态壁纸宿主契约，因此探索结果只能形成版本化、可撤销的兼容适配，不能升级为平台保证。

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
