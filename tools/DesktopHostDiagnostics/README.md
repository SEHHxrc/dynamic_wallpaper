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

# TG0：在旋转前预置有限洋红 guard；必须指定一块稳定 display-id，并由用户手工旋转
./tools/DesktopHostDiagnostics/bin/Release/net10.0-windows10.0.18362.0/DesktopHostDiagnostics.exe --raised-desktop-transition-guard-probe --confirm-display-rotation --target-display <display-id> --duration-seconds 10

# 直接启动已构建的 Release 可执行文件；不构建，也不重试失败样本
./tools/run-product-candidate-reliability.ps1 -Configuration Release -FreshProcessCount 10 -VisibleSeconds 1 -OutputDirectory artifacts/diagnostics/cold-start-26200.9168
```

TG0 只验证预置 Guard 是否会在 DWM 切换到新几何时自动显现。工具先显示精确橙色产品候选，再输出 `GUARD_ARMED`；只有看到该标记后才旋转指定显示器。Guard 使用洋红色，只覆盖“宽高互换候选区域减去当前区域和其他显示器”的有限条带。Armed 之后工具不会移动、缩放或再次 Commit Guard；正式拓扑链随后仍以黄色 Surface 完成重建。人工必须把 Windows 黑屏后的第一可见帧归类为 `GuardVisible / StaticWallpaperVisible / SystemBlackOnly / Inconclusive`。最终黄色正确不代表 Guard 成立。探针退出时要求 Guard、正式 Surface、Renderer 和 DComp 资源全部清理，且不得新增 Shell WorkerW。

该入口不修改 `IDesktopHost`、Surface 模型、Renderer Protocol v1、DTO、Schema、正式 Replace 或生产 allowlist。它只支持单块目标显示器的 90°/270°宽高互换假设；预测原点不成立、区域为空、区域超限或与相邻显示器不能安全隔离时返回 `GuardUnavailable`，不会改用超大窗口。

2026-09-16 首次真实 TG0 在内置主屏 `2880×1800@200% → 1800×2880@200%` 得到 `GuardVisible`。输出为 `GUARD_ARMED → GUARD_OBSERVED → GUARD_RETIRED`，预置区域 `(0,1800 1800×1080)`、`adjacentIntersection=0`、`postArmWindowMutations=0`、`targetReachedCandidate=True`、最终 `ownedWindowsRemain=False`。用户确认黑屏结束后洋红 Guard 一闪而过，未看到新增下方区域的黑屏或静态壁纸，随后黄色完整显示且外接屏、图标、任务栏和普通窗口无覆盖。该记录是横→纵单次正证据，不替代反向和重复性矩阵。

随后反向 `1800×2880@200% → 2880×1800@200%` 也得到 `GuardVisible`：右侧预置区域 `(1800,0 1080×1800)`，其余不变量与清理全部通过；用户确认黑屏后先出现洋红色，随后全屏黄色直到正常恢复，全程符合层级预期。

重复矩阵最终为：纵→横 `5/5` 完整 GuardVisible；横→纵 4 次完整、1 次 Inconclusive，最终补测明确观察到第一帧“上方小部分洋红、其余黑色”，随后完全洋红并由黄色接管。所有运行的自动结构、正式 Replace、层级和清理均通过。该工具因此保留为反例与研究诊断：它证明预置窗口会被揭示，但不能证明首帧原子全覆盖。项目不进入 TG1，也不会将 Guard 接入生产 Surface 生命周期。

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

P1.7 的恢复入口继续使用 `--raised-desktop-product-candidate-recovery --confirm-explorer-restart --target-display all`。附加 `--fault-phase surface-attached|first-frame-presented --fault-action fatal|exit|suppress --target-surface-ordinal 2` 时，故障只在目标 Renderer 的第二次对应事件生效：首次双屏 Apply 不受影响，Explorer generation 重附着时触发整批 abandon，随后以全新 Renderer/Surface 重建。恢复结果由有界 Host journal 直接给出 `Succeeded/Failed/Cancelled/CircuitBroken` 与独立的 `Reattached/Rebuilt`，不通过轮询 Session 数量推断；命令仍会真实终止 Explorer，必须保留显式确认参数。

P1.8 的显式热插拔入口为 `--raised-desktop-product-candidate-topology --confirm-display-change --topology-changes 2 --duration-seconds 2`。它先在初始全部显示器上运行正式产品候选，然后每次输出 `TOPOLOGY_CHANGE_READY` 并最多等待十分钟，由用户执行已经明确同意的物理断开或重连；等待期间测试色块保持活动，不把等待用户操作的时间误解为 `duration-seconds`。每个变化都经真实 `DisplayTopologyChangedCommand` 和可等待 reconciliation terminal 处理，输出 revision、计划类型、活动/退休 Session、阶段时间线与最终清理；工具本身不禁用、不拔插或重启显示设备。若负坐标屏幕的加入/移除改变 Progman 原点，主屏也必须随批次重建，避免复用坐标基准已经失效的 child HWND。

同一入口也用于主屏切换实机门禁。若采样确认只有 `IsPrimary`（以及允许忽略的刷新率）发生变化，而 DisplayId、device path、bounds 和 DPI 均保持不变，工具会额外要求 `NoChange`、无新 Host generation、零 `SurfacesReplaced`、零 retirement，且切换前后的 SessionId 集合完全相同；任一条件不符即返回非零。若 Windows 在切换主屏时同时重排 bounds 或虚拟桌面原点，则该变化不属于 primary-only，必须按正常 `Reconfigure` 处理，不能为了得到 NoChange 而忽略几何变化。

2026-09-14 在 `26200.9445 / session=6 / WinSta0\Default` 完成一次真实主屏切换。初始为 200% 内置主屏 `(0,0) 2880×1800` 和 150% 负坐标外接屏 `(-3840,-156) 3840×2160`；把外接屏设为主屏后，Windows 同时把外接屏移至 `(0,0)`、内置屏移至 `(3840,156)`，虚拟桌面原点发生变化，所以诊断正确输出 `kind=Reconfigure` 而非 primary-only `NoChange`。generation 2 约 1.30 秒完成一次 `SurfacesReplaced`，两个旧 Session 均退休并清理；最终 `valid=True`、`finalGraceful=True`、`finalClean=True`、`ownedWindowsRemain=False`。用户确认两屏先同步显示无覆盖橙色，切换后同步变为黄色，无黑屏、闪烁、半屏提交或图标/任务栏/普通窗口遮挡，随后恢复静态壁纸。该样本关闭当前机器“主屏切换伴随坐标原点重排”的实机门禁；几何完全不变的 primary-only 分支由自动测试覆盖。

同日完成外接屏 scale-only 样本。初始仍为 200% 内置主屏和 `(-3840,-156) 3840×2160@150%` 外接屏；只把外接屏缩放改为 175%，DisplayId、device path、bounds、Primary 和虚拟桌面原点均未变化。诊断输出 `Reconfigure + Succeeded`、`retiredSessions=1`、一次 `SurfacesReplaced`、`scaleOnlyChangeObserved=True`、`scaleOnlyChangeInvariantValid=True`，约 1.09 秒提交；内置屏原 Session 保留，外接屏旧 Session 退休，最终 graceful/clean 且无自有 HWND。用户确认内置屏橙色一直保持到最终清理，外接屏缩放稳定后变为黄色，两屏均无黑屏或覆盖。缩放瞬间应用窗口按新 DPI 放大、桌面产生类似刷新的系统 UI 重布局是 Windows DPI 行为；由于壁纸没有消失、反复换色、越过图标层或错误重建内置屏，不记为 LiveWall 呈现闪烁。

随后尝试内置屏分辨率样本。Windows 实际把内置屏从 `2880×1800@200%` 同时改为 `2560×1600@175%`，外接屏保持 `(-3840,-156) 3840×2160@175%`，所以该样本必须分类为 bounds+scale 联合变化，不能冒充纯 bounds-only。虚拟桌面原点不变，诊断输出 `Reconfigure + Succeeded`、`retiredSessions=1`、一次 `SurfacesReplaced`，约 1.01 秒提交；仅内置屏 Session 被替换，外接屏原 Session 保持，最终 graceful/clean 且无自有 HWND。用户确认外接屏全程保持橙色，内置屏随 Windows 完成尺寸重布局后变为黄色，两者几乎同步稳定，没有黑屏、覆盖或壁纸异常闪烁，随后统一恢复静态壁纸。该证据关闭单屏 bounds+scale 联合变化门禁，纯分辨率变化仍需单独样本。

再次把内置屏从 `2560×1600@175%` 改为 `1920×1200` 时，Windows 又自动将缩放改为 125%，所以计划内 revision 仍是 bounds+scale 联合变化。该 revision 自动输出 `Reconfigure + Succeeded`、`retiredSessions=1`、一次 Replace、约 1.06 秒提交，外接屏 Session 保持，最终清理通过；人工观察与上一样本一致。用户随后在工具只要求一个 topology change 的情况下又把内置屏从 125% 手动调回 175%；第二个 revision 发生在最终观察/清理阶段，桌面图标迁移到外接屏且设置窗口边缘短暂出现纯蓝。旧诊断未继续采样该 revision，故这段只能标记为 `Inconclusive / Unobserved second revision`，不能归因成 LiveWall 产品闪烁，也不能算恢复通过。事后只读采样确认稳定拓扑为内置屏 `1920×1200@175%`、外接屏 `3840×2160@175%`，Shell 可用且无 LiveWall/第三方桌面窗口残留。

为防止同类结果再次被误报，串行 topology 诊断现在会在每个指定 revision 完成后，以 100 ms 间隔持续采样整个 `duration-seconds` 观察期；若 DisplayId、device path、bounds、scale、刷新率或 Primary 再次变化，输出 `TOPOLOGY_OBSERVATION_CHANGED` 并以 inconclusive 非零退出，不再沿用前一 revision 的 `valid=True`。若计划包含多个用户操作，应显式使用相应 `--topology-changes` 数量并等待每次 `TOPOLOGY_CHANGE_READY`，不能在前一阶段后自行追加操作。

P1.8.1 另提供非串行入口 `--raised-desktop-product-candidate-topology-churn --confirm-display-change --topology-changes 4 --duration-seconds 2`。它输出一次 `TOPOLOGY_CHURN_READY` 后持续采样物理变化；每个通过 350 ms 稳定采样的状态立即发布给 Host，不等待上一 reconciliation 完成。被稳定采样合并、从未发布的物理瞬态不伪造 revision。为使显示硬件稳定时间较长的机器仍能形成真实抢占，诊断组合根会在每个 topology Apply 完成 FirstFrame 后、交给 Host 提交前保持 20 秒；更高 revision 会取消该保持并把 fully-prepared 结果送回真实 Host stale-revision 清理路径。该屏障不进入生产协调器、Renderer Protocol 或配置。每个已发布 revision 都必须产生终态：仍在准备的中间 `Reconfigure` 应为 `Cancelled/Superseded` 且零 Replace，同步完成的 `NoChange/Suspend` 可以成功，最终稳定 `Reconfigure` 只能执行一次批量 Replace。工具只观察用户已明确同意的物理操作，不主动禁用最后一块显示器；真实零显示器无法安全产生时应登记为 `EnvironmentInconclusive`。

P1.8.2 的代表性 Span 实机入口在串行 topology 命令上增加 `--layout-mode span`，例如 `--raised-desktop-product-candidate-topology --confirm-display-change --layout-mode span --topology-changes 1 --duration-seconds 5`。该参数只改变诊断 composition root 创建的 Assignment；初始及后续 revision 均继续复用产品 Host、DefaultLayoutPlanner、Raised Surface Factory 和正式 Renderer v1。Span 自动选择全部已连接显示器，期望一个 Session、一个 Renderer、一个物理像素 union Surface、`scaleFactor=1` 和每个成功 revision 一次 Replace；未指定时仍默认 `per-display`。该参数不进入生产配置、协议 DTO 或 Schema。

首次 Span position 运行从外接屏 `Y=-369` 调整到 `Y=-210` 后以 `CompetingDesktopSurface` 失败关闭：旧橙色 Span 被完整保留、没有黄色 Replace，最终正常恢复静态壁纸。原因是旧 Span 本身覆盖虚拟桌面，被下一次 Raised 发现误认为第三方全桌面 Surface。竞争检测随后改为只排除 Factory 按 HWND 生命周期登记的 Host-owned Surface；不按进程名/PID放宽，真正竞争窗口仍拒绝。Release 自动基线为 `218/218`，该失败样本只证明零提交回退安全，必须修复后复测才可登记 Span 重配置通过。

修复后复测从外接屏 `Y=-210` 调整到 `Y=-91`，结果 `Succeeded/Reconfigure`：一个 Span Session 保持双屏物理像素 union 语义，活动适配 19.322 ms，新首帧后一次 Replace 1042.967 ms，退休一个旧 Span Session。用户确认刷新期间两屏持续橙色且没有露黑、静态壁纸或其他颜色，稳定后同步变黄并同步恢复。最终 `valid=True`、graceful/clean、无自有 HWND，退出码 0。

Span scale-only 复测把外接屏从 150% 改为 175%，bounds、位置、刷新率和虚拟原点均不变。自动结果 `scaleOnlyChangeObserved=True / scaleOnlyChangeInvariantValid=True`，活动适配 18.598 ms、一次 Replace 1066.147 ms、活动/退休 Span Session 各 1；用户确认 DPI 刷新期间两屏持续橙色且无黑屏/露底，稳定后同步变黄并同步恢复。最终 `valid=True`、graceful/clean、无自有 HWND，退出码 0。

Span 成员 `2→1→2` 往返实机通过。断开阶段从双屏 Span 到单屏 Span，在 1008.265 ms 一次 Replace，主屏无黑屏/露底并直接变黄；重连阶段单屏黄色持续到外接屏可显示，首次结构探测遇到暂态 mutation 后按既有策略重试，于 2184.797 ms 一次 Replace 为双屏橙色 Span。两个 revision 都是 `Succeeded/Reconfigure`，每阶段活动/退休 Session 各 1，最终 `valid=True`、graceful/clean、无自有 HWND，退出码 0。

同一 Host 的布局模式切换使用 loop 诊断专用 `--layout-sequence`，例如 `--raised-desktop-product-candidate-loop --target-display all --iterations 2 --layout-sequence per-display,span --duration-seconds 5`。序列必须与 iterations 等长；每代仍等待全部 FirstFrame 后一次 Replace，并按实际 Session 数量聚合 retirement。该选项不改变普通 loop 的默认 PerDisplay 行为，也不进入生产配置或 Renderer Protocol。

上述 `PerDisplay→Span` 实机运行通过：generation 1 为 2 Session/2 Surface、1351.030 ms；generation 2 为 1 Session/1 Surface、988.353 ms。两个旧 PerDisplay Renderer 以 Replacement、最终 Span Renderer 以 Shutdown 全部 graceful retirement，`ShutdownCompleted`、Dispose 和 Surface cleanup 均成功；无自有 HWND、新增 WorkerW或退出错误。用户确认两屏橙→黄全程同步，无黑屏、静态壁纸、半屏或覆盖，最后同步恢复。

2026-09-18 的 `26200.9457 / session=2 / WinSta0\Default` refresh-only 样本通过：内置主屏保持 `2880×1800@200%`，仅从 120 Hz 改为 60 Hz；外接屏保持 `3840×2160@150%`。两屏 DisplayId、bounds、scale 和虚拟原点不变，revision `1→2` 的终态为 `Succeeded/NoChange`、`generation=none`、活动 Session 2、retirement 0，人工观察测试色全程不变且无覆盖，最终 graceful/clean、无自有 HWND。此前一次外接屏“刷新率”操作实际把模式改为 `3840×2160@150% → 1280×1024@100%`，因此只登记为成功的 bounds+scale+origin Reconfigure，不能冒充 refresh-only。

同日三次主屏 bounds 尝试都被 Windows 联动缩放，因此不能登记为纯 bounds-only。缩小 `2880×1800@200% → 2560×1600@175%` 时仅主屏橙→黄、外接屏持续橙色；扩大 `2560×1600@200% → 2880×1800@225%` 时仍只替换主屏，自动终态为 `Succeeded/Reconfigure`、活动适配 24.880 ms、Replace 1034.568 ms、最终 graceful/clean 且无自有 HWND，但人工观察到主屏右侧和下侧在橙→黄之间短暂露出静态壁纸。第三次预设相同 scale 后仍得到 `2560×1600@225% → 2880×1800@250%`；人工未见露底，但进程输出句柄在读取前已回收，故不计自动样本。pure bounds-only 在本机登记为 `EnvironmentInconclusive`，不再通过反复切换模式追求；扩大样本保留 `TransientCoverageGap`，不重启 TG1。

同日 position-only、origin-unchanged 样本通过：主屏保持 `(0,0) 2880×1800@200%`，左侧外接屏只从 `(-3840,14)` 移到 `(-3840,144)`，虚拟原点仍为 `(-3840,0)`。终态为 `Succeeded/Reconfigure`，活动适配 23.270 ms、一次 Replace 1047.736 ms、只退休外接屏一个 Session；用户确认主屏全程橙色不变，外接屏刷新后仍保持旧橙色，稳定后变黄，最终两屏一起清理恢复。结果 graceful/clean、无自有 HWND。

同日 origin-changing position 样本也通过：外接屏从 `(-3840,144)` 移到 `(-3840,-369)`，虚拟原点从 `(-3840,0)` 变为 `(-3840,-369)`，Progman bounds 为 `(-3840,-369) 6720×2169`。终态为 `Succeeded/Reconfigure`，活动适配 34.231 ms、批量 Replace 1315.575 ms、活动 Session 2、退休 Session 2；两屏同步刷新并同步变黄，最终 graceful/clean、无自有 HWND。主屏下部短暂露黑按 ADR-009 边界登记为 `TransientCoverageGap`，不是半批提交或附着层级失败。

第一次真实 churn 运行成功采集 `2→1→2→1→2` 四个 revision，四次均完整 Reconfigure、最终优雅清理且无自有窗口残留；用户确认每次稳定态测试色与层级正确。由于人工/硬件稳定间隔使前三次都在下一 revision 发布前完成 Replace，严格结果为 `valid=False / intermediateReplacements=3`。该样本只证明连续完整重规划安全，不能登记为抢占通过；因此随后加入上述诊断提交前屏障后重新执行。

第二次真实 churn 运行在相同 `26200.9445 / session=3 / WinSta0\Default` 环境通过：四个物理状态均发布，revision 2、3、4 全部为 `Cancelled/Superseded`，`intermediateReplacements=0`；最终 revision 5 在暂态 Shell mutation 有界重试和 20 秒诊断保持后恰好一次双屏 Replace。最终 `valid=True`、`finalSessions=2`、`finalGraceful=True`、`finalClean=True`、`ownedWindowsRemain=False`，退出码 0。用户确认初始已提交橙色双屏在四次插拔全过程持续保持、两屏均不遮挡图标/任务栏/普通程序，最后一次接入后统一恢复静态壁纸。该证据关闭 P1.8.1 的真实快速 revision 抢占门禁；不覆盖主屏切换、完整 DPI/bounds 或真实零显示器。

2026-09-12 的首次真实断开样本成功识别显示器 `2 → 1`，随后发布第二阶段等待标记；主屏因负坐标外接屏移除导致 Progman 原点变化而整批重建，人工观察为断开瞬间约 1 秒 Windows 黑屏，随后黄色 Surface 正常显示且系统层级正常。由于第二阶段原 2 分钟交互预算在用户反馈前到期，工具清理后恢复原壁纸并以 Harness timeout 退出；该样本证明第一阶段变化被接受，但没有得到完整 transition 结果，也不构成重连验收。等待预算随后调整为每阶段 10 分钟。

第二次真实运行再次成功识别 `2 → 1`：人工观察为短暂 Windows 黑屏、旧橙色短暂恢复、桌面图标刷新、随后新 generation 黄色主屏，层级正常。重连 `1 → 2` 也被正确采集，但 Raised Adapter 在 350 ms 结构复验中返回 `UnexpectedTopologyMutation`，说明显示设备稳定事件早于 Explorer Progman/WorkerW 子树稳定；Host 未做半提交，诊断随后清理黄色 Session，两屏恢复静态壁纸。该样本记为“断开通过、重连暂态失败关闭”，不记为重连能力通过。产品状态机随后仅针对该强类型暂态结构变化增加 350 ms、1 s、2 s 有界退避；永久结构拒绝仍不会重试或放宽。

第三次运行在断开瞬间暴露了另一层非原子性：`EnumDisplayMonitors` 暂时仍返回 `\\.\DISPLAY2`，而同轮 `QueryDisplayConfig` 已不再包含该 target path。旧代码在 Win32 枚举回调中抛出 `InvalidOperationException`，异常跨非托管边界终止进程，所以用户只看到静态壁纸恢复。修复后回调只记录失败并返回；托管边界最多五次重采完整 DisplayConfig+monitor 快照，仍不一致才返回明确失败。`WindowsDisplayTopologySource` 的后台刷新另外按 350 ms、1 s、2 s 重试，诊断轮询也把该不一致视为暂态。没有稳定 target path 时仍不会伪造 DisplayId。

第四次完整运行在 `26200.9445 / session=3 / WinSta0\Default` 通过。初始拓扑为 200% 主屏 `(0,0) 2880×1800` 与 150% 负坐标外接屏 `(-3840,-156) 3840×2160`。断开被识别为 revision `1→2 / 2→1`，generation 2 于约 1.02 秒以 `Reconfigure + Succeeded` 提交，2 个旧 Session 均清理；重连被识别为 revision `2→3 / 1→2`，generation 3 首次准备在约 518 ms 因暂态拓扑拒绝，350 ms 退避后重试成功，并于约 2.28 秒同时提交 2 个 Session。最终 `valid=True`、两次 transition 均成功、`finalGraceful=True`、`finalClean=True`、`ownedWindowsRemain=False`。人工观察为：断开时短暂 Windows 黑屏后黄色主屏正常；重连稳定前外接屏黑屏、主屏保持黄色，随后两屏短暂同步黑屏并一起显示橙色，图标、任务栏和普通程序始终在橙色上方，最后两屏同步恢复静态壁纸。稳定前可能出现的瞬时主屏颜色变化不作为单独提交证据；自动时间线确认稳定拓扑只发生一次双屏批量 Replace。

2026-09-10 在 `26200.9168 / session=7 / WinSta0\Default` 完成首次 `--target-display all --duration-seconds 5` 双屏产品候选。拓扑为主屏 2880×1800@200%/120 Hz `(0,0)`，外接屏 3840×2160@150%/60 Hz `(-3840,-156)`。自动结果为 `ValidSuccess`：两个独立 Session/Surface 均产生 FirstFrame，于约 1.31 秒后只执行一次批量 Replace；两个 Renderer 均 `ShutdownCompleted`、Dispose 和 Surface cleanup 成功，Shell generation/显示拓扑不变，直属 WorkerW 前后均为 1，无 LiveWall HWND 或新增 WorkerW。用户人工确认两屏同步完整显示橙色约 5 秒并同步结束，外接屏无边缘缺口，桌面图标、任务栏和普通程序始终在上方，没有黑屏。该结果关闭双屏基础 PerDisplay 呈现门禁，但不覆盖长驻、故障、Explorer、热插拔或主屏切换矩阵。

同日随后执行 `--target-display all --soak-seconds 90`。自动结果为 `ValidSuccess`：Apply 在约 1.302 秒完成，两个 Surface 跨越旧 52/82 秒 Apply deadline 后继续保持活动；90 秒结束时批次 `graceful=True`，两组 `ShutdownCompleted/RendererDisposed/SurfacesCleaned` 全部为真、`issues=none`。清理后 `ownedWindowsRemain=False`，Progman 直属 WorkerW 前后均为 1、无新增 WorkerW，Shell generation 和显示拓扑均未改变。用户人工确认直到最终恢复为止，两屏全程同步显示且没有闪烁、黑屏、提前恢复或图标/任务栏/普通程序层级异常。该样本关闭首个双屏长驻门禁，但不替代 process-cold、同 Host 连续 Apply、Explorer、热插拔和故障矩阵。

同日又执行 Release 双屏 10 次 process-cold 批次，得到 `requested=10 / executed=10 / valid=10 / successful=10`，没有补跑、产品失败、环境变化或 Harness 失败。10 份结果均为两个 Session、Apply `Succeeded`、graceful retirement、ShutdownCompleted、Renderer Dispose、Surface cleanup、无 retirement issue、无自有 HWND/新增 WorkerW，且 Shell generation/显示拓扑稳定。Apply 耗时 minimum/median/maximum 为 1245.6/1257.1/1285.2 ms，最后首帧为 1227.7/1238.1/1267.7 ms，批次退休为 43.8/60.6/80.3 ms。用户确认批次期间双屏均未遮挡桌面图标、任务栏或普通程序；未把未明确观察的逐轮视觉同步扩大为人工结论。

随后执行 `--raised-desktop-product-candidate-loop --target-display all --iterations 20 --duration-seconds 1`。20 个 generation 全部成功，每代两个 Session/Surface 和两个 FirstFrame 后仅一次 Replace；40 个 Renderer 全部 graceful retirement，其中 38 个由 Replacement、最后 2 个由 Shutdown 触发，全部 `ShutdownCompleted/RendererDisposed/SurfacesCleaned=True` 且 `issues=none`。最终 `allSucceeded/allRetired/triggersCorrect=True`、`ownedWindowsRemain=False`、`addedWorkerCount=0`。用户人工确认两屏全程同步交替橙黄，过程中无黑屏、无图标/任务栏/程序覆盖，最后正常恢复。该结果关闭 P1.6 当前双屏可靠性切片，但不覆盖 Explorer、热插拔、会话切换和故障注入矩阵。

P1.7-D 正向产品恢复已在同一双屏环境通过：命令 `--raised-desktop-product-candidate-recovery --confirm-explorer-restart --target-display all --duration-seconds 5` 得到 `Succeeded + Reattached`，旧、新 Explorer PID/HWND generation 均不同，两个 Renderer PID 与 SessionId 保持不变；两个新 Surface 首帧齐备后仅一次 Replace，最终优雅退休且无自有 HWND。人工确认双屏同步橙色、Explorer 重启期间同步黑屏、Shell 恢复后同步黄色；两个色块阶段均无闪烁或遮挡图标、普通窗口和任务栏。

恢复定点故障可在同一入口附加 `--fault-phase surface-attached|first-frame-presented --fault-action fatal|exit|suppress --target-surface-ordinal 2`。计划默认只在目标阶段第二次出现时触发，因此初次 Apply 不受影响；恢复批次任一屏失败后不得提交已成功的另一屏，Host 必须 abandon 全批旧/新 generation 资源，并按 350 ms、1 秒、2 秒预算创建全新 Renderer/Surface。`first-frame-presented + fatal` 与 `surface-attached + fatal` 两个实机代表样本均得到 `Succeeded + Rebuilt`：第二屏恢复失败后没有半屏 Replace，约 350 ms 后 generation 3 的双屏新 Renderer 一次提交，最终优雅退休且无自有 HWND。用户确认两次实验均双屏全程同步，橙色、Shell 黑屏、重建橙色和最终恢复动作符合预期，无颜色分裂、长期黑屏、闪烁或层级异常。

同日 P1.7-A 正式进程 9 项矩阵全部得到 `ExpectedFailureValidated`；P1.7-B 的第二屏 replacement FirstFrame Fatal 保持旧双屏橙色且 Generation C 可继续成功；P1.7-C 的旧第二 Renderer `ShutdownCompleted` 抑制明确记录 `ShutdownTimeout`，新双屏不回滚并最终正常退出。双屏产品级 Explorer 正向恢复也得到 `Succeeded + Reattached`：Explorer PID/HWND generation 改变，两个 Renderer PID 保持不变，新旧 Host/child HWND 不复用，恢复后一次批量 Replace 且最终优雅清理。用户确认两屏同步经历橙色、Explorer 黑屏、Shell/图标/任务栏恢复、黄色，橙黄期间均无闪烁或覆盖。黑屏只属于本诊断显式终止 Explorer 的允许窗口，不构成无缝恢复承诺。

P1.9.1 新增两个复用真实 `WindowsHostRuntime`、事件桥和命令循环的入口：

```powershell
DesktopHostDiagnostics.exe --raised-desktop-host-taskbar-noop --target-display all --signal-count 10
DesktopHostDiagnostics.exe --raised-desktop-host-explorer-event-recovery --confirm-explorer-restart --target-display all --duration-seconds 5
```

第一个入口向 LiveWall 自有隐藏信号窗口投递真实注册的 `TaskbarCreated` 消息，但不改变 Explorer。`26200.9457 / WinSta0\Default` 双屏结果为 `10 raw / 0 validated shell changes / 0 recovery / 0 Replace / 0 retirement`，generation 与两个 Session 身份保持，清理完整。第二个入口只终止旧 Explorer，不直接投递 `ExplorerRestartedCommand`；正式事件链观察到 Explorer PID `11908→37580`、一次原始信号、一次结构确认、一次 recovery operation、一次批量 Replace，额外 fallback 周期没有重复恢复。终态为 `Succeeded + Rebuilt`，不是 `Reattached`；人工观察两屏同步黑屏、Shell 恢复、同步橙色后还原，全程没有半屏、不同步或覆盖图标/任务栏/普通窗口，最终无 LiveWall HWND 残留。

P1.9.2 增加两个只等待真实系统事件、不会主动锁屏或发起睡眠的入口：

```powershell
DesktopHostDiagnostics.exe --raised-desktop-host-session-lock --target-display all --duration-seconds 5
DesktopHostDiagnostics.exe --raised-desktop-host-sleep-resume --target-display all --duration-seconds 5
```

命令输出 `SYSTEM_TRANSITION_ARMED` 后，用户在三分钟内手工执行锁定/解锁或睡眠/唤醒。人工等待不使用 Apply/retirement deadline；事件确认后另用有限恢复/反馈预算。每次可用 `--result-json artifacts/diagnostics/<独立样本>.json` 保存系统 revision、策略决定、实际 Pipe 命令写入、Renderer 协议回执、恢复终态和完整 retirement。异常样本保存 `TransitionIncomplete`（外部取消为 `Cancelled`），缺少清理验证不得写成通过。

锁解且环境稳定时要求 Host generation、Replace/retirement 数量和 Session/Renderer 身份不变。睡眠恢复若 Shell/拓扑改变，则等待已有 recovery/reconciliation 明确终态并单独记录，不以身份变化误判失败，也不把重建写成身份保持。全部样本必须有 Suspend 和最终播放状态的实际协议证据、正常 Shutdown、Renderer Dispose、Surface 清理、无自有 HWND/新增 WorkerW。增加 `--pause-before-transition` 验证用户暂停→锁定→解锁后保持 Paused。橙色不变不证明暂停，测试 Renderer 无自主帧循环。

工具不会主动锁屏、发起睡眠、禁用显示设备或修改生产 allowlist。2026-10-11 补齐显式 suspend/resume 通知注册与释放后，Debug/Release 自动基线为 `246/246`。物理单屏启动/退出、锁解 3/3、用户暂停锁解 1/1、睡眠恢复 2/2 均通过；首个睡眠 `TransitionIncomplete` 样本继续保留，不覆盖。样本环境为 `26200.9457 / WinSta0\Default / S0 Modern Standby`；双屏系统恢复待补测，不因此接受 Stage B。

### P1.9.3.1 真实会话断开与重连入口

首版范围：RDP 动态壁纸呈现不支持；RDP 实机为 `OutOfScope / WaivedForV1`，不是通过，也不阻塞后续开发。以下入口保留用于本地用户切换等安全环境下的诊断；通用会话保护不删除。本地用户切换实机当前为 `NotValidated`。

```powershell
DesktopHostDiagnostics.exe --raised-desktop-host-session-reconnect --target-display primary --duration-seconds 5 --result-json artifacts/diagnostics/session-reconnect-01.json
```

复用正式 Runtime、事件桥及会话环境终态，不主动断开、切换用户、注销、启用 RDP 或重启 Explorer。输出 `SESSION_RECONNECT_ARMED` 后才由用户按事先确认的方式操作；人工操作预算独立为十分钟。必须返回原用户的同一 Session；不要注销原账户，否则 Host 进程会退出。只有锁屏/解锁通知、不存在真实断开/重连时，结果为 `TransitionIncomplete`，不能当作通过。

结果保存 armed 后的通知序号、当前 Session 匹配结果、validation revision、终态、Resume/Reconfigure/Rebuild 路径、阶段失败信息、实际播放命令与独立协议回执，以及关停清理证据。失败终态保留为 `ProductFailure`；人工未完成或事件缺失保留为 `TransitionIncomplete`；清理未经验证时明确写 `Unavailable`。稳定环境仍要求身份、generation、Replace/retirement 不变；环境变化则等待 recovery/reconciliation 终态，不把 Rebuild 写成 Resume。返回后同时验证 WinSta0\Default、原 Session 和 Shell 可用。

可附加 `--pause-before-transition` 验证重连后保留用户暂停。静态色块不能证明动态帧停止。默认入口仍是诊断，不写配置或 allowlist。2026-10-11 Debug/Release 自动基线 `277/277`，Release 工具零警告/错误；真实会话样本尚未执行。本机 EditionID 为 `CoreCountrySpecific`，原生 RDP 入站实机样本受环境限制；切换用户须先确认已有账户和真实通知序列。

### P1.9.4-C Shell/display 并发实机入口

```powershell
DesktopHostDiagnostics.exe --raised-desktop-host-environment-recovery --confirm-display-change --confirm-explorer-restart --target-display all --duration-seconds 5 --result-json artifacts/diagnostics/environment-recovery-01.json
```

必须先得到用户对色块和 Explorer 重启的明确同意。默认目标仍为 primary；双屏测试显式选择 all。用户预先打开显示设置，等 `ENVIRONMENT_RECOVERY_ARMED` 后在 180 秒内只修改一次目标屏缩放，并保留更改，不拔插、旋转、锁屏、睡眠或重复操作。工具只在正式 Host 拓扑准备仍在途时重启 Explorer 一次；`ENVIRONMENT_RECOVERY_RESTARTING` 后允许短暂黑屏和图标/任务栏恢复，随后等待橙色完整覆盖并在指定观察时间后正常退出。不会自动还原显示设置；用户如需还原，须等诊断退出后手动处理。

结果通过必须有 confirmed Shell generation 抢占旧 reconciliation 的证据、一个成功 recovery、一次批量 Replace、最新 revision 的精确 Surface bounds、Renderer 实际退出及资源清理。没有重叠为 `EnvironmentInconclusive`（独立退出码 9），不是通过或自动重试理由；溢出为 HarnessFailure，异常记录 phase/type 和 DiagnosticIncomplete（外部取消为 Cancelled），不得假定清理成功。逐 revision/recovery 时间线和清理证据保存 JSON，不包含认证令牌、Pipe 名或 Shell HWND/PID。Shell WorkerW 前后计数仅说明是否新增，不证明未公开请求的可撤销性。当前 Debug/Release `302/302`；实机尚未执行，Stage B 仍未接受。

验收矩阵和下一步子窗口/Z-order 诊断方向见 `docs/windows-desktop-host.md`；实时状态见 `docs/implementation-status.md`。
