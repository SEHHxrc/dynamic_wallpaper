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
```

Raised 预检要求 `WinSta0\Default`、`GetShellWindow == Progman`、Progman 具有 `WS_EX_NOREDIRECTIONBITMAP`、直接 DefView 具有 `WS_EX_LAYERED`、双方属于同一 Explorer 进程，且 350 ms 内结构指纹稳定。结果只携带不包含 HWND/PID 的 SHA-256 结构指纹和强类型拒绝码；完整 HWND/PID 只显示在本次运行的临时快照中。

Raised 诊断退出码：结构拒绝为 5；请求后 Shell WorkerW 未自行恢复为 6（`CleanupIncomplete`）；Explorer 重建失败为 7。普通探针绝不重启 Explorer；只有命令同时包含 `--raised-desktop-explorer-recovery-probe` 和 `--confirm-explorer-restart` 才允许终止当前 Progman 所属 PID，缺少任一参数都会在修改 Shell 前拒绝。色块窗口销毁成功不代表 Shell 请求已撤销。

## 色块模式边界

专用 Window Dispatcher、hidden provisional Surface 和 `ReplaceSurfacesAsync` 已落地，色块入口也已实现。该模式只允许显式启动，设置自动超时，并在正常退出、取消或异常时销毁全部窗口；默认运行继续保持只读。

色块模式允许诊断代码尝试尚未进入生产 allowlist 的候选，但仍执行结构验证并失败关闭。2026-09-06 已在 `26200.9168`、`session=2 / WinSta0 / Default` 完成第三方动态壁纸退出后的连续采样和 Explorer 重启复采。旧 Explorer 一度保留 `Progman` 子 `WorkerW`；重启后稳定基线只有 `Progman → SHELLDLL_DefView → SysListView32 → SysHeader32`，没有符合既定 Legacy WorkerW 规则的候选。限时色块尝试因此在创建 Surface 前拒绝附着，前后拓扑不变。这不是工具错误，也不能通过把 `Progman` 直接当作附着点来绕过。工具入口已实现不等于真实图标层级、首帧交换或 Explorer 恢复已经验收。

同一 build 的隔离 Raised 实验得到的是结构正结果而非可见附着能力：`0x0D/0x01` 唯一新增了全屏、同 Explorer、无 owner、位于 DefView 后方的 Progman 直接子 WorkerW；修正后的 layered GDI 色块自动快照验证了 `DefView(z0) > LiveWall(z1) > WorkerW(z2)`，但显式向 DC 填充洋红像素、强制刷新 GDI 并等待 DWM 提交后，人工观察仍完全没有变化。因此该结构未进入当前桌面的实际可见合成路径，不得称为 attach point 或启用生产 `RaisedDesktopAdapter`。

诊断输出已拆分为 `OwnedResourcesCleanup` 与 `ShellMutationRecovery`。`OwnedResourcesCleanup=Complete` 只表示 LiveWall 自有 HWND 已销毁；`ShellMutationRecovery=NoMutationObserved` 只表示本次调用没有引入新的 Shell 结构，不会覆盖更早实验留下的副作用。初次请求生成的 Shell WorkerW 仍保留，整体 shell mutation recovery 仍必须记录为 `CleanupIncomplete`。工具不会静默重启 Explorer；经用户明确授权的 Explorer 重启只能恢复诊断环境，不能证明请求本身可撤销。

non-layered GDI 探针已实现并完成一次 5 秒运行：输出 `PresentationProbeCompleted`、`presentationKind=NonLayeredGdi`、`OwnedResourcesCleanup=Complete`、`ShellMutationRecovery=NoMutationObserved`，但人工观察仍完全没有桌面变化，最终判定为 `PresentationFailed`。`PresentationProbeCompleted` 只表示探针执行完毕；人工失败后不得产生 `RenderableDesktopAttachment`。

DirectComposition/交换链探针现已实现：硬件 D3D11 composition swap chain 绑定 LiveWall 自有 `WS_EX_NOREDIRECTIONBITMAP` HWND，而不是绑定 Explorer 的 Progman。首次 5 秒实验人工确认真实像素可见，但窗口从顶层样式转换后残留 `WS_CAPTION`，交换链客户区没有覆盖上、左非客户区。探针改为直接创建无边框 Progman 子窗口后，第二次运行自动结果为 `PresentationProbeCompleted`、`OwnedResourcesCleanup=Complete`、`ShellMutationRecovery=NoMutationObserved`，活动窗口为 `0,0 2880x1800`、style `0x5E000000`，仍保持 `DefView > LiveWall > WorkerW`；人工确认完整覆盖壁纸区域，桌面图标和任务栏符合预期。该结果形成单屏 `RenderableDesktopAttachment` 诊断证据，但不会自动修改生产 allowlist。

显式 Explorer 恢复实验已完成一次自动与人工成功运行：只有在第一个 DComp Surface 活动快照验证通过后才终止当前 Progman 所属 Explorer；工具观察到 Shell PID 与核心 HWND generation 全部变化，等待新 Progman 子树稳定后重新运行 Raised 请求并创建全新的 DComp Surface，结果为 `generationChanged=True`、`rebuilt=True`、`OwnedResourcesCleanup=Complete`。稳定性门禁只比较 Progman 与其后代，忽略无关顶级窗口的全局 Z-order 波动；完整报告指纹仍保留全部采样信息。人工观察序列为“旧洋红短闪 → Shell 黑屏重建 → 新黄色完整显示且不遮挡任务栏 → 数秒后还原”。

该恢复实验的 Host Surface HWND、DComp target 和交换链仍由同一诊断进程拥有，因此只验证 Shell attachment 与 generation 重建，不验证现有跨进程 Renderer Protocol。下一项诊断必须是隔离的 Renderer-child DirectComposition 探针：父进程创建生产等价 Host 容器，独立辅助进程通过现有 `AttachSurface` 语义接收容器 HWND，在其中创建自有 child HWND，并将自有 DComp target/交换链绑定到 child。若该路径可见，协议继续保持 v1；只有它失败且证据证明必须跨进程共享 GPU 资源时，才进入协议 1.1。该探针尚未实现，不得把本文描述当作已有命令。

验收矩阵和下一步子窗口/Z-order 诊断方向见 `docs/windows-desktop-host.md`；实时状态见 `docs/implementation-status.md`。
