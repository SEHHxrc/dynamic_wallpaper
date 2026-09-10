# DesktopHostDiagnostics

采集 Shell/显示器拓扑，并以受控色块模式验证候选桌面附着。不得自动修改持久设置或生产 allowlist；输出必须脱敏稳定设备路径中的用户数据。

## 当前能力

默认命令只读输出真实会话中的稳定显示器 ID、虚拟桌面坐标、缩放和刷新率：

```powershell
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj
```

`--shell-topology` 只读输出当前进程的 session ID、window station、thread desktop、完整 Windows build/UBR、`GetShellWindow` 锚点、`Progman/WorkerW` 顶级窗口及 Shell 后代递归快照。每个窗口记录 class、PID/TID、parent/owner、style/ex-style、bounds、visibility、`SHELLDLL_DefView`/`SysListView32` 关系和同级 Z-order：

```powershell
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --shell-topology
```

若当前进程所在 desktop 无法访问 Explorer，工具输出 `Inconclusive / Shell unavailable` 并返回退出码 3。`Shell anchor: 0x0` 与空窗口集合只证明当前执行上下文看不到 Shell，不能解释为该 Windows build 有 0 个候选；显示器拓扑存在也不会覆盖此状态。默认只读显示器模式不要求 Shell 可见。

若 Shell 可见，但实验适配器找不到通过结构规则的附着点，色块模式输出 `Not supported / No compliant desktop attach point` 和具体拒绝原因，并返回退出码 4。该结果是当前规则下明确的“不支持”，不同于 Shell 不可见的无结论状态。

`--color-block` 显式创建每显示器测试 Surface/色块并自动清理。默认持续 10 秒；`--duration-seconds` 只可与该模式组合，范围为 1–30 秒：

```powershell
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --color-block
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --color-block --duration-seconds 15
```

Raised Desktop 研究使用一组独立且显式的入口，不修改 Legacy 适配器或生产 allowlist：

```powershell
# 预检通过后发送一次 0x052C / 0x0D / 0x01，并比较 before/after 指纹
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-probe

# 结构通过后显示限时 layered child 色块
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-color-block --duration-seconds 5

# 可扩展 presentation probe；支持 layered-gdi、non-layered-gdi 与 direct-composition-swap-chain
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-presentation-probe non-layered-gdi --duration-seconds 5
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-presentation-probe direct-composition-swap-chain --duration-seconds 5

# 显式重启当前 Progman 所属 Explorer，并在新 Shell generation 上重建 DComp 探针
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-explorer-recovery-probe --confirm-explorer-restart --duration-seconds 5

# 独立 Renderer 进程通过协议 v1 接收 Host 容器 HWND，并创建自有 child/DComp/swap chain
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-renderer-child-probe --duration-seconds 5

# 故意终止辅助 Renderer，验证 child/GPU/Host 自有资源清理；不会重启 Explorer
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-renderer-child-probe --duration-seconds 1 --simulate-renderer-crash

# 显式重启 Explorer，并保持同一 Renderer PID 向新 Host Surface 重新附着
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-renderer-child-recovery-probe --confirm-explorer-restart --duration-seconds 5

# 复用正式 HostCommandLoop、SessionCoordinator、Surface Factory 和 Renderer v1 会话的产品候选链
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-product-candidate --duration-seconds 5

# 单次结构化结果；不持久化认证令牌、Named Pipe 名称或 HWND
./tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe --raised-desktop-product-candidate --duration-seconds 1 --result-json artifacts/diagnostics/manual/run-01.json

# 显式选择全部显示器或一个稳定 display-id；默认仍为 primary
./tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe --raised-desktop-product-candidate --target-display all --duration-seconds 5 --result-json artifacts/diagnostics/manual/dual-screen.json
./tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe --raised-desktop-product-candidate --target-display win-display-5905e8dd96f126ad3fcf7ce9012666f1726901d94ced285eb61acf3fdda49fe9 --duration-seconds 5

# 仅产品候选允许显式越过普通 30 秒显示上限
./tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe --raised-desktop-product-candidate --soak-seconds 90

# 同一 Host 内对全部显示器串行执行 generation、原子 Replace 和上一批 Session retirement
./tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe --raised-desktop-product-candidate-loop --target-display all --iterations 20 --duration-seconds 1

# 直接启动已构建的 Release 可执行文件；不构建，也不重试失败样本
./tools/run-product-candidate-reliability.ps1 -Configuration Release -FreshProcessCount 10 -VisibleSeconds 1 -OutputDirectory artifacts/diagnostics/cold-start-26200.9168
```

产品候选命令默认只选择当前主显示器；`--target-display primary|all|<display-id>` 必须显式扩大范围。`all` 使用 PerDisplay 为每块显示器创建独立 Surface 和 Renderer Session，只有所有 Session 都产生当前 generation 的 FirstFrame 后才执行一次批量 Replace；任何一屏失败都会清理整批 provisional Renderer/Surface，不允许另一屏单独激活。命令会输出运行前和清理后的完整 Shell 拓扑、逐 Session 显示器/Surface/retirement 结果、Progman 直属 WorkerW 数量及新增句柄；没有新增 WorkerW 只表示本轮未观察到额外 mutation，不证明此前已有 WorkerW 可由 LiveWall 撤销。

`--result-json` 使用 schema `1.0` 记录 run ID、UTC 时间、完整 build、执行 desktop、显示拓扑 SHA-256、Apply 终态/阶段时间线、retirement、清理和分类。分类固定为 `ValidSuccess / ProductFailure / EnvironmentBlocked / EnvironmentChanged / Cancelled / HarnessFailure`。批次驱动要求固定次数全部为 `ValidSuccess`；产品失败不会被丢弃或补跑，竞争 Surface、错误 desktop、Explorer generation 或显示拓扑变化会停止整批。统计只输出 minimum/median/maximum，不以 10 个样本定义 p95/p99。

`--raised-desktop-product-candidate-loop` 只创建一个 `HostCommandLoop`，并与单次候选共用 `--target-display primary|all|<display-id>`（默认 primary）。每轮为选定显示器创建独立 Renderer/provisional Surface；全部首帧完成一次原子 Replace 后，等待上一批所有 Session 的独立 retirement，再进入下一轮。测试 Renderer 按 generation 在橙色和黄色之间交替。最后一轮由显式 Shutdown 退休；双屏 20 轮应产生 40 个 retirement，其中前 38 个由 Replacement 触发、最后 2 个由 Shutdown 触发。工具必须确认 generation 数量、退休 Session 数量、自有 HWND 和新增 WorkerW 均符合预期。

Raised 预检要求 `WinSta0\Default`、`GetShellWindow == Progman`、Progman 具有 `WS_EX_NOREDIRECTIONBITMAP`、直接 DefView 具有 `WS_EX_LAYERED`、双方属于同一 Explorer 进程，且 350 ms 内结构指纹稳定。结果只携带不包含 HWND/PID 的 SHA-256 结构指纹和强类型拒绝码；完整 HWND/PID 只显示在本次运行的临时快照中。

Renderer-child Host Surface 获取还会拒绝 DefView 与 WorkerW 之间既有的、来自其他进程的可见全屏桌面窗口，并报告 `CompetingDesktopSurface`。诊断工具不会与第三方动态壁纸周期性争夺 Z-order；竞争程序必须由用户完整退出，若它在 Explorer 重启后自动恢复，每秒层级复验会使实验失败关闭。

Raised 诊断退出码：结构拒绝为 5；请求后 Shell WorkerW 未自行恢复为 6（`CleanupIncomplete`）；Explorer 重建失败为 7；Renderer-child 生命周期或清理失败为 8。普通探针绝不重启 Explorer；只有命令同时包含 `--raised-desktop-explorer-recovery-probe` 和 `--confirm-explorer-restart` 才允许终止当前 Progman 所属 PID，缺少任一参数都会在修改 Shell 前拒绝。色块窗口销毁成功不代表 Shell 请求已撤销。

## 色块模式边界

专用 Window Dispatcher、hidden provisional Surface 和 `ReplaceSurfacesAsync` 已落地，色块入口也已实现。该模式只允许显式启动，设置自动超时，并在正常退出、取消或异常时销毁全部窗口；默认运行继续保持只读。

色块模式允许诊断代码尝试尚未进入生产 allowlist 的候选，但仍执行结构验证并失败关闭。2026-09-06 已在 `26200.9168`、`session=2 / WinSta0 / Default` 完成第三方动态壁纸退出后的连续采样和 Explorer 重启复采。旧 Explorer 一度保留 `Progman` 子 `WorkerW`；重启后稳定基线只有 `Progman → SHELLDLL_DefView → SysListView32 → SysHeader32`，没有符合既定 Legacy WorkerW 规则的候选。限时色块尝试因此在创建 Surface 前拒绝附着，前后拓扑不变。这不是工具错误，也不能通过把 `Progman` 直接当作附着点来绕过。工具入口已实现不等于真实图标层级、首帧交换或 Explorer 恢复已经验收。

同一 build 的隔离 Raised 实验得到的是结构正结果而非可见附着能力：`0x0D/0x01` 唯一新增了全屏、同 Explorer、无 owner、位于 DefView 后方的 Progman 直接子 WorkerW；修正后的 layered GDI 色块自动快照验证了 `DefView(z0) > LiveWall(z1) > WorkerW(z2)`，但显式向 DC 填充洋红像素、强制刷新 GDI 并等待 DWM 提交后，人工观察仍完全没有变化。因此该结构未进入当前桌面的实际可见合成路径，不得称为 attach point 或启用生产 `RaisedDesktopAdapter`。

诊断输出已拆分为 `OwnedResourcesCleanup` 与 `ShellMutationRecovery`。`OwnedResourcesCleanup=Complete` 只表示 LiveWall 自有 HWND 已销毁；`ShellMutationRecovery=NoMutationObserved` 只表示本次调用没有引入新的 Shell 结构，不会覆盖更早实验留下的副作用。初次请求生成的 Shell WorkerW 仍保留，整体 shell mutation recovery 仍必须记录为 `CleanupIncomplete`。工具不会静默重启 Explorer；经用户明确授权的 Explorer 重启只能恢复诊断环境，不能证明请求本身可撤销。

non-layered GDI 探针已实现并完成一次 5 秒运行：输出 `PresentationProbeCompleted`、`presentationKind=NonLayeredGdi`、`OwnedResourcesCleanup=Complete`、`ShellMutationRecovery=NoMutationObserved`，但人工观察仍完全没有桌面变化，最终判定为 `PresentationFailed`。`PresentationProbeCompleted` 只表示探针执行完毕；人工失败后不得产生 `RenderableDesktopAttachment`。

DirectComposition/交换链探针现已实现：硬件 D3D11 composition swap chain 绑定 LiveWall 自有 `WS_EX_NOREDIRECTIONBITMAP` HWND，而不是绑定 Explorer 的 Progman。首次 5 秒实验人工确认真实像素可见，但窗口从顶层样式转换后残留 `WS_CAPTION`，交换链客户区没有覆盖上、左非客户区。探针改为直接创建无边框 Progman 子窗口后，第二次运行自动结果为 `PresentationProbeCompleted`、`OwnedResourcesCleanup=Complete`、`ShellMutationRecovery=NoMutationObserved`，活动窗口为 `0,0 2880x1800`、style `0x5E000000`，仍保持 `DefView > LiveWall > WorkerW`；人工确认完整覆盖壁纸区域，桌面图标和任务栏符合预期。该结果形成单屏 `RenderableDesktopAttachment` 诊断证据，但不会自动修改生产 allowlist。

显式 Explorer 恢复实验已完成一次自动与人工成功运行：只有在第一个 DComp Surface 活动快照验证通过后才终止当前 Progman 所属 Explorer；工具观察到 Shell PID 与核心 HWND generation 全部变化，等待新 Progman 子树稳定后重新运行 Raised 请求并创建全新的 DComp Surface，结果为 `generationChanged=True`、`rebuilt=True`、`OwnedResourcesCleanup=Complete`。稳定性门禁只比较 Progman 与其后代，忽略无关顶级窗口的全局 Z-order 波动；完整报告指纹仍保留全部采样信息。人工观察序列为“旧洋红短闪 → Shell 黑屏重建 → 新黄色完整显示且不遮挡任务栏 → 数秒后还原”。

隔离 Renderer-child DirectComposition 探针现已实现。父进程以显式 DefView anchor/WorkerW backdrop 创建 Raised Host Surface，只通过现有协议 v1 `AttachSurfacePayload` 把 LiveWall 容器 HWND 传给独立 `RendererChildProbe`；Progman、DefView、WorkerW 不跨进程。辅助进程创建并拥有 child HWND、D3D11 device、DComp target/visual 和 composition swap chain；只有 target 创建成功后发送 `SurfaceAttached`，只有 `Present + Commit + DwmFlush` 成功后发送 `FirstFramePresented`。

2026-09-08 在 `26200.9168 / WinSta0\Default / 2880x1800 / 200%` 完成正常退出、父进程 Ctrl+C 取消和故意崩溃三轮验证：Host 与 Renderer PID 不同，child parent/bounds 正确，活动结构为 `DefView > Host Surface > 既有 mpv > WorkerW`，事件顺序正确，正常 Shutdown、取消与退出码 `-1` 的故意崩溃均未留下 LiveWall 自有进程或 HWND。用户在三轮中均看到完整蓝色桌面像素，图标、普通桌面程序和任务栏未被遮挡，因此单屏协议 v1 `HwndChild` binding 的呈现门禁通过。最初输出将颜色描述为橙色是诊断常量按 Win32 `COLORREF` 字节序解释造成的文案错误，后续常量已校正。该结果尚不等于同一 Renderer 进程跨 Explorer generation 重新附着。协议和 JSON Schema 均未修改；只有后续证据证明必须跨进程共享 GPU 资源时才进入协议 1.1。

同日完成一次 Renderer-child Explorer 重附着自动实验：旧 Explorer PID `29900`、Shell HWND `0x100FA` 变为 PID `11884`、HWND `0x2041A`，Renderer PID 始终为 `17548`；旧 Host/child `0x2E0768/0xE0032` 与新 Host/child `0x60788/0x20446` 均未复用。辅助进程先释放旧 generation 的 child/DComp/swap chain，再在新 Host HWND 上创建第二代资源并发送第二组 `SurfaceAttached/FirstFramePresented`，最后正常退出且无自有资源残留。用户确认橙色、Shell 黑屏、黄色顺序正确，但黄色观感可能明显短于指定 5 秒，任务栏层级也仅能凭记忆判断，因此这次人工结果不记为通过。工具随后增加单调时钟计时、每秒拓扑复验，并要求 Host 在整个区间始终是 DefView 的下一同级窗口，以识别第三方桌面进程重插导致的提前遮挡。

增强后的 10 秒实验在第二代首帧成功后的第一次每秒复验即检测到层级失效并清理退出。随后只读快照确认第三方 `mpv` 已自动重启，当前结构为 `DefView(z0) > mpv(z1) > WorkerW(z2)`；这与黄色快速消失一致。该结果证明持续复验门禁有效，但不能判定 LiveWall 同进程重附着的持续呈现失败。必须完整退出或禁用竞争壁纸程序的 Explorer 自动恢复行为后再运行恢复实验。

完全退出 Lively Wallpaper 后的干净 10 秒重测自动与人工验收通过：Explorer PID `34796 → 7192`，Renderer PID 始终为 `6516`；旧/新 Host `0x303A2 → 0x70194` 与 child `0x4007A → 0x40236` 均未复用。第二代 `SurfaceAttached/FirstFramePresented` 成功后，单调时钟完成 10 秒区间，每秒快照均确认 Host 紧邻 DefView 后方且 child parent/bounds/PID 正确；最终 Renderer 正常 Shutdown，并且无 LiveWall 自有资源残留。人工观察为“橙色约 2 秒 → 黑屏 → Explorer 与任务栏恢复 → 黄色完整约 10 秒”，黄色期间图标与任务栏保持在上方。中间黑屏来自显式终止旧 Explorer 后 Shell generation 尚未建立，不是错误；该诊断只验收正确重建与重新附着，不宣称产品级无缝切换。

2026-09-09 正式产品候选在 `session=5 / WinSta0\Default / 26200.9168`、主屏 2880×1800@200% 与外接屏 3840×2160@150%（负坐标）的双屏环境运行三次。第一次由外层固定 15 秒等待终止，两屏均无变化并失败关闭；内部握手、Attach、Load/FirstFrame 当前各自最多允许 10 秒，因此该样本尚不能区分真实冷启动卡死与总预算提前耗尽。第二和第三次均只在主屏显示橙色约 5 秒，桌面图标和任务栏位于其上，外接屏保持不变，结束恢复无黑屏。最终一次输出 `firstFrame=True`、`ownedResourcesCleanup=True`、`ownedWindowsRemain=False`，运行前后 Progman 直属 WorkerW 均为既有 `0x205B6`，未新增 WorkerW。该证据标记为“多屏环境中的主屏单 Surface 产品候选真实首帧通过”；在增加阶段时间线并统一总 deadline 前，首次样本保持 `UnclassifiedTimeout`，物理单屏和双屏全覆盖仍未验收。

同日 P1.5 可观测性接线完成后进行了两次主屏候选运行。第一次冒烟运行中，用户看到主屏橙色一闪而过；外接显示器被其他应用程序遮挡，视觉结果为 `Inconclusive`，不能登记为外接屏有变化或无变化。随后再次运行 1 秒主屏候选，用户确认主屏橙色正常显示约 1 秒，外接显示器没有变化，这符合当前命令默认只选择 primary 的范围。诊断不再固定等待 15 秒，而是等待 Apply generation 的强类型终态；默认单 Surface 推导 deadline 为 52 秒。第二次终态为 `Succeeded`：`ProcessStarted=756.736 ms`、`PipeConnected=798.834 ms`、`HelloReceived=868.551 ms`、`Initialized=879.453 ms`、`FirstFramePresented=1052.298 ms`、`SurfacesReplaced=1070.247 ms`、`CleanupVerified=2110.917 ms`。最终 `firstFrame=True`、`ownedResourcesCleanup=True`、`ownedWindowsRemain=False`，前后直属 WorkerW 数量均为 1 且没有新增句柄。旧的 15 秒失败不能由新日志事后归因，仍保留为历史 `UnclassifiedTimeout`；后续冷启动失败必须给出 generation、phase、outcome 和强类型 reason。

P1.5.1 完成后，产品候选的成功 Apply 时间线在 `SurfacesReplaced` 终止；显示期结束后的 `Shutdown / ShutdownCompleted / Renderer Dispose / Surface cleanup` 改由独立 Session retirement result 输出。该结果分别报告 `graceful` 和 `ownedResourcesCleanup`，并在 Renderer 不响应 Shutdown 时输出强类型 `ShutdownTimeout`，不再复用已经终止的 Apply deadline，也不会由诊断直接调用 Coordinator 后再被 Host Dispose 重复退休。

P1.5.1 接线后的首次交互式复测在创建 Surface/Renderer 前以 `CompetingDesktopSurface` 失败关闭：只读进程检查确认 `mpv` PID 37096 来自 Microsoft Store 版 Lively Wallpaper。该样本只证明竞争壁纸门禁仍有效；工具没有终止第三方进程，也没有修改 Shell。用户完全退出 Lively 后，在当时没有外接显示器的物理单屏环境再次执行 1 秒 primary-only 候选，自动结果通过：`FirstFramePresented=1233.154 ms`、`SurfacesReplaced=1256.741 ms`，独立 retirement 用时 `108.563 ms`，且 `shutdownSent=True`、`shutdownCompleted=True`、`rendererDisposed=True`、`surfacesCleaned=True`、`issues=none`。最终 `ownedWindowsRemain=False`，前后 Progman 直属 WorkerW 均为 1、没有新增句柄。用户人工确认主桌面橙色正常显示约 1 秒，图标和任务栏层级正确。因此该次形成物理单屏主显示器的 P1.5.1 自动与人工通过证据；不覆盖双屏、外接屏或热插拔能力。

P1.6 可靠性驱动已经实现结构化单次结果、Release process-cold 聚合脚本、产品候选专用 soak 和同一 Host 串行循环。Codex 上下文中的运行时冒烟成功写出 JSON，并因当前 desktop 为 `WinSta0\CodexSandboxDesktop-*`、Shell 不可见而正确分类为 `EnvironmentBlocked`；该样本未创建 Surface/Renderer，不计为产品失败或有效冷启动样本。双屏 90 秒长驻、10/10 次 process-cold 和 20 轮同 Host 循环均已从 `WinSta0\Default` 完成。

P1.7-A 的正式 Renderer 定点故障入口为 `--raised-desktop-product-candidate-fault`，必须同时指定 `--fault-phase surface-attached|content-loaded|first-frame-presented` 与 `--fault-action fatal|exit|suppress`；默认选择全部显示器并注入第二个 Surface，也可用 `--target-surface-ordinal` 显式调整。故障计划只通过子进程环境变量传给 `RendererChildProbe`，辅助进程读取后立即清除；Renderer Protocol v1、DTO 和 Schema 不变。只有失败阶段/原因吻合、零 Replace、零活动 Session、全部 provisional 资源清理、Shell generation/拓扑稳定且无新增 WorkerW 时，结果才分类为 `ExpectedFailureValidated` 并返回成功。若正式注入前先发现竞争桌面 Surface、Shell 不可见或 desktop 错误，则分类为 `EnvironmentBlocked` 并停止批次，不把环境阻塞记成产品失败。

2026-09-10 在 `26200.9168 / session=7 / WinSta0\Default` 完成首次 `--target-display all --duration-seconds 5` 双屏产品候选。拓扑为主屏 2880×1800@200%/120 Hz `(0,0)`，外接屏 3840×2160@150%/60 Hz `(-3840,-156)`。自动结果为 `ValidSuccess`：两个独立 Session/Surface 均产生 FirstFrame，于约 1.31 秒后只执行一次批量 Replace；两个 Renderer 均 `ShutdownCompleted`、Dispose 和 Surface cleanup 成功，Shell generation/显示拓扑不变，直属 WorkerW 前后均为 1，无 LiveWall HWND 或新增 WorkerW。用户人工确认两屏同步完整显示橙色约 5 秒并同步结束，外接屏无边缘缺口，桌面图标、任务栏和普通程序始终在上方，没有黑屏。该结果关闭双屏基础 PerDisplay 呈现门禁，但不覆盖长驻、故障、Explorer、热插拔或主屏切换矩阵。

同日随后执行 `--target-display all --soak-seconds 90`。自动结果为 `ValidSuccess`：Apply 在约 1.302 秒完成，两个 Surface 跨越旧 52/82 秒 Apply deadline 后继续保持活动；90 秒结束时批次 `graceful=True`，两组 `ShutdownCompleted/RendererDisposed/SurfacesCleaned` 全部为真、`issues=none`。清理后 `ownedWindowsRemain=False`，Progman 直属 WorkerW 前后均为 1、无新增 WorkerW，Shell generation 和显示拓扑均未改变。用户人工确认直到最终恢复为止，两屏全程同步显示且没有闪烁、黑屏、提前恢复或图标/任务栏/普通程序层级异常。该样本关闭首个双屏长驻门禁，但不替代 process-cold、同 Host 连续 Apply、Explorer、热插拔和故障矩阵。

同日又执行 Release 双屏 10 次 process-cold 批次，得到 `requested=10 / executed=10 / valid=10 / successful=10`，没有补跑、产品失败、环境变化或 Harness 失败。10 份结果均为两个 Session、Apply `Succeeded`、graceful retirement、ShutdownCompleted、Renderer Dispose、Surface cleanup、无 retirement issue、无自有 HWND/新增 WorkerW，且 Shell generation/显示拓扑稳定。Apply 耗时 minimum/median/maximum 为 1245.6/1257.1/1285.2 ms，最后首帧为 1227.7/1238.1/1267.7 ms，批次退休为 43.8/60.6/80.3 ms。用户确认批次期间双屏均未遮挡桌面图标、任务栏或普通程序；未把未明确观察的逐轮视觉同步扩大为人工结论。

随后执行 `--raised-desktop-product-candidate-loop --target-display all --iterations 20 --duration-seconds 1`。20 个 generation 全部成功，每代两个 Session/Surface 和两个 FirstFrame 后仅一次 Replace；40 个 Renderer 全部 graceful retirement，其中 38 个由 Replacement、最后 2 个由 Shutdown 触发，全部 `ShutdownCompleted/RendererDisposed/SurfacesCleaned=True` 且 `issues=none`。最终 `allSucceeded/allRetired/triggersCorrect=True`、`ownedWindowsRemain=False`、`addedWorkerCount=0`。用户人工确认两屏全程同步交替橙黄，过程中无黑屏、无图标/任务栏/程序覆盖，最后正常恢复。该结果关闭 P1.6 当前双屏可靠性切片，但不覆盖 Explorer、热插拔、会话切换和故障注入矩阵。

验收矩阵和下一步子窗口/Z-order 诊断方向见 `docs/windows-desktop-host.md`；实时状态见 `docs/implementation-status.md`。
