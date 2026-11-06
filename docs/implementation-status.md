# 实现与验收状态

> 截止日期：2026-11-06。本文只记录可变的工程状态，不改变 `ARCHITECTURE.md`、已接受 ADR 或专题设计中的规范边界。

## 状态口径

- **已实现**：代码路径和公开端口已经存在。
- **自动验证通过**：可在测试进程或受控替身中验证实现契约，不等于 Windows Shell 真实兼容。
- **真实桌面验收通过**：已在声明支持的 Windows 完整 build、显示器和 DPI 矩阵中验证图标层级、恢复与清理。
- **未接受**：不得用于生产能力声明，也不得加入 build allowlist。

## 当前阶段：C2.0 代码与自动验证完成，真实内容恢复未验收（2026-11-06）

### C2.0：最小持久视频库与默认 Video Provider（2026-11-06）

**只完成代码、自动测试和一次无内容的 Host 启动冒烟；没有真实桌面实验，C2 未关闭。**

| 项 | 状态 |
|---|---|
| `JsonWallpaperRepository` 替换占位仓库：`library/<id>/<version>` + `library/.index.json`，索引是唯一提交点 | 已实现、自动验证通过 |
| `WallpaperManifestReader` 严格读取 manifest v1；`WallpaperContentTree` 拒绝重解析点并计算内容哈希 | 已实现、自动验证通过 |
| 保存时复制到私有 staging 后再校验，源目录后续改动进不了库；已提交版本不可变 | 已实现、自动验证通过 |
| 读取失败关闭：索引损坏、内容被篡改、manifest 身份不符、入口经过重解析点时均视为不存在；损坏索引不会被覆盖 | 已实现、自动验证通过 |
| 默认 Provider 只接受与 Host 同目录的 `LiveWall.Renderer.Video.exe`，缺失时无 Provider | 已实现、自动验证通过；未用真实 Renderer 跑过默认链 |
| 启动恢复经真实仓库把 CanonicalContentRoot/入口交给 Renderer；缺失、篡改或无 Provider 时 Failed、零 Surface、保留 Assignment | 自动验证通过，桌面与 Renderer 为替身 |
| 真实 Host 进程启动 | Release `--validate-startup` 退出码 0，配置为空，未创建 `library/`；不涉及内容恢复 |

Debug/Release 各 **644/644**（Domain 12、Application 316、Renderer Contracts 121、Package 87、Windows 108），均 0 警告 0 错误；日志在 `artifacts/diagnostics/c2/`。Renderer v1 协议和 DTO 不变。

**真实恢复尚不可达**，以下均未解决，不得记为通过：

1. 两个生产 allowlist 仍为空，默认 Host 在 `EnsureTopologyAsync` 失败关闭（Adapter 的 `IsSupported` 为 false，不发送 Shell 请求），内容恢复终态为 TopologyFailed。是否及如何启用受 ADR-010 与 Stage B 门禁约束，C2 不改动。
2. 还没有写入库的入口：`SaveImportedAsync` 没有 Importer、RPC 或工具调用方，产品内无法导入视频。
3. Host 构建输出不含 Renderer 和 `native/libmpv-2.dll`，并排部署要等打包切片；当前开发 DLL 仍是 DevelopmentOnlyNotForDistribution。
4. 仓库只收 Video；Web/Image 被拒绝。`.lwpkg` ZIP 读取未实现。

已知限制：Renderer 占用旧内容时，移除或升级仍会提交索引，旧目录成为不可见残留，同版本重新导入时清除，否则一直留在磁盘（`.staging`/`.trash` 在每次 Host 启动后的首次写操作时清理，不含这类目录）。读取不重算内容哈希，只校验结构和文件存在；哈希只在保存时校验。

顺带修正 `wallpaper.schema.json` 的 `entry` 模式：旧模式会放行 `content/../wallpaper.json` 和 `content/./x`，属于校验漏洞；代码读取器一直拒绝这些路径。

门禁现状：`tools/check.ps1` 当前不能完整通过。`verify-boundaries.ps1` 依赖 PowerShell 7，本机只有 5.1；用内存中改过路径和编码处理的副本运行（未改仓库文件）时，唯一报错是 `.github` 缺少 README，该目录来自提交 1980a68，与 C2.0 无关。构建和测试是直接执行的，结果如上。

### 代码清理（2026-11-06）

在 C2.0 基础上做了一次不改行为的清理：删除分析器确认的未使用参数、只写字段、未使用属性、从未构造的 `RendererInitialized` 事件和 15 个多余 `using`；诊断编排中 5 处重复的 Host 装配、6 处会话结果投影、两份只读诊断仓库和合成内容/探针 Provider 合并为共享实现；两个 JSON 存储共用 `StrictJson`。Renderer v1、DTO/Schema、生产组合根与诊断入口参数均不变。Debug/Release 各 644/644，`VideoPresentationProbe` Release 构建 0 警告 0 错误。

清理后复验（2026-11-07，`26200.9711 / session=4 / WinSta0\Default / 2880×1800 / 200%`，Release 可执行文件显式重建）：

| 项 | 结果 |
|---|---|
| 隐藏 Host 产品链 + controls、candidate-plan、load-failure、first-frame-timeout、process-exit、pipe-disconnect | 7 项退出码均为 0，shellTouched=False，无 Renderer 残留；证据 `artifacts/diagnostics/cleanup/hidden-20261107-021440/` |
| 只读桌面预检 | ready=True，未发 Shell 请求、未建 Surface/Renderer；`video-c1/video-product-65f3b7d9b24343e7bfb43a8d7e1cd02b.json` |
| 获准可见 Video 控制 20 秒 | 四阶段完成，同 Renderer 新容器重附着，暂停 41 / 恢复 20 样本，ShutdownCompleted，Renderer ExitCode=0，自有清理 Complete；`video-c1/video-product-125d0cd25e6b4939b27bb0be04f88e1d.json` |
| 获准可见合成色块 loop（3 轮 × 3 秒） | 3/3 首帧后单次 Replace，3 个 Renderer 优雅退休，自有窗口为零，addedWorkerCount=0；日志 `cleanup/visible-loop.log` |
| 人工观察 | 用户确认两项均符合预期：画面、暂停/重附着/恢复、橙黄交替、图标/任务栏层级和原壁纸恢复正常 |

Video 运行前直接子 WorkerW 为 0（此前遗留已不在），运行后为 1，ShellMutationRecovery=CleanupIncomplete，整轮退出码 6，内层分类 ProductFailure；与清理前 2026-11-03 C1.2 复测在同样 0→1 时的分类一致，属 ADR-010 已知限制，不是本次清理的回归。未销毁 Shell 窗口或重启 Explorer。清理后入口行为在上述代表范围内复验通过；fault/recovery/topology 等其余诊断模式只有编译与自动测试，未做可见复验。

UI RPC DTO、`IProcessSupervisor`、导入端口、`RendererRestartPolicy` 等未被调用的类型属于架构文档规定、尚待实现的公开契约，按设计保留，不视为冗余。

### C1 收口与历史边界（2026-11-03）

P1.9.5 按下文 E 节既定条件关闭，不追加注销。P1.9.2 双屏系统恢复、P1.9.4 真实并发及双屏仍为 `Deferred / Pending hardware`；不把它们改为 Passed/Waived，也不阻塞 C0 隔离开发。

**C1 已按约定有限范围关闭**：C1.2 单屏正式产品候选及 C1.3 单屏控制/同 Renderer 重附着/正常退休代表项均通过，最小失败收口和未支持能力拒绝已验证。最新 Debug/Release 各 **573/573**。本次控制验收整体退出码 0；Shell WorkerW 前后均为 1、未新增，分类 NotRequired，不表示此前遗留 mutation 已恢复。C2 最小持久视频仓库与正式 Host 内容恢复待启动；双屏/真实 Video Explorer 恢复、长期资源与完整编码矩阵独立保留。Stage B 未接受，生产 allowlist 仍为空。

- **P1.10.0 策略设计完成**：[ADR-010](adr/010-raised-shell-mutation-production-gate.md) 冻结请求前/后/退出后三类基线和两种清理语义。已有 WorkerW 不能算本次创建，新增残留为 CleanupIncomplete，generation/采样不确定为 Inconclusive；不做未经验证的逆消息、不销毁 Shell 窗口、不静默重启 Explorer。生产 mutation 接受仍未关闭，两个 allowlist 保持为空。
- **C0 物理单屏呈现可行性切片通过并关闭（2026-10-28）**：用户确认 cover 复测“全程没有黑色，其他仍然正常”，结合实际 client/VO 同为 2880×1800、上下边距 0、左右各 -160 的裁切，真实运动画面、循环/暂停恢复、尺寸变化、桌面层级及自有清理在本单屏代表边界内通过。首轮黑边失败原样保留。该历史 wid/gpu-next 后端由独立诊断入口验证，不是产品 provisional/Apply/Replace 或 C1 正式会话验收，不把加载/VO 事件映射为 FirstFramePresented。ShellMutationRecovery 仍为 CleanupIncomplete，生产门禁不因 C0 关闭而放行。
- **C1.1 代码与非可见验证完成**：ADR-011 已由用户明确接受并同步根架构；正式 Video Program、Hello/Initialize、单接收泵、Wire/路径门禁、阶段超时及自有 child/DComp/交换链提交已接线。新增 37 项 Wire 自动门禁，当时 Debug/Release 各 520/520；正式可执行文件隐藏容器正常/暂停重附着、提前 Shutdown、Pipe 断开与路径拒绝已测，详见下节。后续 C1.2/C1.3 已补单屏人工代表项并按有限范围关闭 C1，默认产品 Host 内容链留给 C2。
- **开发依赖固定**：shinchiro `20261021 / x86_64 / mpv e471496c56`，项目内 `.cache/libmpv/20261021-x64`，归档 SHA256=`EFC719EFF3DC5954B4A5EFC800632808BCEC01CC9034940DD14E5EB7FE9EA633`，DLL SHA256=`04E4D27DC385DC18DDD93FBE69C75E635B864F3C6EC53A9122DF54C2F7842F4A`。检查实际版本为 `mpv v0.41.0-1227-ge471496c5`、FFmpeg `N-127893-g9a861e968`、client API 2.6。头文件 ISC 不等于 mpv core/FFmpeg 发行许可；该包为 DevelopmentOnlyNotForDistribution，发行审计仍是开放项，不安装到系统或改变全局 PATH。
- **素材与无桌面预检通过**：本地生成 320×180、24fps、72 帧/3 秒的原始 BGR AVI，移动青色条与橙黄背景，没有第三方媒体。原生 schema v1 manifest 检查通过；素材 SHA256=`8CADEB631BE8B6245B43CFEDC6B9F43F42C750AE4C144E83A9598D3DABADD68C`。fixture 为 `artifacts/diagnostics/video-c0/fixture-v1-20261024`，无桌面实际解码结果为 `artifacts/diagnostics/video-c0/headless-decode-20261024.json`：FileLoaded/PlaybackRestarted=True、time-pos=0.416667，vo=null，无 HWND/GPU/Shell Surface，desktopPresentationValidated=False。不是 D3D11、桌面可见或硬解能力通过。
- **自动验证**：新增 14 项 C0 路径/哈希、输出证据隔离、确定性视频/索引门禁，Debug/Release 全量均 **442/442**（Domain 12、Application 263、Renderer Contracts 36、Package 23、Windows 108）。C0 工具 Debug/Release 与产品 Video Release 构建零警告/错误；架构边界和差异检查通过。首轮受限环境的合约测试未完成，已中止并将大数组逐元素断言换成 Span 比较；在正常本地 Pipe 权限下重跑新增组 14/14 及两配置全量均通过，未放宽失败门禁。NuGet 审计/格式化工具的沙箱网络或 Pipe 拒绝通过明确的本地权限重试解决，未禁用审计。

最终非可见进程门禁也通过：实际 DLL 版本/完整构建参数保存于 `artifacts/diagnostics/video-c0/library-inspection-20261024.json`；缺少 `--confirm-desktop-presentation` 返回 2，未创建本应拒绝的结果文件/Surface；错误 DLL 哈希返回 1，未加载本地代码。C0 工具包含 PerMonitorV2 manifest，退出时保持 STA pump 直到 libmpv 结束，5 秒正常清理预算之外最多另等 2 秒强制结束自有子进程；环境/拓扑变化不算稳定呈现通过。结果的自有类名已归一化，不把类名内的临时 PID/GUID 写成结构身份。以上门禁均未创建可见视频或改变 Shell。

### C0 首轮物理单屏呈现尝试（2026-10-24，待人工观察确认）

用户明确授权后，在 `26200.9711 / session=12 / WinSta0\Default`、物理单屏 `2880×1800 / 200% / 60 Hz` 执行一次 `--desktop ... --duration-seconds 10 --confirm-desktop-presentation`。只读预检未发现竞争壁纸或残留诊断进程，原始证据为 `artifacts/diagnostics/video-c0/run-76c5350ff3074a89b8170b677b14b5ab.json`。分类 `PresentationAttemptCompleted`、failure=null：Renderer 进程独立、child parent 校验通过；gpu-next/d3d11 已配置，FileLoaded/PlaybackRestarted/VOConfigured 有记录，媒体时间推进，播放重启计数最终为 4（含首次启动，不宣称完整循环次数为 4）；Paused/PauseStable/ResumeAdvanced/ResizeExercised 均 True。Shell generation 与显示拓扑保持。

ShutdownCompleted/ChildDestroyed/HostDestroyed=True，Renderer ExitCode=0，Forced=False，OwnedWindowsRemain=False、OwnedResourcesCleanup=Complete；结束后只读检查 VideoPresentationProbe 进程数为 0。**本轮整体退出码为 6，不是 0**：请求前 Progman 直接子 WorkerW=0，请求后新增并在自有窗口销毁后仍保留 1 个 Shell WorkerW，ShellMutationRecovery=CleanupIncomplete。严格遵守 ADR-010，不把自有清理重命名为 Shell 恢复，不销毁 Shell-owned 窗口，不静默重启 Explorer，不自动重测。原始结果 manualPresentation=NotYetConfirmed、FirstFramePresentedEmitted=False、ProductSessionCreated=False、ProductionAttachmentEnabled=False 保持不变，需用户确认实际运动/循环、暂停/恢复、尺寸及层级后才能登记对应人工项，不能仅凭 VO 就绪推断可见。

### C0 首轮人工反馈与 cover 修正（2026-10-24，尚未重跑可见实验）

用户反馈顶部全程约 1 cm 黑色、底部约 1 cm 偏绿，其余均符合预期，包括约 1 秒暂停。真实视频可见、运动/控制、层级及恢复代表项按反馈记录；**稳定完整覆盖未通过**，不能用 VO 就绪或“其余正常”覆盖黑边，也不能把全程留边算成 ADR-009 的旋转 TransientCoverageGap。原始 JSON 的 manualPresentation=NotYetConfirmed 是当时机器记录，人工补充保存在本台账，不重写旧结果。

代码核对发现明确缺口：生成的 manifest.defaults.fit=cover，但 MpvClient 未配置 fit，16:9 的 320×180 视频按默认比例适配 2880×1800（16:10）时，推导的有效画幅为 2880×1620，上下各留 90 物理像素；与顶部观测相容，但首轮没有实测 client/VO 边距，不能把推导写成已测量的唯一根因。底部绿条有原始素材依据：非运动条区域的顶部 RGB=255,255,0，中部第 0 帧为 255,96,0；底部红色刻意降为 100，第 36 帧原始 RGB=100,168,0。该源数据解释绿色标记，不自动证明最终整体色彩准确。此前笼统描述“橙黄背景”不够清楚，已补分区说明，未重生成素材或改写历史哈希。

修正只在私有后端明确 keepaspect=yes、keepaspect-window=no、video-unscaled=no、panscan=1，按现有 cover 语义等比裁切填满；C0 只接受其原生 fixture 的 cover 配置。没有扩大窗口、拉伸画面或修改 Shell placement/生产 provisional。诊断新增 GetClientRect 采样的 Host/child 尺寸，以及 mpv osd-dimensions 的 w/h、四边距和 cover 参数；缺失/零尺寸/dummy/不一致/正留边不得判几何通过，负边距允许裁切。即使几何全通过，ProvesDesktopPresentation 仍为 False，实际像素和层级仍需人工确认，C1 正式首帧策略尚未实现。

新增 **9** 个留边/缺失/非有限值/覆盖不等于呈现门禁，并在已有 AVI 测试中核对方向标记 RGB；C0 纯门禁合计 23/23，Debug/Release 全量均 **451/451**（Domain 12、Application 263、Renderer Contracts 45、Package 23、Windows 108），Release 工具和产品 Video 构建零警告/错误。真实 DLL 无界面检查 `artifacts/diagnostics/video-c0/library-cover-inspection-20261024.json` 读回 keepaspect=yes/panscan=1；`headless-cover-decode-20261024.json` 解码通过，但 vo=null、viewport 0×0、HasNoLetterbox=False、ProvesDesktopPresentation=False，未伪造 GPU/像素通过。原首轮结果 SHA256 仍为 `B8447E44886F8790E52FDD25D85EB688E709B24D6D5D18C5EA0AC5651078B655`；原素材 SHA256 不变。

### C0 cover 授权复测（2026-10-28，人工反馈待收集）

用户明确同意重新复测，只读检查确认 `26200.9711 / session=14 / WinSta0\Default`、单屏 `2880×1800 / 200% / 60 Hz`，未发现竞争壁纸或残留诊断进程；当前基线无 Progman 直接子 WorkerW。使用相同 libmpv 和相同原始视频（SHA256 均未变化），执行一次约 15 秒的修正后可见探针，原始文件为 `artifacts/diagnostics/video-c0/cover-retest-20261028-ebc2e645ae174de4b933411c50054030.json`，SHA256=`3ACABEDE677F899372075DF62969BF47F0F4EDBDD31D927DDB644B10EF7DBB64`。

结果 `PresentationAttemptCompleted`、failure=null。初始/最终 Host 与 child 实测 client 均为 2880×1800，ChildFillsHost=True；最终 mpv viewport=2880×1800，top=bottom=0，left=right=-160，keepaspect=yes/panscan=1，CoverConfigured/HasNoLetterbox/geometry.validated=True。这是等比裁切而非扩大 HWND或拉伸；和首轮只靠比例计算的推断不同，本次有真实 VO 边距及 client 尺寸记录。Paused/PauseStable/ResumeAdvanced/ResizeExercised=True，Shell generation 与拓扑未变。

ShutdownCompleted/ChildDestroyed/HostDestroyed=True、Renderer ExitCode=0、Forced=False、OwnedWindowsRemain=False，OwnedResourcesCleanup=Complete；结束后探针进程数为 0。请求前 WorkerW=0，退出后新增的 1 个 Shell WorkerW 仍在，所以 ShellMutationRecovery=CleanupIncomplete，**整体进程退出码仍为 6**，不能解释为完全恢复或 Renderer 失败。工具未重启 Explorer、销毁 Shell 窗口或继续运行第二次实验。原始 manualPresentation=NotYetConfirmed、FirstFramePresentedEmitted/ProductSessionCreated/ProductionAttachmentEnabled=False 保留，等待用户确认顶部实际黑边、分区色彩、运动/暂停和层级；几何通过不替代可见像素验收。

本次复测阶段只执行一次已授权的实验、读取证据并同步状态文档，没有修改代码或重新运行全量自动测试；最近代码验证基线仍是 2026-10-24 的 Debug/Release 451/451。当时待收集的人工观察已在下节补齐，原始机器结果不改写；不自动再次运行桌面测试或重启 Explorer。

### C0 物理单屏呈现验收通过，开发切片关闭（2026-10-28）

针对上述唯一 cover 复测样本，用户明确反馈：“不错，这次全程没有黑色，其他仍然正常”。结合前述观察范围，登记该次稳定全覆盖、真实连续运动及循环、约 1 秒暂停/恢复、尺寸变化与恢复、图标/任务栏/普通窗口层级和结束后原壁纸恢复的代表性人工验收通过。背景分区按已说明的黄色/橙黄/偏绿方向标记理解；这不是仪器色准、HDR 或所有颜色管理路径验收。

证据仍对应 `26200.9711 / session=14 / WinSta0\Default`、单屏 `2880×1800 / 200% / 60 Hz`，固定 libmpv 与相同原始 BGR AVI。自动几何与播放记录、正常 ShutdownCompleted、Renderer ExitCode=0、句柄/自有 HWND/进程清理和本次人工观察共同证明：**Renderer-owned libmpv gpu-next/D3D11 可以通过既有 HwndChild 容器进入当前桌面可见合成路径，cover 能填满目标显示器。C0 呈现可行性开发切片按此有限边界关闭，不追加重复色块、视频或注销实验。**

清理结论继续分开：OwnedResourcesCleanup=Complete；本次新增 Shell WorkerW 未自行恢复，ShellMutationRecovery=CleanupIncomplete，整个诊断进程退出码仍为 6。C0 通过不意味着 Raised 请求可撤销，不静默重启 Explorer，不改变 ADR-010 或生产 allowlist。原始 JSON 的 manualPresentation=NotYetConfirmed/FirstFramePresentedEmitted=False 是运行当时机器记录，不靠事后改值伪造程序呈现证明；本节保留用户反馈并关联原文件 SHA256=`3ACABEDE677F899372075DF62969BF47F0F4EDBDD31D927DDB644B10EF7DBB64`，历史首轮失败不追改。

C0 关闭时确认下一切片为 **C1：正式 Video Renderer 协议 v1 会话**，先选择真实首帧呈现判据。该次关闭只补录验收，没有启动新测试；随后获用户同意开始 C1，新增开发和验证记录见下节。C0 的人工反馈、FileLoaded/PlaybackRestart/VO 几何均不能直接代替 C1 运行时 FirstFramePresented。

### C1.0：内部事务与提交门禁、D3D11 Render API 无窗口预检（2026-10-28）

**已实现并自动验证；C1 正式会话和桌面呈现尚未完成。** `VideoSessionTransaction` 是 Renderer 内部单写者事务核，明确 Initialize、Attach、Load、首帧、Bounds、重附着和 Shutdown 的阶段；`VideoFirstFrameGate` 将证据绑定到 session/generation、随机 binding epoch、content revision、操作序号和目标尺寸。首次及重附着只接受 Render/Present/Commit/同步精确成功，缺失、负值或正状态（含 occluded）均不能放行；未绑定到 Renderer child 的离屏帧也不能成为首帧。SetBounds 保留当前 binding/generation，允许同一目标已有帧变换后 Commit；Shutdown/Fault 清除待提交票据，迟到结果不能复活会话。这些均是私有代码，不增加 Wire 字段。

固定版本原生头文件提供 D3D11 Render API，调用方拥有纹理和呈现。C1.0 新增私有 `MpvD3D11RenderContext` 原型，由同一专属 render 线程调用和释放；普通 client API 留在控制线程，context 必须先于 client/native library 释放。C0 wid/gpu-next 与 headless vo=null 路径保留。**C1.0 当时的候选 vo=libmpv 与根架构技术表 gpu-next 不同**，ADR-011 当时为 Proposed，尚未修改正式 Program/架构；随后用户明确接受，当前状态见 C1.1，不再重复申请该决定。

`VideoPresentationProbe --render-api-preflight` 不创建 HWND/DComp target，不访问 Shell，不发送 FirstFramePresented。真实 GPU 将同一原始视频渲染到自有 D3D11 纹理，开发预检读回两帧不同非空 RGB 数据，并检查自身 Present 精确 S_OK；CPU readback 只用于诊断，不是生产帧拷贝方案。内部 10 秒取消预算不是 native hang 的硬进程保证，外层驱动可额外有界结束自己创建的 helper。

预检过程保留三份失败原始文件，均未重写或当成成功：

- `artifacts/diagnostics/video-c1/render-api-c9e98d50e1a44c93babc78f6e525d8db/result.json`：媒体错误，valid=False。
- `artifacts/diagnostics/video-c1/render-api-logged-27e706e2b988444ca87f02b0bfdc06a1.json`：媒体错误 -15，valid=False。
- `artifacts/diagnostics/video-c1/render-api-details-af6404f0cdea4365ac2fdf27ecbb456c.json`：媒体错误 -15，valid=False；gpuFailure=null 不等于呈现成功。

核对到预检自身生命周期竞争：render 线程取得两帧后立即释放 context，而控制线程还在读取媒体状态。改为两帧就绪后继续服务，由控制者采样完成再统一退休。修正后的独立结果为 `artifacts/diagnostics/video-c1/render-api-owned-lifetime-1b858b240d654d60b36af72f85609fc5.json`：退出码 0、valid=True、failure/gpuFailure=null、current-vo=libmpv、newVideoFrames=2、distinctPixelHashes=2、PresentResults=[0,0]。成功不倒推历史错误唯一根因，也不能替代尚未建立的 child/DComp 目标或桌面人工验收。结果明确 windowCreated/compositionTargetCreated/shellTouched/firstFramePresentedEmitted/desktopPresentationValidated/formalProtocolSessionValidated 均为 False。

新增 **32/32** 项纯自动测试覆盖严格提交结果、票据隔离、首帧一次性接受、恢复新 binding、Bounds 当前 generation、各阶段退出与迟到证据。2026-10-28 Debug/Release 全量各 **483/483**（Domain 12、Application 263、Renderer Contracts 77、Package 23、Windows 108），日志为 `artifacts/diagnostics/video-c1/full-suite-{debug,release}-20261028.log`。产品 Video 与预检工具 Release 构建均零警告/错误，架构边界 10 项目和差异检查通过。GPU 预检独立于这 483 项测试，不能混称自动协议或桌面验收通过。

C1.0 结束时将正式 Pipe/Envelope、实际目标与提交证据接线列为下一步 C1.1；该后续实施记录如下。C1.0 本身没有启动可见实验，也未完成正式会话，不能将其预检扩大为 C1.1 或桌面成功。

### C1.1：已接受后端、正式会话与隐藏目标验证（2026-10-28）

用户明确接受 ADR-011；状态改为 **Accepted（后端选择已接受，呈现及产品验收待完成）**，根技术表同步为 `vo=libmpv + D3D11 Render API + Renderer-owned DComp/交换链`。C0 wid/gpu-next 保留为历史诊断，不进入正式静默回退。跨进程仍只传 Host 容器 HWND，协议 v1/DTO/Schema 不变。首帧只证明当前目标有效提交，不承诺物理扫描，隐藏 provisional 可以先准备再激活。

正式 `LiveWall.Renderer.Video.Program` 已替换占位：接受 Pipe/SessionId，读取并清除一次性认证环境变量，使用相同受限 Named Pipe/长度帧源码，执行 Hello → Initialize/Initialized。固定哈希后加载开发 DLL，检查实际 client/render exports 与 D3D11 context；不从全局 PATH 选择 libmpv。初始化不返回半可用状态。仅引用 Contracts 产品项目，通用 IPC 原语由 Infrastructure 维护并源码链接；Vortice 使用项目已固定 3.8.3。Renderer 私有 NativeMethods 单文件只包含自有 child/消息泵、Pipe server PID 与进程退出顺序，边界脚本增加精确文件白名单，没有放开其余目录或引入 Shell API。

`VideoRendererSession` 的单接收泵验证版本/session/messageId/payload，投递到 32 项有界队列，控制 actor 唯一修改事务状态/发事件；每连接命令 ID 上限 65536，耗尽失败关闭，不淘汰后重放。JSON 严格校验数字类型和属性大小写，不将字符串数值强转为 generation/协议版本，未知附加字段仍可忽略。初始化及阶段预算各 10 秒。有效 Shutdown 可抢占 Initialize/Attach/Load/首帧，清理完成才回执；迟到有效提交也被取消门禁丢弃。清理失败不能发送 ShutdownCompleted，Dispose 不重复。空闲时控制 actor 检查 client 健康，GPU worker 错误也能终止会话，不必等待新命令。native 调用不合作时仍依赖 Host 最终自有进程预算，不将 CTS 称为硬终止保证。

`MpvVideoBackend` 的专属 STA render 线程拥有持久 D3D11 device/immediate context/Render Context 及 HWND 消息泵，每 binding 创建全新 child/DComp target/交换链。parent 必须属于当前 Pipe server PID；窗口 noactivate/tool/disabled，实际 client 尺寸与票据核对。普通 client API 在控制侧；更新回调只唤醒，不反向等待。重附着不销毁 core/context，暂停时将当前有效视频帧重新 Render/Present/Commit；FrameSequence 是提交序列，不要求时间推进。首次/重附着同步使用 WaitForCommitCompletion，连续播放不逐帧同步或读回；Bounds 对既有帧变换并 Commit 后确认当前 Renderer generation。SetFit 白名单和 SetVolume 范围检查已接入，音频初始为零音量且静音；持续音频/性能能力仍未验收。

头文件补齐与哈希已记录在 `tools/libmpv-render-api.lock.json`：固定提交 e471496c56；render.h SHA256=`F2550AA29B2E9B20988BD954B4383E9FA05FD9E40D1669FBC6EDF2AFAC603F15`，render_d3d11.h SHA256=`7B880453EE3C780B0D0F1C3F8ECAA1D23B289522F4C687E6857C8475F4A3F6FB`。DLL/原视频哈希与 C0 一致，均仅在项目缓存，未做发行许可或硬解结论。

新增 **37/37** 项真实长度帧/Pipe + 假后端门禁。最终修正后的 Debug/Release 全量各 **520/520**（Domain 12、Application 263、Renderer Contracts 114、Package 23、Windows 108）；日志为 `artifacts/diagnostics/video-c1/c11-final-suite-{debug,release}-20261028.log`。实际原生实验与自动测试数分开：

| 隐藏容器场景 | 原始结果 | 结论 |
|---|---|---|
| 正常完整会话（最终严格 Wire） | `artifacts/diagnostics/video-c1/formal-strict-normal-5f7800f571ae4d129b551e4cdaa2a767.json` | valid=True；3 次首帧分别为初始/Bounds/暂停重附着；child 归属/parent/尺寸和回执前销毁通过，ShutdownCompleted、ExitCode=0、未强制结束、自有 Host/child 清理 |
| Attach 后提前 Shutdown | `artifacts/diagnostics/video-c1/formal-hidden-shutdown-after-attach-833fe013427f4faab6b6d91e8b281952.json` | valid=True；零首帧，正常回执/ExitCode=0，自有资源清理 |
| Attach 后 Pipe 断开 | `artifacts/diagnostics/video-c1/formal-hidden-disconnect-after-attach-8d963cb0f3b94d8c82f84effaa53c446.json` | 预期失败门禁 valid=True；Renderer ExitCode=1，未伪报正常 Shutdown，零首帧、无 child 残留 |
| 非法路径强类型拒绝 | `artifacts/diagnostics/video-c1/formal-corrected-invalid-path-efc173a5074248ef967c6bc2fbfb74fa.json` | 预期失败门禁 valid=True；ContentInvalidPath、零首帧、ExitCode=1、自有资源清理 |

加强拒绝码核验时保留一份失败 `formal-final-invalid-path-48d08c752c9d4def9c8c2cf630681caf.json`：路径已拒绝并清理，但被错误归为协议状态错误，valid=False。修正 Resolve 异常筛选遗漏 InvalidDataException，并让 8 个路径测试全部断言 ContentInvalidPath；上表新样本通过，旧样本不改写。更早宽口径样本也原样保留，不能改成已经通过后加的严格拒绝码门禁。

这些实验均没有访问/修改 Shell，desktopPresentationValidated/productionAttachmentEnabled=False；既有 WorkerW mutation 没有在本轮尝试恢复。**C1.1 代码与自动/隐藏目标验证完成，实际单屏桌面验收待执行，C1 整体继续 In progress。** 下一步应为显式、限时的正式 Video 桌面代表验收及产品候选组合；默认 Host Video Provider/仓库属于后续接线。Throttle/完整属性/遥测、持续资源/音频、硬解/拷贝、编码和环境矩阵仍未完成。C2/C3、发行审计、P1.9.2/P1.9.4 延期、Stage B `In progress / Not accepted` 和两个空生产 allowlist 保留。

### C1.2：正式 Video 产品候选入口就绪，单屏人工验收待批准（2026-10-29）

显式 `DesktopHostDiagnostics --video-product-candidate` 已接入同一 `RendererProductCandidateDiagnostics.RunAsync`：HostCommandLoop → SessionCoordinator → Raised 诊断 override/lease/Native Surface Factory → 正式 RendererProcessProvider/RendererProtocolSession → 正式 Video Program → 全部首帧 → 一次 Replace → 独立 retirement。通过强类型 VideoAssets 选择已固定的视频原生 fixture 与开发 DLL，默认色块候选不变；没有复制一套简化握手、改默认生产组合根或持久化实验兼容配置。素材/库检查先于桌面适配器请求，要求本地绝对非 reparse 路径、固定 manifest/权限/cover/mute、媒体与 DLL 哈希匹配；这不是 C2 通用导入或持久仓库。

新入口必须显式 `--confirm-desktop-presentation`；缺少时退出码 2，尚未读取 Shell、创建 Surface 或启动 Renderer。仅支持当前主屏单 Surface，显示时间从 Apply Succeeded/Replace 完成开始计算，默认 10 秒、上限 30 秒；准备阶段保留真实 Apply deadline，不引入 15 秒外层超时。该首轮是连续运动视频，不主动暂停、调整显示或重启 Explorer。Ctrl+C 触发取消，已提交资源也走独立 retirement。

只读 `--preflight-only` 不需要呈现批准，只校验资产、显示拓扑和 Shell 执行上下文。`ready=True` 只证明这些前提，attachmentValidationDeferred=True；它不发送 Raised 请求、不创建 Surface/Renderer，也不证明能力或像素。运行前、请求后和清理后的结构快照脱敏保存；Shell HWND/PID 仅在运行期参与 generation/WorkerW 身份比较，不写成持久身份。新增 WorkerW 残留仍返回 6/CleanupIncomplete，采样或身份不确定为 Inconclusive，不用 NoNewWorkerObserved 冒充恢复。自有清理与 Shell 恢复分开，原始 manualPresentation=NotYetConfirmed 不因后续反馈而改写。

`tools/run-video-product-candidate.ps1` 提供可审查的 Release 入口，不运行 dotnet run/MSBuild。只临时选择项目 .dotnet 运行时，不改全局 PATH、配置或启动项；正常/失败/取消后恢复三个进程环境变量。首次脚本检查发现 PowerShell 将原来的 null 恢复为空值，已改为对原本不存在的变量显式删除；未设置、空值、已有值三种恢复门禁均通过。该检查的原始只读结果保留，没有改写成可见实验。

新增 **26/26** 项自动门禁，覆盖呈现确认/只读分离、时限/参数、fixture Video 类型与权限、WorkerW 三基线/句柄复用拒绝、Attach/Content/FirstFrame 超时零提交和提交后取消清理。最终 Debug/Release 各 **546/546**（Domain 12、Application 289、Renderer Contracts 114、Package 23、Windows 108），日志 `artifacts/diagnostics/video-c1/c12-suite-{debug,release}-20261029.log`。诊断工具 Release 构建零警告/错误，架构边界及差异检查通过；正式 Renderer DTO/Schema/协议版本均不变。

真实产品链的隐藏容器预检：`artifacts/diagnostics/video-c1/product-chain-hidden-418a4852c3d54abf93ddc36eb8b83c24.json`，SHA256=`7A1B3AA2B63A0D1BCC78CA883F71A84C66089CCD6C62DDE397BC746FB10A01B3`。真实 Host 编排/Provider/Video 进程通过，Apply generation=1、Succeeded、首帧后 replacementCount=1；Apply 耗时约 551.4 ms（单次记录，不设 SLA），ShutdownCompleted=True、Renderer ExitCode=0、Graceful/OwnedResourcesCleaned=True，Host Surface 已销毁。只替换了 IDesktopHost 为不可见自有容器，**没有验证 Raised lease/Factory 或桌面像素**；attachmentBackendValidated/desktopPresentationValidated/productionAttachmentEnabled=False。

最终只读预检：`artifacts/diagnostics/video-c1/desktop-readonly-final-15be9a39b86f44ee998ea4ce9a2abb41.json`，SHA256=`3495F16EEC273553372046229D566EDE19F2D724211F5DB14633DAA2D0144542`。当前为 `26200.9711 / session=14 / WinSta0\Default`，物理单屏 2880×1800、200%；ready=True、attachmentValidationDeferred=True，shellRequestSent/surfaceCreated/rendererStarted=False。这不是“0 候选”或可见测试结果。尚未执行带确认的实际入口，等待用户批准约 10 秒的连续视频代表验收。

C1.2 在 2026-10-29 的状态为代码/自动/隐藏产品链完成，可见单屏验收待批准；随后首轮结果如下。C1 整体仍 In progress，默认生产 Video Provider/持久库及 C2/C3 未启用。

#### C1.2 首轮可见实验：提交成功，进程退出失败（2026-11-02）

用户明确批准后仅执行一次主屏 10 秒连续视频实验，环境为 `26200.9711 / session=17 / WinSta0\Default / 2880×1800 / 200%`。原始证据为 `artifacts/diagnostics/video-c1/c12-desktop-direct-20261102-0e8f762bf9e447f2814f4af51a2acd16.json`，SHA256=`461B1A5BC6B6B1D37FB58329AE97AAE2D6451A4AAF57D4EE10E233035F475A20`。Apply generation=1 Succeeded，FirstFramePresented 约 1333.3 ms，唯一一次 SurfacesReplaced 约 1354.1 ms；提交后计时 10 秒。ShutdownSent/ShutdownCompleted/RendererDisposed/SurfacesCleaned 均为 True，retirement 约 385.9 ms，自有窗口为零、进程已退出，但保留的实际进程句柄读得 ExitCode=-1，最终 `CandidateFailed / exitCode=8`。人工画面尚未确认，原始 `manualPresentation=NotYetConfirmed` 不改写。

该版本的 `retirement.graceful=True` 只表示收到 ShutdownCompleted；外层 `ownedResourcesCleanup=Incomplete` 又混合了非零退出码条件，不能从这些旧字段推断仍有自有窗口。后续诊断已独立报告自有资源清理和 `gracefulRetirement`：资源已销毁、窗口为零、全部进程退出才算自有清理完成；还须回执及全部实际退出码为 0 才能通过正常退休。非零退出仍返回 8，不放宽验收；本轮旧 JSON 保持原样。

Shell 请求前直接子 WorkerW=0、退出后=1，新增 1 个 Shell-owned WorkerW，`ShellMutationRecovery=CleanupIncomplete`。没有发送逆消息、销毁 Shell 窗口或重启 Explorer；自有清理不替代 Shell mutation 恢复。首次 `.ps1` 启动被当前 Windows PowerShell 执行策略拒绝，发生在脚本执行及 Surface/Renderer 创建之前，单独保存 launcher 记录；未修改执行策略，改为直接运行同一已构建 Release exe，此前失败不算额外桌面样本。

受控 Pipe 测试在修复前可稳定复现：ShutdownCompleted 后延迟 50/150 ms 正常返回的进程，仍被 Host Dispose 强制终止。修复将有效回执与进程终止分开：已回执进程有独立最多 1 秒正常退出等待，逾期强制终止后再有限等待；未回执或被 abandon 的会话不获得正常退出宽限。等待属于既有 Renderer Dispose 预算，不链接 Apply deadline。重复/并发 Dispose 等待同一完成任务，进程句柄只释放一次；不改变 DTO、Schema、协议版本或生产装配。

新增 7 项退出门禁及 6 项清理分类门禁；Debug/Release 各 **559/559**（Domain 12、Application 302、Renderer Contracts 114、Package 23、Windows 108），日志为 `artifacts/diagnostics/video-c1/c12-exit-fix-suite-{debug,release}-20261102.log`。修复前失败日志单独保留。本轮修复不关闭 C1.2 可见验收，修复后不得自动重播；C0 原始事实、Shell mutation 门禁、P1.9.2/P1.9.4 延期、Stage B `In progress / Not accepted` 和两个空生产 allowlist 不变。

修复后的真实隐藏产品链证据：`artifacts/diagnostics/video-c1/product-chain-hidden-exit-fix-built-20261102-8e2ea296d0b347b8b024b3e80233eade.json`，SHA256=`E1BFB1C82EA7A2C7D9633B4DFDB61D75FDA5C105514F6786B5D6FEDDEC7937D7`。valid=True、replacementCount=1、ShutdownCompleted=True、实际 Renderer ExitCode=0、Graceful/OwnedResourcesCleaned=True、Host Surface 已销毁；retirement 约 365.6 ms。该复核没有触碰 Shell 或显示桌面视频，desktopPresentationValidated=False；不替代可见复测。

首次隐藏复核 `product-chain-hidden-exit-fix-20261102-00a510e1c47e48bc8bcaf727e92555a7.json` 为 PipeConnected 握手超时、零 Replace、隐藏 Host Surface 已清理，SHA256=`72193D7C73839705BF7B7D0EFD867D17F7089206C23BE2B41D6CA848AC9D456F`。所选 x64 Release Video DLL 当时仅 4608 字节，属于未显式重建的旧输出；全量 test 成功不保证非测试可执行文件已更新。随后显式构建 DesktopHostDiagnostics 及其正式 Video 依赖，DLL 更新至 99840 字节，再得到上列成功记录。失败原样保留，不将该次超时改写成退出修复通过。诊断工具和隐藏探针 Release 显式构建均零警告/错误，项目引用、P/Invoke、Renderer 格式边界复核及 git diff --check 通过；当前脚本执行策略未调整。

2026-11-03 用户补充“确实完整覆盖”，因此将上述 2026-11-02 首轮实验的**主屏四边覆盖人工项登记为 Passed**。本条反馈仅确认覆盖，未将连续运动、图标/任务栏/普通窗口层级及结束恢复补写为逐项通过，也不视为新的可见实验授权。原始 JSON 的 manualPresentation、CandidateFailed/退出码 8 与 SHA256 保持不变；Renderer ExitCode=-1 和 Shell WorkerW CleanupIncomplete 仍按原始证据保留。此次只补录反馈，没有重新播放视频或运行自动测试；修复后的 10 秒主屏可见复测待明确确认。

#### C1.2 修复后单屏可见复测：呈现与正常退休代表项通过（2026-11-03）

用户单独批准后，先执行只读预检，再执行且仅执行一次主屏 10 秒正式 Video 产品候选。环境为 `26200.9711 / session=1 / WinSta0\Default`，物理单屏 2880×1800、200%；使用已显式构建的 x64 Release 诊断工具及正式 Video Renderer，固定 fixture/libmpv 不变。只读结果为 `artifacts/diagnostics/video-c1/c12-retest-preflight-20261103-52167a945faf489d8f2bfe9544589ef9.json`，ready=True、connected displays=1；预检本身没有发送 Raised 请求或创建 Surface/Renderer。

实际复测证据为 `artifacts/diagnostics/video-c1/c12-desktop-retest-20261103-52167a945faf489d8f2bfe9544589ef9.json`，SHA256=`C07AAA562F64EEA0E538AC274D27B3EF8709362438788522FC88F25AA2648C43`。Apply generation=1、Succeeded，FirstFramePresented 约 1340.7 ms；全部首帧后唯一一次 SurfacesReplaced 约 1361.5 ms，随后显示 10 秒。正常退休约 407.8 ms，ShutdownSent/ShutdownCompleted/RendererDisposed/SurfacesCleaned=True、issues=[]，实际 Renderer ExitCode=0，独立 gracefulRetirement=True、OwnedResourcesCleanup=Complete，自有窗口与实验进程均为零；Shell generation 和显示拓扑均未变化。这关闭了本单屏代表路径中“回执后过早终止进程”的可见复测项，不能将旧 ExitCode=-1 追改为成功。

工具返回后，用户对“四边完整覆盖、青色条连续移动、图标/任务栏/普通窗口始终在上方、结束后恢复原壁纸”明确选择“全部正常”。因此本次正式协议 v1、Renderer-owned libmpv D3D11 Render API、真实 Raised Factory 产品候选的**单屏呈现与正常退休代表项通过**。反馈只补入台账，原始 manualPresentation=NotYetConfirmed 不改写；没有将结果扩大为双屏、暂停/重附着、性能/硬解/编码矩阵或默认生产内容链通过。

Raised 请求前直接子 WorkerW=0、退出后=1，新增并遗留 1 个 Shell-owned WorkerW，ShellMutationRecovery=CleanupIncomplete；因此整轮保持 `CandidateRunCompletedWithShellRecoveryIssue / exitCode=6`，不能记作完整 Shell 恢复或 ValidSuccess。没有销毁 Shell 窗口、发送逆消息、重启 Explorer 或自动重播。C1 整体 In progress、P1.9.2/P1.9.4 延期、Stage B `In progress / Not accepted`、两个空生产 allowlist、默认 Video Provider/持久库及 C2/C3 待接线继续保留。此次没有修改代码或 Renderer v1 DTO/Schema，也没有重跑全量测试；559/559 为上次修复的 Debug/Release 基线。

### C1.3：正式 Video 控制与重附着，代码/自动/隐藏验证完成（2026-11-03）

不重复 C1.2 的连续播放实验；本轮没有显示桌面视频、操作 Shell 或改变显示设置。新增工具入口 `VideoPresentationProbe --host-control-preflight <formalVideoExe> <dll> <fixtureRoot> <freshDirectory> <scenario>`，复用正式 HostCommandLoop、SessionCoordinator、Provider/RendererProtocolSession 和独立 Video Program，仅 IDesktopHost 使用自有隐藏容器。场景为 controls、load-failure、first-frame-timeout、process-exit、pipe-disconnect。

控制 actor 对 Fit、Volume/Mute 和播放状态实际执行后读回 libmpv 状态；Attach/Bounds/重新提交时验证既有设置仍保持。诊断 composition root 仅给本轮子进程设置 `LIVEWALL_VIDEO_CONTROL_EVIDENCE`，Renderer 读取后立即删除环境项；按 CreateNew 写入全新本地非 reparse 路径，最多 512 条只读观测，记录单调耗时、媒体时间、暂停和画幅/音量状态。不记录令牌、Pipe 名称、媒体路径、PID/HWND，不增加 DTO/Schema/遥测能力；默认播放没有该日志或周期采样。

修复三处控制/生命周期边界：后端忽略取消并迟到完成 Playback 时，再检查停止 token，禁止 Shutdown 后回传成功；Host 仅接受匹配已发送命令的播放反馈，旧 generation 的已知迟到控制回执不能放行新 binding 的阶段；正式会话通过独立 ActiveFailure 完成任务向 Host 单写者转发活动期错误，不增加第二个事件消费者。当前活动正式 Renderer 失败后保留 Assignment、移除失效运行会话并有界退休；恢复事务仍拥有共享 Renderer 时，由既有整批恢复路径处理，避免重复清理。

新增 11 项自动门禁（Application 4、Renderer Contracts 7）；最终 Debug/Release 各 **570/570**（Domain 12、Application 306、Renderer Contracts 121、Package 23、Windows 108）。日志为 `artifacts/diagnostics/video-c1/c13-final-suite-{debug,release}-20261103.log`。普通 Release 诊断/Video 输出及 x64 隐藏探针已显式构建，均零警告/错误，避免把旧占位或旧 Host 输出误当最新构建。

五类正式进程隐藏证据汇总：`artifacts/diagnostics/video-c1/c13-evidence-index-20261103-7ef2353a55a34b40a8b733275df8f304.json`，SHA256=`43944B61ACB290298895940C7D9CB1FECF418EAC24EE37A1545C706E90820070`。索引保存各原始结果路径与哈希，不改写旧结果；所有有效案例的隐藏 Host 窗口已清理，最终无 Video/Probe 进程残留，shellTouched/desktopPresentationValidated/productionAttachmentEnabled=False。

| 隐藏场景 | 实际证据 | 有效范围 |
|---|---|---|
| controls | 暂停期间媒体时间增量 0；Resume 后推进约 0.5 秒；同一 Renderer 在 synthetic Host recovery 中得到全新 parent/child，模式 Reattached；用户暂停意图及 Fit/Volume/Mute 保持，最终真实 ExitCode=0 | 正式 Host 编排及实际媒体/设置读回；不等于真实 Explorer 重启或可见恢复验收 |
| Bounds / Fit / Volume | 640×360 child 适配、确认仍为当前 generation=1；四种 Fit 映射读回，音量 0/37/83 与静音变化读回；恢复到新 binding 后仍为 Contain、83、Muted | 不承诺主观听感、音轨/编码或全部可见画幅矩阵；原始 fixture 无音轨 |
| load-failure | 真实无效本地 AVI 导致 ContentLoaded 阶段失败；新批次零 Replace、旧 Session/Assignment 保持，后续 Apply 成功；真实进程退出码 0/1/0 | 正式媒体加载失败与普通替换回退 |
| first-frame-timeout | 正式 Renderer 已提交真实首帧，由诊断 IRendererSession 投递过滤器只扣留第二次 Apply 的 FirstFrame 事件；Host 在 FirstFrame 阶段超时，旧会话保持，后续 Apply 成功；真实退出码 0/0/0 | Host 首帧等待/回收门禁；**不是** libmpv/D3D 挂死样本，也不伪称原生提交失败 |
| process-exit | 只终止本轮自有活动 Renderer；实际退出码 -1，Host 自动接收 renderer.process.exited，活动 Session=0、Assignment 保留、自有清理成功 | 故障退出，不伪造 ShutdownCompleted 或正常退出码 |
| pipe-disconnect | 关闭本轮 Host 通道，Host 获得明确失败并清理；实际退出码 -1，观测到 Host Terminate=1、句柄 Dispose=1，无 ShutdownCompleted，清理完整 | 断连允许强制结束未回执进程，不将其记作正常退休 |

所有有效故障诊断只在预期失败及清理不变量同时成立时返回 0/ExpectedFailureValidated；实际 Renderer 非零退出码单独保留。首次 process-exit 驱动结果因没有区分 Host 的预期后台失败而记为 valid=False；随后增加 ActiveRendererFailureException 强类型分类，仅接受已验证的该类故障，其他关停异常仍失败。首次 pipe-disconnect 驱动错误要求自然退出码必须为 1，而未回执进程可能被 Host 正确强制结束为 -1；新增真实 Terminate/Dispose 计数后按非零实际退出、无虚假回执与完整清理验收。两份失败路径都在索引 failedSamplesPreserved 中保留，没有将失败覆写或放宽正常退休的 ExitCode=0 要求。

能力边界：Initialize、Attach、Load（空 properties）、Play/Resume/Pause/Suspend、SetBounds、SetFit、SetVolume/Mute、Shutdown 已接入；Throttle、SetProperties、SetAudioFrame、CapturePreview 和连续遥测未实现，未支持的命令以明确错误拒绝，不返回成功。C1.1 的票据/旧尺寸拒绝、暂停新目标提交和准备阶段超时门禁沿用，不重复计算为新原生样本。

以上为 C1.3 初次代码/自动/隐藏验证的状态。随后用户明确要求开始验收，新增并验证了下列共用阶段驱动，完成本轮可见代表项；未重复 C1.2 连续播放实验。

#### C1.3 单屏控制代表验收通过，C1 有限范围关闭（2026-11-03）

显式入口为 `DesktopHostDiagnostics --video-product-candidate --control-recovery --confirm-desktop-presentation ...`，wrapper 对应 `tools/run-video-product-candidate.ps1 -ControlRecovery -ConfirmDesktopPresentation`。固定 Playing、Paused、ReattachedPaused、Resumed 四阶段各约 5 秒，总观察 20 秒，不放宽通用 30 秒上限；若显式给定 duration 必须为 20。每阶段从真实协议反馈/首帧就绪后计时，人工观察等待不受旧 Apply deadline 约束。缺少呈现确认仍拒绝；只读预检不创建 Surface/Renderer。

`VideoControlCandidateDiagnostics` 共用正式 Host 单写者和 Renderer 会话，Pause/Resume 等待当前 generation 的实际反馈；重附着采用诊断合成 Host 恢复命令，创建全新容器/child 后按首帧屏障替换，保留同一 Renderer 和暂停意图。**未重启 Explorer，也没有将此项宣称为真实 Shell generation 恢复测试。** 先用相同驱动完成隐藏预检：`artifacts/diagnostics/video-c1/c13-stage-plan-hidden-20261103-9dd686e11de04ee6a43404750b44dbc0/result.json`，SHA256=`DA104453898EE1523ECE90670E6AE95CE868636C8BE2C5886E60B14F4614D418`，四阶段、fresh Surface、同 Renderer、正常 ExitCode=0 和完整清理通过。

随后仅执行一次获准可见实验，环境为 `26200.9711 / session=1 / WinSta0\Default / 2880×1800 / 200%`。证据：`artifacts/diagnostics/video-c1/c13-control-desktop-20261103-54b77824c4024de5910cbc9785a6d098.json`，SHA256=`148D736081076B6BCB314425BA0E1B5517C1675A4AD7D00E31A5010862E53B43`；同名 `.json.native.jsonl` 为限长只读 libmpv 观测。四阶段实际保留约 5.015/5.006/5.001/5.001 秒；Host generation 1→2，Reattached 成功约 750.1 ms，同一 Renderer、全新 Host/child 已验证。41 个暂停样本的媒体时间增量为 0；恢复后 20 个样本确认时间再次推进，暂停当前帧在新目标上重提，设置保持。

最终 ShutdownSent/ShutdownCompleted/RendererDisposed/SurfacesCleaned=True、issues=[]、retirement 约 364.7 ms，真实 Renderer ExitCode=0、gracefulRetirement=True、OwnedResourcesCleanup=Complete，无实验进程或 LiveWall HWND 残留。Shell WorkerW 为 1→1、addedCount=0，三基线结果 ShellMutationRecovery=NotRequired，整轮 `CandidateRunCompletedAwaitingManual / exitCode=0`。该分类只说明**本轮没有新增 mutation**；此前 C0/C1.2 新增 WorkerW 的 CleanupIncomplete 及 Platform 生产门禁继续保留，不能追改为已恢复。

用户对“先移动、停止约 10 秒（中间重附着但保持同一暂停画面）、再恢复移动、最后恢复原壁纸，全程覆盖及图标/任务栏/窗口层级正常”明确反馈**全部符合预期**。因此 C1.3 的单屏控制/重附着/正常退休代表项关闭。反馈只补台账，原始 JSON 的 manualPresentation 与 classification 不改写；没有自动重播或追加 Explorer 实验。

新增 3 项阶段入口参数/批准/固定时长门禁，最新 Debug/Release 各 **573/573**（Domain 12、Application 309、Renderer Contracts 121、Package 23、Windows 108），日志 `artifacts/diagnostics/video-c1/c13-acceptance-final-suite-{debug,release}-20261103.log`。Release 普通输出和 x64 工具已构建；正式 Host 入口、DTO/Schema/协议版本不变，新增控制参数只属于诊断程序，正式 Host Program 不接受实验开关。

wrapper 的 `-ControlRecovery -PreflightOnly` 另在受限 `WinSta0\CodexSandboxDesktop-*` 上下文运行，记录 `video-product-bfe5442f55cb4560b9afc3a02f69c44b.json`：正确识别 ShellUnavailable、ready=False/退出码 3，controlRecovery=True，未发送请求或创建 Surface/Renderer；三个进程环境变量恢复通过。该只读环境受限结果与上列 WinSta0\Default 可见样本分开，不解释为 Windows 无候选或产品回归。架构边界脚本（10 项目）和差异检查通过。

| C1 约定收口项 | 结论 |
|---|---|
| 正式 v1 握手/阶段、Renderer-owned Render API 与目标提交证据 | 代码、自动及原生隐藏验证通过 |
| 正式产品候选单屏可见首帧、首帧后 Replace、正常退休 | C1.2 代表项通过并关闭，旧失败保留 |
| 实际播放控制、暂停新 binding、Bounds/设置保持及退出交错 | 隐藏门禁通过，C1.3 单屏可见代表项通过并关闭 |
| 加载失败、Host 首帧超时、活动崩溃与 Pipe 断开 | 五类隐藏证据按声明范围通过；首帧扣留不冒充 GPU 挂死 |
| Throttle/完整属性/音频帧/截图/连续遥测 | 首版 C1 未支持，明确拒绝，不以实现扩展能力作为本切片关闭前提 |

**C1 开发与上述有限单屏代表验收关闭；Stage C 整体尚未完成，下一步为 C2 最小持久库/正式 Host 内容恢复。** C2 尚未接线；双屏、真实 Video Explorer 恢复、长期资源、完整编码/硬解/听感/发行矩阵继续独立排期。Stage B `In progress / Not accepted`、Shell mutation 生产启用门禁、P1.9.2/P1.9.4 延期和两个空生产 allowlist 不变。

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
| P1.9.0 真实 Host 组合根与生命周期 | 已实现同步 `Program`、同线程单实例 Mutex、`WindowsHostCompositionRoot`、`WindowsHostRuntime` 状态机、启动恢复终态等待、启动后环境补采、幂等 Shutdown 和反序释放。Restore 先载入完整持久 Assignment，再只 Apply 在线显示器；失败恢复或离线显示器不会在关停持久化时丢失。Stopping 后拒绝外部变更，迟到 prepared completion 只能终态化并清理 provisional 资源 | 自动覆盖重复 Shutdown、停止后 Apply/拓扑/Explorer 拒绝、FirstFrame 后迟到完成零 Replace、完整 Assignment 保存、Runtime 启停/补采/持久化/一次性释放；随 P1.9.1 合并后的 Debug/Release 总基线为 `224/224`。Release 正式组合根 `--validate-startup` 返回 0 并生成 schema v1 配置 | **生命周期骨架与停止门禁完成。** 生产壁纸仓库和 Video/Web Renderer 产品入口仍未实现，默认组合根只允许加载/保留 Assignment 并失败关闭内容恢复；后续各切片以本表对应状态为准，P1.9.5 关闭不等于 Stage B 接受或 allowlist 放行 |
| P1.9.1 `TaskbarCreated` 门禁与真实事件链恢复 | 已实现内部 Explorer 监视端口、350 ms 验证与 2 秒后备链路、验证/转发失败强类型结果、Stopping 丢弃分类，以及只供诊断组合根使用的真实隐藏窗口信号注入和 Explorer 重启入口；默认 `LiveWall.Host` 不暴露实验开关 | 自动覆盖 10 次 burst 严格 No-op、验证异常、fallback outage 去重、真实 Bridge 转发、关停后丢弃和 Dispose 后零投递；Debug/Release 总基线 `224/224`。`26200.9457 / WinSta0\Default` 双屏 No-op 得到 `10 raw / 0 validated changes / 0 recovery / 0 Replace`。真实 Explorer PID `11908→37580` 后得到 `1 raw / 1 validated change / 1 recovery / 1 Replace / 0 duplicate`、终态 `Succeeded + Rebuilt`，退出码 0且无自有 HWND；人工确认两屏同步黑屏、Shell 恢复、同步橙色并还原，全程无半屏或覆盖 | **P1.9.1 关闭。** 本次为整批 `Rebuilt`，不能记作同 Renderer `Reattached`；黑屏属于显式终止 Explorer 的允许窗口。后续系统/并发延期项、生产内容入口和 Shell mutation 策略仍独立保留，Stage B 与两类生产 allowlist 不变 |
| P1.9.2 系统锁定、显示电源与睡眠恢复 | 已实现完整状态源、单调 revision、WTS/Power 聚合及显式 suspend/resume 通知注册/释放；解锁、点亮和唤醒验证环境后才恢复完整播放策略 | P1.9.2 修复基线 Debug/Release `246/246`，随 P1.9.3.0 最新整体基线 `262/262`。物理单屏基线、锁解 3/3、用户暂停锁解 1/1、S0 Modern Standby 睡眠恢复 2/2 通过，实际命令/回执及清理完整；首个未完成睡眠样本保留 | **自动及物理单屏代表场景通过；双屏系统恢复待补。** 不宣称 P1.9.2 全部关闭，不代表 RDP/用户切换或 Video/Web 动态帧循环通过 |
| P1.9.3.0 会话状态机与正式事件链 | 已实现 Session 匹配、断开提交门禁、准备取消、最新环境延后处理、revision 关联验证、独立验证预算及可等待会话环境终态 | Debug/Release `262/262`；自动覆盖重复、抢占、迟到结果、Shell/拓扑延后处理、FirstFrame 后零提交、暂停意图、失败/超时、Stopping、恢复取消及离线 Assignment | **本代码/自动切片完成；真实会话实机未验收。** 后续 P1.9.3.1 才进行 RDP/用户切换诊断与人工实验，协议和 allowlist 不变 |
| P1.9.5 退出竞态与系统结束清理 | 已实现共享 Stop/Dispose、停止后零新提交、后台任务排空、独立退休预算、原生有限等待与双回执、Host/Renderer 进程局部退出顺序及逐 Session 持久清理证据 | Debug/Release `428/428`，受控 Host `5/5`、无 Surface Renderer `2/2`、合成证据 `25/25`；单屏活动 Close、无 Renderer 真实注销及修正后的活动 Renderer 单屏真实注销均通过。最后样本真实收到 ShutdownCompleted、Renderer ExitCode=0、自有 Surface=0，人工确认注销前正常、登录后原壁纸 | **2026-10-24 按 P1.9.5-E 明示边界关闭。** 不扩展为真实关机/重启、双屏退出或全部产品矩阵已验收；旧失败样本保留，P1.9.2/P1.9.4 延期、Stage B 与空生产 allowlist 不变 |
| Platform.Windows attachment lease | 已实现 | 自动验证 Platform 内部 parent/anchor/backdrop/generation、无句柄能力描述及跨 generation 旧 HWND 不再被显隐/复用 | Shell HWND/PID 不进入 Application、Renderer、持久化或 allowlist；每个新 generation 必须重新发现 |
| Raised Desktop Surface Factory | 已实现生产形态接线 | 受控 HWND 测试验证 hidden provisional、`WS_EX_NOREDIRECTIONBITMAP`、`DefView > LiveWall > WorkerW`、显式批量激活和清理 | 只消费当前 generation 的内部 lease；不等于当前 build 已获生产支持 |
| Raised Desktop 生产适配 | 已实现发现和 Factory 调用路径，但 allowlist 关闭 | 自动结构/Factory 契约通过；尚未进行允许生产选择后的完整真实矩阵 | `ValidatedBuilds` 为空，生产 Selector 跳过；不得由诊断成功自动写入 allowlist |
| 产品入口与完整运行链路 | 已增加显式单次、soak、process-cold 批次、同 Host 循环和真实拓扑变化入口，均复用 HostCommandLoop、SessionCoordinator、生产 Surface Factory 与正式 RendererProtocolSession | 物理单屏、`26200.9168` 双屏 P1.6/P1.7，以及 `26200.9445` 双屏断开/重连、快速抢占和主屏切换通过 | 当前入口仍只用于显式诊断；剩余 DPI/bounds、会话和 Shell mutation 矩阵完成前不改变 allowlist 或 Stage B 状态；首次历史样本不得事后定性 |

因此 Stage B 的总体状态仍为 **`In progress / Not accepted`**。已经完成的机制不应再描述为“尚未实现”，但自动测试结果也不能替代真实桌面兼容性结论。

### P1.9.3.1 真实会话诊断入口（2026-10-11）

首版范围决定：**RDP 实机为 `OutOfScope / WaivedForV1`，不支持远程桌面动态壁纸呈现，不再阻塞首版或 P1.9.4。** 这是范围豁免，不是测试通过。通用会话状态机、WTS 事件、暂停/失败关闭策略和显式诊断入口保留；本地快速用户切换实机仍为 `NotValidated`，有现成安全环境时补测，不创建账户或更改远程服务来获取样本。

已实现 `--raised-desktop-host-session-reconnect`，复用正式 Host Runtime 和事件桥，等待与 reconnect revision 匹配的会话环境终态；不通过轮询 Session 数量推断恢复完成。armed 后人工预算独立为十分钟，工具不主动断开、切换用户、注销或配置 RDP。通知、验证终态、恢复路径、实际 Renderer 播放回执及清理结果可保存 JSON；锁屏、错误 Session、倒序通知、未知路径或失败终态不能作为成功样本。异常保留 failure phase、已获得的验证终态与通知，不假定清理成功。

该入口完成时的 Debug/Release 自动基线为 `277/277`，Release 工具零警告/错误，架构边界检查通过。**入口和自动证据门禁完成；真实会话样本未执行，不能写为实机通过。** 本机 `26200.9457 / EditionID=CoreCountrySpecific`；RDP 按首版范围豁免，本地用户切换保留 `NotValidated`，不再要求先取得 RDP 样本才推进 P1.9.4。所有人工实验仍需显式授权。P1.9.2 双屏系统恢复独立待补；协议、Schema、两个生产 allowlist 和 Stage B 状态不变。

### 当前开发与验收边界（2026-10-23）

**P1.9.4：代码与自动门禁完成，开发切片关闭。真实 Shell/显示拓扑并发及双屏实机验收为 `Deferred / Pending hardware`，生产验收门禁仍待关闭。** 该延期保留测试义务，不是 RDP 的首版范围豁免，也不是实机通过；不阻塞 P1.9.5 开发。下文 10 月 11 日的“尚未关闭”描述是当时历史状态，以本节口径为准。

### P1.9.5-B/C 退出通知与隔离进程验证（2026-10-23）

**P1.9.5 已按 E 节明示边界关闭（2026-10-24）：最后活动 Renderer 单屏真实注销重测通过，真实收到 ShutdownCompleted，Renderer ExitCode=0，自有 Surface 清理为零，双回执及配置保留通过，用户确认注销前正常、登录后原壁纸。** 首轮缺少 ShutdownCompleted 的失败及更早旧样本原分类不变，不以缺少 stdout 尾行推断 Main 返回或实际 Host 退出码。修正后最近一次 Debug/Release 全量 **428/428**（Domain 12、Application 263、Renderer Contracts 22、Package 23、Windows 108），工具构建零警告/错误；另有合成证据 25/25、受控 Host 5/5、无 Surface Renderer 进程 2/2。本轮仅核验真实注销证据和同步状态文档，不重跑或改写旧样本；此前 354/378/379/394/417 等数字为历史基线。P1.9.2/P1.9.4 延期、Stage B 与两类空生产 allowlist 独立保留，不追加重复注销义务。

已新增 Platform.Windows `Lifecycle` 独立 STA 隐藏窗口及控制台退出入口；Program 在 Restore 前创建，Runtime Stop 之后释放，不占用 Surface 销毁线程。查询消息立即返回 TRUE，取消结束不退出；确认结束、普通 Close、控制台关闭和卸载共享同一 Stop。Native callback 最多等待 4 秒，不阻止 Windows 关机；超时记录 TimedOut，不承诺系统会给足普通 retirement 预算。`HostProcessLifetime` 在 Runtime 清理后原子保存 `host-exit-receipt.json` 再完成共享退出任务；文件不含令牌、Pipe、HWND/PID，不修改配置或 Renderer Schema。缺失回执不能视为通过，回执存在也不能倒推已在 OS 强制终止期限内完成。

隔离 Release 正式 Host 的五项受控进程样本全部通过：Close、Query→取消→Close、shutdown/logoff/close-app 标志的确认消息。只发送给本轮启动且 PID 匹配的生命周期窗口，不广播；独立 installation scope 与项目 artifacts 配置目录，预置离线 Assignment，零壁纸 Surface。五次均退出码 0、自有窗口 0、Assignment 保留、退出回执匹配、无强制清理。最终证据：`artifacts/diagnostics/host-exit/batch-a18fa720d7db44248dcf196b91d95d3c/summary.json`。**分类为 `ControlledProcessGatePassed`，绝不是实际 Windows 注销/关机通过。**

`--renderer-exit-process-check` 在任何 Shell/显示器采样前执行，正式 Provider 启动真实 RendererChildProbe、完成 Hello/Initialize，分别验证 Shutdown/ShutdownCompleted 与 ForcedDispose；不发送 Attach/Load，所以没有 child HWND、DComp 或可见色块。两项均确认真实进程退出和句柄释放；ForcedDispose 的 ShutdownCompleted=False 被保留，不伪装优雅退休。证据位于 `artifacts/diagnostics/renderer-exit-process-check-20261023.json`。

原始失败样本保留：首轮驱动使用超长 installation scope，现已修正且 Program 在参数解析时拒绝非法 scope；后续正式 Host 已 Running 但生命周期标题不可达，定位为 Unicode CreateWindowEx 与 ANSI DefWindowProc 混用，改为 DefWindowProcW 并加入完整标题回归。这些旧失败不是当前五项通过样本，也不删除或回写为成功。

活动桌面入口 `--raised-desktop-host-exit-check --target-display primary --duration-seconds 5 --result-json <path>` 已编译，等待人工许可后运行：正式 Runtime/Renderer/Surface 提交橙色，计时后只向 LiveWall 自有生命周期窗口投递 Close，经共享 Stop 检查正常退休、真实 Renderer 退出、Surface/窗口与 Shell WorkerW 清理。不会注销或重启 Explorer。真实注销样本须先保存工作，经用户确认启动独立观察进程，再由用户手动注销/重登录；缺少 OS 实际通知样本或回执时继续保留未验收状态。两类生产 allowlist、Renderer v1/DTO/Schema 和 Stage B 不变。

#### 活动 Surface 首次 Close 样本与诊断竞态（2026-10-23）

经用户授权，在 `26200.9711 / session=8 / WinSta0\Default / 单屏 2880×1800 / 200%` 执行一次 5 秒活动退出检查。原始证据 `artifacts/diagnostics/active-host-exit-20261023-223042.json` 保留为 `ProductFailure`（退出码 7），不回写成功。1 个 Session 正常完成 Shutdown/ShutdownCompleted、Renderer Dispose 和 Surface cleanup，真实 Renderer 已退出且句柄释放；无自有窗口、无新增 WorkerW，Shell 保持稳定。用户随后确认“确实全程正常”：橙色显示及退出、图标/任务栏/普通窗口层级和原壁纸恢复的人工观察通过。该确认不替代失败的退出原因/回执校验，也不视为修复后可见重跑或真实注销的授权。

失败项是 `exitReason=DuplicateAfterNativeClose / receiptVerified=False`：诊断回调在登记原生 WindowClose 停止任务前就通知等待者，等待者发起的重复请求可能抢先成为退出原因。现改为先登记原生请求，再发布对应的共享完成任务；等待该任务并校验重复请求返回同一任务。新增回归覆盖观察到通知时原生原因已固定、重复请求不能抢占，以及退出完成必须等待回执写入。修复不改变生产会话语义、Renderer 协议或 allowlist；修复后的可见实验仍须单独获准重跑，本次不能关闭活动 Surface 验收。

修复后 Debug/Release 全量均为 **379/379**（Domain 12、Application 233、Renderer Contracts 22、Package 21、Windows 91）；Release 诊断工具构建零警告/错误，架构边界与差异检查通过。上文 `378/378` 为首次活动样本之前的历史基线。

#### 活动 Surface Close 修复后复测（2026-10-23）

用户确认首次视觉全程正常并明确允许重跑后，在同一 `26200.9711 / WinSta0\Default / 2880×1800 / 200%` 单屏环境完成一次 5 秒复测。证据 `artifacts/diagnostics/active-host-exit-retest-20261023-223803.json` 为 `ValidSuccess`，退出码 0：`nativeCloseObserved=True / exitReason=WindowClose / nativeWaitOutcome=Completed / receiptVerified=True`；1 个 Session 的 retirement 为 28.6941 ms，Shutdown、ShutdownCompleted、Renderer Dispose、Surface cleanup 全部成功，issues 为空。真实 Renderer 退出及句柄释放已确认，自有窗口为零、新增 WorkerW 为零，Shell 稳定。

该复测的自动验收通过；用户随后确认“和上次一样正常”，本轮橙色约 5 秒、图标/任务栏/普通窗口层级及原壁纸恢复的人工观察通过，**单屏活动 Surface 正常 Close 退出验收关闭**。首轮退出码 7 及其原始记录继续保留。该项只验证 LiveWall 自有窗口 Close，不是实际 Windows 注销/关机，也不修改 Stage B 或生产 allowlist；后者仍需独立授权及真实 OS 事件证据。本次确认不授权工具注销、关机或重启 Explorer。

#### 真实手动注销观察已准备（2026-10-23）

用户同意开始真实会话结束实验后，通过 `tools/run-host-session-end.ps1 -Mode Arm` 在 `26200.9711 / session=8 / WinSta0\Default` 启动一个独立 Release 正式 Host，检查到 `HOST_RUNNING`、本轮观察进程恰为 1、stderr 为空且尚无退出回执。专用证据目录为 `artifacts/diagnostics/host-session-end/run-cda178f4bff24080ac1ebd19154be50f`，当前仅为 `ArmedAwaitingManualLogoff`，不是注销通过。

本轮不创建壁纸 Surface/Renderer、不覆盖用户配置、不主动调用注销/关机、不注册自动启动；独立配置预置一个离线 Assignment。由用户保存工作后手动“注销”并重新登录，再执行 `tools/run-host-session-end.ps1 -Mode Verify -RunDirectory <上述绝对目录>`，核对真实 SessionLogoff 回执、Stopped/Cleanup、HOST_STOPPED、Assignment 与播放策略保存。缺失回执或退出日志只能记未完成/失败，不能靠新登录清空旧进程的事实推定应用优雅清理通过。只锁屏/睡眠不算样本；工具不设置操作倒计时，用户可从容保存工作。

#### 真实手动注销首样本：清理回执通过、退出尾声未证实（2026-10-23）

用户确认通过开始菜单用户头像下的“注销”完成注销并重新登录，而非锁屏。核验上述唯一目录得到 `verification.json: IncompleteOrFailed`（脚本退出码 1）。真实回执于本地时间 `2026-10-23 22:45:55 +08:00` 写入，`reason=SessionLogoff / runtimeStopped=True / cleanupSucceeded=True / failureType=null`，时间位于本轮 armed 之后；完整离线 Assignment 与播放策略文件保存通过，stderr 为空。stdout 只有 `HOST_RUNNING`，缺少 `HOST_STOPPED ... windowWait=Completed ... exitCode=0`，因此 `processExitLogged=False`。真实通知、Runtime 清理及配置保留有正面证据，但不能推断 Main 最后阶段、native wait 结果或进程退出码已证实，整体不改判通过。

微软 `WM_ENDSESSION` 文档规定会话可在应用处理该消息并返回之后随时结束，控制台文档也说明系统结束期间控制台函数可能不可靠；因此 stdout 尾行不应成为今后唯一的系统退出尾声观测点。当前缺失与该时序相容，但尚不能证明其具体原因，不能据此排除末段退出未完成或宣称 Windows 已造成日志丢失。下一步应先设计并验证回调返回前的有界持久确认，与 Runtime 清理回执分层，再讨论后续实机补测；不删除或放宽本次预先冻结的失败条件，不自动再次注销。参考：https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession 、https://learn.microsoft.com/en-us/windows/console/setconsolectrlhandler 。

本样本不创建活动 Surface/Renderer，不能与正常 Close 色块结果合并宣称“携带活动 Renderer 的 OS 强制结束”已验收。此次仅核验并记录，不修改生命周期代码、验证脚本、协议、生产 allowlist 或 Stage B 状态，不启动任何新实验。

#### P1.9.5-D 原生回调分层持久观测（2026-10-23）

按用户同意补足代码，Platform 的独立退出监视器新增可选强类型 observation writer，Host 在真实 Program 装配；仍不把 IPC、Assignment 或文件存储下放到 Platform。先等待共享 Runtime Stop（含 `host-exit-receipt.json`），再在原生处理路径记录 cleanup outcome，并原子写入单独的 `host-native-exit-receipt.json`。两个文件用 Host 本轮 runId 关联，不保存 HWND/PID/令牌。整体仍是同一个最多 4 秒预算，预留最多 500 ms（小预算为四分之一）给观测写入；清理超时不取消共享 Runtime 清理，写入等待过期只取消该写入。同步阻塞的回调在工作线程执行，不能卡住原生线程；任意更晚的重复通知不重启预算、不重写第一原因、不重复写入，也不把首次超时改成 Completed。AtomicFileWriter 在提交前再检查取消；不保证能强制中断不合作的存储/回调或任意底层 I/O。

新文件的阶段明确为 `NativeCallbackCleanupObserved`：证明本轮 native 处理路径观察到的清理状态及持久证据，不是 Main 返回、native callback 已经返回或实际 OS 退出码的证明。记录中 `CallbackReturnObserved=False / ProcessExitObserved=False`；文件无法独立证明它自身写完之后的执行时序。生产 Program 若观察到 native 整体等待 Failed/TimedOut，正常收尾时仍返回非零，不伪装正常退出。

此为**前瞻式证据契约 v2**，不是追改上次 stdout 门禁：新的 Arm manifest 标注 `nativeExitEvidenceVersion=1` 并保存 Host executable SHA256；Verify 校验同一 runId、原因、阶段、时间/预算、Runtime 状态、配置及完整 native 文件，输出 `verification-v2.json / SessionLogoffCallbackEvidenceValidated`。该分类只代表 callback 清理证据，`processExitCodeObserved` 仍独立报告，不能暗示已观察整个进程优雅返回。旧 manifest 继续执行原 stdout 门禁，首个真实样本仍为 `IncompleteOrFailed`；未知版本拒绝降级。真实新样本必须再次获得用户许可，本轮未 Arm 或注销。

验证完成：Debug/Release 全量均 **394/394**，新增 15 项门禁覆盖写入顺序、同步/异步失败、写入阻塞/超时、共享清理不中断、并发通知、首次原因/超时锁定、Host 双回执关联与取消提交。`tools/test-host-session-end-evidence.ps1` 的 **13/13** 合成证据门禁通过，包括 v2 无 stdout 尾行但双回执有效、旧样本仍失败、缺失/错配/超时/错误状态/未知版本拒绝；证据位于 `artifacts/diagnostics/host-session-end/fixtures-61b540297a3d4739b547a843bad85e6a/summary.json`。五项受控正式 Host 进程 **5/5** 通过，每项双回执关联、真实退出码 0、Assignment 保留、自有窗口为零且无强制清理；证据位于 `artifacts/diagnostics/host-exit/batch-bcfdde3e28b840b8ad297229c6b40da7/summary.json`。它们仍是受控消息，不是 OS 实际注销通过。Renderer v1、DTO/Schema、Stage B 和两个生产 allowlist 不变。

#### 双回执真实注销补测已就绪（2026-10-23）

用户再次同意继续后，已启动新的独立 Release 正式 Host，证据目录 `artifacts/diagnostics/host-session-end/run-1a8d73f998984bb5baf8eeed0d7332e8`。运行上下文为 `26200.9711 / session=9 / WinSta0\Default`，manifest 标注 `nativeExitEvidenceVersion=1`；已复核 `HOST_RUNNING`、stderr 为空、匹配本轮目录的观察进程恰为 1、Runtime 与 native 两份退出回执均尚不存在。状态仍为 `ArmedAwaitingManualLogoff`，不是通过；只等待用户保存工作后通过开始菜单用户菜单手动注销并重新登录，不显示色块、不注册自动启动或主动结束会话。

重新登录后使用 `tools/run-host-session-end.ps1 -Mode Verify -RunDirectory E:\project_c\dynamic_wallpaper\artifacts\diagnostics\host-session-end\run-1a8d73f998984bb5baf8eeed0d7332e8` 核对本轮 v2 双回执和配置，结果写入本轮 `verification-v2.json`。旧样本 `run-cda178f4bff24080ac1ebd19154be50f/verification.json` 原始 SHA256 仍为 `015E39D8577EA898C8D8DAE0C95626DDF86B3218F221273C85416CE116C5771B`，未改写。两个样本不得混用，也不能用会话重建后的进程消失推断 graceful exit。

#### 双回执真实注销清理代表场景通过（2026-10-23）

用户确认已经注销后重新登录，第三方动态壁纸在登录后自启动且已由用户关停。只读核验本轮 `run-1a8d73f998984bb5baf8eeed0d7332e8` 得到 `verification-v2.json: SessionLogoffCallbackEvidenceValidated`，**核验脚本退出码 0**。这是核验脚本的退出码，绝不是本轮 Host 实际进程退出码。

真实 Runtime 回执于本地时间 `2026-10-23 23:14:13.947 +08:00` 写入，native 回执于 `23:14:13.958 +08:00` 写入；两者 Host runId 一致，均与本轮时间范围匹配，原因/stop reason 为 `SessionLogoff`。native phase=`NativeCallbackCleanupObserved`、cleanup outcome=`Completed`、elapsed=`81.416 ms`、总预算=`4000 ms`，RuntimeStopped/CleanupSucceeded 均为 True，failureType 为 null。完整离线 Assignment、播放策略保存与空 stderr 通过。stdout 仍只有 HOST_RUNNING，`processExitLogged=False / processExitCodeObserved=False / callbackReturnObserved=False` 如实保留；本轮是在事先冻结的 v2 callback 清理证据范围内通过，不是把缺少尾行解释成 Host 退出码 0。

**该无活动 Surface/Renderer 的真实注销清理代表场景关闭，无需为同一目标继续重复注销。** 登录后第三方自启动不使已经落盘的本轮回执失效；本轮不涉及桌面附着或竞争壁纸恢复，不能据此宣称竞争矩阵通过。旧样本 `run-cda178f4bff24080ac1ebd19154be50f/verification.json` 的原始 SHA256 仍为 `015E39D8577EA898C8D8DAE0C95626DDF86B3218F221273C85416CE116C5771B`，未改写且仍不完整。自动基线仍为 394/394，本轮只核验真实文件并更新状态，没有重新运行全量测试或启动新的 Host/色块。

以上为 D 阶段当时边界；其后 E 节活动 Renderer 单屏真实注销也已通过，P1.9.5 按明示条件关闭。不扩展为任意强制终止、真实关机/重启、双屏退出或所有异常矩阵已经实机通过。那些若作为产品接受项，必须明确另行规划和授权。P1.9.2 双屏系统恢复及 P1.9.4 实机延期义务、Stage B `In progress / Not accepted`、Renderer v1/DTO/Schema 与两类空生产 allowlist 不变。

#### P1.9.5-E 最后关闭项：活动 Renderer 的真实注销（2026-10-23）

针对用户要求先完成 P1.9.5，冻结下列切片关闭条件，不再用“完整生产矩阵”这个无边界表述把已完成子项无限延期：

| 关闭项 | 当前证据 | 状态 |
|---|---|---|
| Attach/Load/FirstFrame/Replace、recovery/reconciliation/retry 与退休期间停止、迟到完成、重复退出、配置保留 | 受控状态机与资源所有权自动门禁 | 通过 |
| 原生窗口/控制台通知、共享 Stop、最多 4 秒及分层持久观测 | Debug/Release 自动与原生自有窗口门禁 | 通过 |
| 正式 Host 与 Renderer 进程级退出 | 五项 Host 受控进程与 Renderer 优雅/强制退出 | 通过 |
| 单屏活动 Surface 正常 Close | `active-host-exit-retest-20261023-223803.json`，自动及人工确认 | 通过 |
| 无活动 Renderer 的真实 SessionLogoff | `run-1a8d73f998984bb5baf8eeed0d7332e8/verification-v2.json` | 通过 |
| 活动 Renderer 正在呈现时的单屏真实 SessionLogoff | `run-7b157be0a83247e5a5af4a323858dda0/verification-v3.json`，真实 ShutdownCompleted、Renderer ExitCode=0、Surface=0、双回执/配置保留及人工确认；首轮失败保留 | 通过 |

以上六项已全部通过，P1.9.5 于 2026-10-24 按明示边界关闭；它不宣称任意第三方组件、强杀、真实关机/重启或所有双屏物理组合均已验收。P1.9.2/P1.9.4 的双屏延期和 Stage B 其他门禁仍独立保留，不能转嫁成 P1.9.5 新增的重复注销义务。OS 回调之后的 Main 返回和实际 Host 退出码不是能由回调内文件自证的事实，仍保持未观察，而不是追加无法保证的 stdout 尾声要求。本文随后各项“就绪/待重测”是当时记录，以最后真实重测结果为准。

已实现 `DesktopHostDiagnostics --raised-desktop-host-session-end --run-directory <fresh-dir>`，由 `tools/run-host-session-end.ps1 -Mode Arm -WithActiveRenderer` 显式启动。仅诊断组合根注入合成内容和实验 Raised，生产 Program 不接收新开关，allowlist 不变；复用正式 Runtime、事件桥、SessionCoordinator、Surface Factory 和 Renderer Protocol v1。只选一次当前主屏、创建一个 Session/Surface，首帧及一次 Replace 完成后落盘 `active-ready.json` 并报告 ready。启动预算来自 Host 实际 Apply budget，而非另一个固定 15/20 秒；人工注销等待独立为最多十分钟，超时只清理自有资源、不触发 OS 注销。旧配置或已有本轮证据拒绝覆盖。

native 观测写入路径在同一个 4 秒预算内，先持久化 `active-exit-evidence.json`，再写 native 回执。前者关联同一 Host runId，包含首帧提交 generation、预期 SessionId 集合、逐 Session Shutdown/ShutdownCompleted/Dispose/Surface cleanup，以及真实 Renderer process exit/handle disposal；不记录 HWND/PID/认证令牌/Pipe。新 manifest 才标记 `activeSessionEvidenceVersion=1`；Verify 除 Runtime/native 双回执、配置外，还独立检查非空唯一 Session、generation、完整且不重复的退休与进程证据、自有 Surface 为零，写 `verification-v3.json / ActiveRendererSessionLogoffEvidenceValidated`。不能以 v2 无 Renderer 的记录替代 v3，也不能只相信文件里的 Valid 布尔值。

本轮新增 **23** 项 C# 证据/参数门禁，Debug/Release 基线 **417/417**；合成证据扩展为 **25/25**，覆盖活动记录缺失、旧 runId/generation、ShutdownCompleted 缺失、进程/句柄未释放、残留 Surface、重复退休及缺少 native 回执。最终合成证据：`artifacts/diagnostics/host-session-end/fixtures-b1b5a2f2ecf844189f0ed0f80ae55953/summary.json`；五项无 Surface 正式 Host 回归 **5/5**：`artifacts/diagnostics/host-exit/batch-093cb62169f14a8883fd255a6235a1fc/summary.json`。首轮合成内容全部符合预期，但驱动继承了最后一个预期失败子进程的 LASTEXITCODE，已在驱动成功末尾显式 exit 0，未更改产品失败条件。真实活动注销仍未执行，不能提前标为完成。

最终源码复核还捕获一次测试稳定性失败：既有 `CallbackFailuresCannotEscapeWndProcOrRestartCleanup(False)` 用 50 ms 测试普通异常传播，但引入工作线程调度后负载可能先耗尽该测试预算，得到 TimedOut 而非预期 Failed。已把非超时用例预算明确调整为 1 秒，真实超时用例仍保留短预算，生产 4 秒与独立退休预算均未更改。修正后重新执行 Debug/Release 全量均 **417/417**，Release 工具零警告/错误，边界和差异检查通过；并非原样重跑掩盖失败。最终补强后合成证据仍 **25/25**：`artifacts/diagnostics/host-session-end/fixtures-914eef43b1e940a9a1d9ba31f523ddaa/summary.json`。旧真实失败样本原始 hash 保持不变，本轮没有启动新色块或注销。

#### 最后活动 Renderer 注销实验已就绪（2026-10-24）

用户明确同意后，已执行 `tools/run-host-session-end.ps1 -Mode Arm -WithActiveRenderer`，本轮唯一证据目录为 `artifacts/diagnostics/host-session-end/run-d654e00982c9497ea201a3f2443618aa`。环境为 `26200.9711 / session=10 / WinSta0\Default`；`active-ready.json` 确认 generation=1、1 个活动 Surface、FirstFrameCommitted=True、预期 SessionId 为 `2a0fb7951ca24613b205e381b9f4391d`。再次只读检查确认本轮诊断进程与其 Renderer 子进程各 1 个、stderr 为空，Runtime/native 回执与 active-exit-evidence 均尚不存在，状态只是 `ArmedAwaitingManualLogoff`，不是验收通过。

主屏橙色保持等待用户手动注销（最多十分钟）；工具不主动注销、重启 Explorer 或改显示设置。重新登录后只需执行 `tools/run-host-session-end.ps1 -Mode Verify -RunDirectory E:\project_c\dynamic_wallpaper\artifacts\diagnostics\host-session-end\run-d654e00982c9497ea201a3f2443618aa`，核对 `verification-v3.json`、双回执、逐 Session 退休/真实 Renderer 退出/Surface 清理及 Assignment 保留。若橙色因等待超时自行消失，不把该轮当作实际注销通过，应先核对其终态再决定下一步。

#### 首个活动 Renderer 真实注销失败与退出顺序修正（2026-10-24）

用户确认已注销/重新登录，注销前画面正常，登录后被第三方壁纸接管。核验 `run-d654e00982c9497ea201a3f2443618aa/verification-v3.json` 为 `IncompleteOrFailed`，核验脚本退出码 1：Runtime/native 回执均是实际 `SessionLogoff`，RuntimeStopped/CleanupSucceeded、Assignment、播放策略、空 stderr 全部通过；native elapsed=87.8712 ms，未超时。原提交 Session `2a0fb7951ca24613b205e381b9f4391d` 的退休 elapsed=31.7882 ms，ShutdownSent=True 但 ShutdownCompleted=False，issue=`ShutdownFailed: Renderer failed during shutdown: renderer.process.exited.`。Renderer 实际退出/句柄释放、Surface 清理及 OwnedSurfaceCount=0 均通过，然而 Graceful=False / active evidence Valid=False，**严格保持失败，不以资源已清理替代协议回执**。原始核验文件 SHA256=`0B672B072CAED77898EEFE3EB87A65040A4536C767220DECF2C4755EE285E7C1`，不改写。

登录后第三方壁纸接管不能解释注销时已经落盘的缺失回执；本轮不操作第三方应用。只读查询时间窗口内的 Application 1000/1001/1026 未获得相关事件，旧证据又没有 Renderer 实际退出码，不能断定是系统按序终止、Renderer 自行失败还是其他结束时序。代码核对发现 Host/Renderer 都没有声明相对进程退出顺序，使用相同默认级别，这是一处明确的依赖缺口，与“Renderer 先于 Host 完成 Shutdown 交换而结束”的观察相容，但不是对旧样本唯一根因的证明。

修正仅限当前进程：新增 Platform `WindowsProcessShutdownOrder`，正式 Host 与活动诊断 observer 使用 `0x281`，RendererChildProbe 在 Hello 前使用 `0x280`，设置后读回校验，失败不报告 ready，正常结束后恢复原参数；不使用系统保留范围、SHUTDOWN_NORETRY、全局策略、服务、注册表或 CPU 优先级。[官方定义](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessshutdownparameters)是高 shutdown level 优先结束、各进程默认 0x280。本修正不增加原生 4 秒/退休预算，不改变 v3 必须有真实 ShutdownCompleted 的门禁。新 ready/manifest 记录 Host/Renderer 级别；tracker 增加 Renderer 实际 ExitCode 元数据并保留完整退出错误，进程失败消息不再包含不必要的 native PID。旧样本退出码未知，不能用修正后的数据倒推或补填。

修正后 Debug/Release 全量均 **428/428**，新增八项纯假原生退出顺序测试、两项退出码参数样本和一项退休错误详情保留测试；测试运行器不修改自身真实退出顺序。首次全量发现新测试没有初始化 fake Coordinator，已补 CreateCommandLoop 初始化，修正后两配置全部通过。Release 工具零警告/错误；以下非可见门禁均通过，不代替真实 OS 注销：

- 受控正式 Host 进程 **5/5**：`artifacts/diagnostics/host-exit/batch-e16a8b20791b41ae912392fe60c38d01/summary.json`，各项退出码 0，无 Surface、无真实系统注销，双回执匹配、无自有窗口残留。
- 正式测试 Renderer 无 Surface 进程 **2/2**：`artifacts/diagnostics/renderer-exit-order-20261024-002509.json`，优雅 Shutdown 有真实 ShutdownCompleted；强制 Dispose 不伪造回执；两者真实退出与句柄释放均通过，不创建 child HWND/DComp。
- 合成证据 **25/25**：`artifacts/diagnostics/host-session-end/fixtures-7bc566fd0e7e46cb9b50843445962156/summary.json`，v3 缺少 ShutdownCompleted 等负例仍被拒绝。旧真实失败核验文件 hash 保持不变，未通过修改证据或放宽校验取得通过。

修正完成时，P1.9.5 最后一项仍为失败后待独立授权重测，没有提前关闭，也未新增别的关闭项。当时第三方接管桌面，未经用户关停确认与可见实验许可，没有启动新 Surface、注销或修改第三方启动项；此前无 Renderer 的 v2 通过及旧样本原分类不变。后续授权重测见下文。

#### 退出顺序修正后的活动注销重测已就绪（2026-10-24）

用户确认第三方壁纸完全退出并明确同意重测，已执行 `tools/run-host-session-end.ps1 -Mode Arm -WithActiveRenderer`，新目录为 `artifacts/diagnostics/host-session-end/run-7b157be0a83247e5a5af4a323858dda0`。当前 `26200.9711 / session=11 / WinSta0\Default`、物理单屏 `2880×1800 / 200%`；generation=1、1 个活动 Surface、FirstFrameCommitted=True，SessionId=`3b5af1204d5c4115b666050f28507c4c`。ready/manifest 已核对 HostShutdownLevel=641（0x281）、RendererShutdownLevel=640（0x280）；Host 观察器与 Renderer 子进程各 1 个且同会话，stderr 为空，尚无退出回执。状态仅为 `ArmedAwaitingManualLogoff`，不能提前判通过。

主屏橙色保持等待用户手动注销，最长十分钟，无自动注销、Explorer 重启或启动项修改。重新登录后使用本轮完整目录运行 `tools/run-host-session-end.ps1 -Mode Verify -RunDirectory <本轮目录>`，重点确认真实 ShutdownCompleted、Renderer 实际退出码及句柄释放、Surface 为零、双回执和 Assignment 保留；旧失败目录不改写。若等待超时自动清理，不把超时结束视为实际注销通过。

#### 活动 Renderer 真实注销重测通过，P1.9.5 关闭（2026-10-24）

用户确认“注销前正常，登陆后是原壁纸”。对本轮独立目录 `artifacts/diagnostics/host-session-end/run-7b157be0a83247e5a5af4a323858dda0` 执行 Verify，脚本退出码 0，`verification-v3.json` 分类为 `ActiveRendererSessionLogoffEvidenceValidated`。这是实际手动 SessionLogoff，不是受控消息注入；工具未主动注销、重启 Explorer 或注册自启动。环境证据对应 `26200.9711 / 原 session=11 / WinSta0\Default`、物理单屏 `2880×1800 / 200%`，不将旧 build 或双屏样本泛化为本轮通过。

Runtime/native 回执 runId 同为 `5c939d96fe05485dbbb0039b51586346`、reason=SessionLogoff，RuntimeStopped/CleanupSucceeded=True；native elapsed=**72.4434 ms**、budget=4000 ms，cleanupOutcome=Completed。generation=1、原提交 Session `3b5af1204d5c4115b666050f28507c4c` 的 retirement elapsed=**30.722 ms**，ShutdownSent/ShutdownCompleted/RendererDisposed/SurfacesCleaned/Graceful/OwnedResourcesCleaned 全部 True、issues 为空；Renderer 实际退出已观察、ExitCode=**0**、句柄已释放；最终 OwnedSurfaceCount=**0**、active evidence Valid=True。Assignment 保留、策略已保存、stderr 为空。登录后只读进程检查也未发现 LiveWall.Host、DesktopHostDiagnostics 或 RendererChildProbe 残留。

本轮核验文件 SHA256=`552D7FDEB155769020B60ACFB70CB76A0FD33AFFAEA6F22DAFAD893E78CD13EB`。首个活动失败文件仍为 `0B672B072CAED77898EEFE3EB87A65040A4536C767220DECF2C4755EE285E7C1`，更早 stdout-only 不完整样本仍为 `015E39D8577EA898C8D8DAE0C95626DDF86B3218F221273C85416CE116C5771B`，均未改写或重新解释。一次修正后成功证明本轮退出链成立，不能倒推首轮失败的唯一根因，也不是长期成功率声明。

实际 **Host** 退出码、Main 返回与 native callback 返回仍未观察，`processExitLogged/processExitCodeObserved/callbackReturnObserved=False` 按证据范围保留；上文 Renderer ExitCode=0 和核验脚本退出码 0 不与其混淆。登录后的原壁纸符合本诊断不注册自启动的行为，不构成产品自动启动/Assignment 跨登录自动呈现验收。按 E 节有限关闭表，**P1.9.5 代码、自动/受控门禁及代表性实机项全部完成，正式关闭，不再追加重复注销。** P1.9.2 双屏系统恢复、P1.9.4 真实 Shell/display 并发及双屏延期、生产内容入口与 Shell mutation 策略仍独立保留；Stage B 继续 `In progress / Not accepted`，Renderer Protocol v1/DTO/Schema 和两类空生产 allowlist 不变。本轮仅核验、同步状态与检查文档差异，未启动新的色块或系统操作。

### P1.9.5-A 退出事务与等待者安全（2026-10-23）

本轮完成代码及自动门禁，不操作真实桌面。Shutdown 入队即发出停止取消信号，Host 单写者进入 Stopping 并终态化 Apply/recovery/reconciliation；普通 Apply、拓扑 Replace 和恢复 Replace 关联停止取消，尚未执行的 Replace 不再提交。已经发生的原生窗口操作不能承诺回滚或被抢占，随后仍必须按停止路径清理，不能声称 Windows 物理显示层具有强事务撤销。

`HostCommandLoop.DisposeAsync` 共享一个完成任务：先取消准备和重试，保持内部通道开放，等待所有准备/恢复/策略后台生产者完成转交；通过单写者队列屏障处理迟到批次，随后完成通道并排空所有排队调用者，最后等待独立 Session retirement，才释放 operation/CTS。停止后仍可返回幂等 Shutdown/状态快照；通道关闭后变更命令明确返回 `host.stopped`、TryPost 返回 false。清理错误进入有界失败记录，Dispose 不伪装成功；ShutdownTimeout 但最终自有资源已清理时仍保留独立退休问题，不回滚新会话。

正在 reattach 的 recovery 是旧/新 Surface 与共享 Renderer 的清理所有者，Shutdown 不再同时普通退休同一个 Renderer；恢复第一步拓扑采样失败或取消也进入整批 abandon。Runtime 的 Stop 共享任务不受单个调用者取消等待影响；配置读取/Assignment 恢复阶段的 Stop 取消启动，迟到启动不能重启已释放的事件桥或复活 Running，初始化未完成不覆写原配置文件。

新增 **20** 个受控样本：Attach/Load/FirstFrame/Replace 退出；迟到 Apply/reconciliation/reattach/rebuild；恢复/拓扑退避退出；三种恢复 Replace 取消；恢复初始拓扑查询取消；重复 Shutdown/Dispose 和旧 Renderer 退休超时；迟到事件、40 个排队调用者全部终结；取消 Runtime Stop 等待；启动/退出交错；故意清理失败仍可观察。正常样本逐一检查 Renderer 与 Surface 只清理一次，失败注入样本保留真实失败而非宣称零残留。

当时 Debug/Release 均 **354/354**（Domain 12、Application 221、Renderer Contracts 22、Package 21、Windows 78），Release 工具零警告/错误，架构边界与差异检查通过。**P1.9.5-A 代码及自动门禁完成；P1.9.5 整体未接受。** 其后 B/C 已补齐系统通知接线及受控真实进程样本，实际 OS 会话结束与活动桌面观察仍待确认。合作组件使用既有阶段/退休预算，不能据此保证任意不响应取消的第三方组件也能有界退出。未启动色块、改变显示器或重启 Explorer，协议、Schema、两个生产 allowlist 和 Stage B 状态不变。

### P1.9.4-A Shell 抢占未提交准备（2026-10-11）

确认 Shell generation 改变后，Host 立即取消尚未终态的 Apply preparation，并将进行中的拓扑 reconciliation 终态化为 `Cancelled/Superseded`；Shell 恢复使用最新已接受拓扑。自动覆盖普通 Apply 和拓扑准备已经完成 FirstFrame、内部完成尚未投递时的抢占：取消终态立即可等待；恢复完成后迟到 provisional 结果只能清理，不能 Replace 或改变已提交 Session/Assignment。

此项只是 P1.9.4 的第一条自动边界，不等价于完整环境恢复仲裁。反向顺序（Shell 恢复中接受新 topology revision）、NoChange/Suspend 抢占、旧恢复终态与新事务隔离、恢复期重复信号及真实并发实验仍待收口。未执行 Explorer 或显示变化实机实验。

本次 Debug/Release 全量自动基线 `279/279`（新增两条受控的 FirstFrame 后迟到结果测试），Release 诊断工具构建及架构边界检查通过；不修改 Renderer Protocol v1、DTO/Schema 或生产 allowlist。

### P1.9.4 自动证据收口（2026-10-23）

按当前没有外接显示器的条件，只推进代码和受控自动门禁，不执行色块、显示设置变化或 Explorer 重启。已用强类型 `DisplayReconciliationSupersessionCause` 区分 `ShellGenerationChanged` 与 `NewerTopology`，并发证据不再依赖日志文本。

诊断入口与自动测试现在共用同一个纯证据校验器，输出 `ValidationIssues`：逐个已发布 revision 必须恰有一个终态；最终显示器、Session 和 Surface ordinal 必须一一对应、bounds 精确匹配；每个最终 Surface 必须恰有一条当前 generation 的成功首帧且位于唯一 Replace 之前；每个活动 Session 的退休和 Renderer 实际退出必须有对应记录。重复显示器、漏屏、旧 generation 首帧/退休、重复首帧、提交后首帧、缺失退出和空集合不能靠 `All()` 或总数量蒙混通过。无重叠/未观察到最终 revision 仍为 `EnvironmentInconclusive`；采集丢失为 `HarnessFailure`；产品不变量冲突为 `ProductFailure`。

整批 Rebuild 现在将真实 Apply preparation journal 中的逐 Surface `SurfaceAttached/FirstFramePresented` 在 Replace 前纳入 recovery journal，注明来源；这些条目表示 Host 收集到的准备证据，不冒充原始事件的独立性能时钟。新增受控双屏 `2→1→0→最终双屏/零屏` 交错，证明中间 revision 立即取消、仅最新布局一次提交或 Suspend、无 stale Shell resize、无多余 Renderer、离线 Assignment 保留。Suspend 保留已放弃 reattach 的历史首帧，但没有 Replace；不能把历史首帧误算成当前可见提交。

本轮新增 **32** 条自动样本，当时 Debug/Release 全量均 **334/334**（Domain 12、Application 201、Renderer Contracts 22、Package 21、Windows 78）。Release 工具零警告/错误，架构边界和差异检查通过。**P1.9.4 开发切片关闭；C 的真实并发、双屏层级和资源/Shell 清理实机证据为 `Deferred / Pending hardware`，生产验收门禁仍待关闭。** 暂不因无外接屏反复启动人工实验；不阻塞 P1.9.5 退出竞态代码。Renderer v1、DTO/Schema、默认产品入口、两个生产 allowlist 和 Stage B 状态不变。

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
