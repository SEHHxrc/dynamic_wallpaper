# Windows Desktop Host 设计与验收状态

> 文档性质：Stage B 规范设计和验收边界。核心机制已经实现并通过自动化测试，但真实 Windows Shell 兼容矩阵尚未通过；根目录 `ARCHITECTURE.md` 仍是最高约束，实时状态见 `implementation-status.md`。

## 设计结论与当前落地状态

| 事项 | 当前状态 | 持续约束 |
|---|---|---|
| Raised Desktop 能力 | 两种 GDI 失败；无边框 Host-owned DComp、独立 Renderer-child v1 `HwndChild` binding、同 Renderer 跨 Shell generation 功能恢复均通过；Platform 内部 Lease/Factory 已接线；生产 Adapter 因空 allowlist 不参与选择 | P1 / Stage B 关键路径；正式 Renderer 会话、完整矩阵、Shell mutation 策略和独立 allowlist 完成前不得启用 |
| Surface 窗口线程所有权 | 已实现，待真实验收 | 使用 Host 进程级专用 Window Dispatcher，所有自有 HWND 操作封送到该线程 |
| 首帧 Surface 切换 | 已实现，待真实验收 | `CreateSurfaceAsync` 创建隐藏 provisional Surface；由批量 `ReplaceSurfacesAsync` 显式提交 |
| Explorer/Shell 恢复 | 成功路径已实现；独立 Renderer 同进程跨 generation 重附着、第二次首帧、层级与清理已自动及人工通过；失败分支已改为失效会话、generation-safe abandon、全新 Renderer/Surface 限次退避重建与最终熔断 | 功能恢复正向验收及失败策略自动门禁通过；真实 Explorer 组合故障矩阵仍待验收；显式终止 Explorer 的短暂黑屏不属于无缝恢复承诺 |
| 桌面诊断入口 | 已实现，待矩阵验收 | 提供 Shell/Legacy 诊断及隔离 Raised Desktop 探针与色块；默认运行仍只读 |
| Desktop/Display 防抖 | 已实现 | 固定为 350 ms，位于根架构允许的 300–500 ms 范围 |
| Stage B 总体状态 | `In progress / Not accepted` | Lease/Factory、v1 `HwndChild` binding、正式 Renderer 会话、物理单屏短时以及 P1.6 双屏基础/90 秒长驻/10 次 process-cold/20 轮连续替换已通过；正式局部故障、双屏 Explorer、完整 DPI、热插拔、主屏切换、Shell mutation 和体验优化尚未完成 |

## 1. Shell 适配器边界

WorkerW/Progman/Raised Desktop 不是 LiveWall 可以依赖的公开动态壁纸 API，因此全部视为按 Windows Shell build 验证的适配策略，而不是平台保证。

当前状态：

- `RaisedDesktopAdapter` 已能发现内部 Raised lease，但生产 `ValidatedBuilds` 为空，因此正常 Selector 不会选择它；
- Adapter Selector 随后尝试 Legacy WorkerW；
- Legacy WorkerW 已有发现和结构验证代码，但生产 `ValidatedBuilds` 为空，在真实桌面层级、Explorer 重启、DPI/热插拔矩阵验收完成前仍标记为 Experimental；
- 当前完整 build `26200.9168` 在已验证适配器和现有 Legacy 请求参数下没有合规候选；隔离 Raised 诊断已有单屏正结果，但不改变 Legacy 结论，也不自动形成生产支持；
- 若没有通过验证的附着点，必须失败关闭或回退静态壁纸，不得因为找到了 `Progman` 就宣称支持 Raised Desktop。

一个适配器只有同时满足以下条件才能返回可供生产使用的 attach point：

1. 当前 Shell build 位于该适配器的已验证范围；
2. 重新枚举得到的类名、父子关系、进程身份和窗口层级符合该版本规则；
3. 结构候选可接受测试 Surface，且其窗口位于桌面图标下方；
4. 通过与拟采用 Renderer 提交路径一致的 presentation probe，真实像素在当前桌面可见合成路径中出现；
5. Explorer 重启后能够重新发现，不依赖旧 HWND；
6. 失败不会遮挡图标、任务栏或普通应用窗口。

Build allowlist、层级指纹和验收结果由 `DesktopHostDiagnostics` 采集；未经记录的 build 默认不启用 Raised Desktop。

### 1.1 结构候选、呈现验证与附着能力

Windows 桌面适配必须采用两阶段能力判定，不能再把“找到一个父 HWND”与“壁纸能够显示”视为同一事实：

```text
StructuralCandidate
  → PresentationProbe
  → RenderableDesktopAttachment
  → Host DesktopSurface/container
  → RendererSurfaceBinding
```

- `StructuralCandidate` 只证明 Shell build、父子关系、进程、owner、bounds 与 Z-order 满足规则；`StructurallyValidated` 是该阶段的结果，不是生产附着点。
- `PresentationProbe` 必须用拟投入生产的内容提交类型证明实际像素可见，并验证图标、任务栏和普通应用不被遮挡。
- `RenderableDesktopAttachment` 才是适配器可以交给 Desktop Host 的能力；它必须绑定 adapter id、完整 build/UBR、结构指纹、当前 Shell generation、presentation kind 和已通过的验收矩阵。

Platform.Windows 已在一次 Shell generation 内持有仅限进程内使用的 attachment lease，核心形状为：

```csharp
internal sealed record DesktopAttachmentLease
{
    DesktopAttachmentCapability Capability;
    DesktopSurfacePlacementKind PlacementKind;
    string StructuralFingerprint;
    DesktopShellGeneration ShellGeneration;
    ulong ParentWindowHandle;
    ulong ZOrderAnchorWindowHandle;
    ulong BackdropWindowHandle;
}
```

该类型及 `IDesktopHostAdapter` 均为 Platform.Windows 内部接口。Application 的 `DesktopTopology` 只返回不含句柄的 `DesktopAttachmentCapability(AdapterId, PresentationKind, RendererBinding)`；Shell-owned HWND 不得进入 Domain、持久存储、allowlist 或跨进程协议。Renderer Protocol v1 传递的是 LiveWall Host 自有容器 HWND，不是 Progman/DefView/WorkerW。Explorer/Shell generation 变化后 lease 整体失效并重新发现；替换流程只激活新 generation 的 provisional Surface，把旧 Surface 记为 retired，不再对旧 HWND 执行显隐、换父级或复用操作。

这里必须区分两个正交维度：attachment presentation 描述 Platform.Windows 如何把 Host Surface 放入 Shell 可见合成路径；Renderer binding 描述 Host 和独立 Renderer 进程之间传递什么。Host-owned DComp 探针已经验证前者的一条可见路径；独立 Renderer-child 探针也已验证协议 v1 的 `HwndChild` 结构、IPC 事件顺序、真实像素与正常/取消/崩溃清理：Renderer 在 Host 容器内创建自有 child HWND，并在该 child 上绑定自有 DComp target/交换链。同 Renderer PID 跨 Explorer generation 的第二次 Attach/FirstFrame、干净环境 10 秒持续层级复验和人工恢复序列均已通过；竞争程序自动插入时则明确失败关闭。Shell 重启期间的短暂黑屏属于旧 generation 已销毁而新 generation 尚未可附着的状态，不等同于产品无缝恢复验收。该内部图形实现不要求协议升级；只有跨进程需要传递 HWND 之外的共享纹理、交换链句柄、同步 fence 等 GPU 对象时，才新增强类型 binding。

## 2. 专用 Window Dispatcher

Host 内建立唯一的专用窗口线程。该线程设置为 STA，并运行标准 `GetMessage → TranslateMessage → DispatchMessage` 消息循环。STA 用于给未来 COM/WinRT 互操作提供确定环境；User32 的硬性边界是窗口归创建线程所有，不能由其他线程调用 `DestroyWindow` 销毁。

Dispatcher 独占以下操作：

- 注册/注销窗口类；
- 创建用于接收 Shell 广播的隐藏顶级信号窗口；
- 创建、定位、显隐、换父级和销毁所有 Desktop Surface HWND；
- 执行 Z 顺序验证及同一父窗口内的批量 Surface 交换；
- 处理 `TaskbarCreated`、`WM_DISPLAYCHANGE`、`WM_DPICHANGED` 和自定义调度消息；
- 在最后一个 Surface 销毁后退出消息循环。

Application、Host CommandLoop 和后台任务只能提交异步工作项，不能直接操作 HWND。Dispatcher API 必须满足：

- `Task` 在窗口线程完成操作后才结束；
- 调用取消只取消尚未开始的工作，不能中断一半的窗口交换；
- 关闭顺序为“停止接收 → 销毁所有 Surface → 销毁信号窗口 → 注销类 → 退出线程”；
- WindowProc 不做磁盘、Pipe、Renderer 等阻塞 I/O，只发布不可变平台事件。

用于接收 `TaskbarCreated` 的窗口必须是隐藏顶级窗口，而不是 message-only window，因为 Shell 将该消息广播给顶级窗口。

当前实现已满足上述线程封送和消息循环边界，并有自动化测试覆盖；这不替代真实桌面窗口层级验收。

## 3. Surface 生命周期和目标端口

Surface 状态固定为：

```text
Provisional(hidden) → Active(visible) → Retired(hidden) → Destroyed
                   ↘ failure/cancel → Destroyed
```

目标 Application 端口为：

```csharp
public interface IDesktopHost
{
    Task<DesktopTopology> EnsureTopologyAsync(
        DisplayTopology displays,
        CancellationToken cancellationToken);

    // 创建后必须保持隐藏，仅供 Renderer 附着和准备首帧。
    Task<DesktopSurface> CreateSurfaceAsync(
        SurfaceRequest request,
        CancellationToken cancellationToken);

    // 在 Window Dispatcher 中批量激活新 Surface，并隐藏被替换 Surface。
    Task ReplaceSurfacesAsync(
        SurfaceReplacement replacement,
        CancellationToken cancellationToken);

    // 幂等；实际 DestroyWindow 必须在创建 Surface 的 Window Dispatcher 执行。
    Task DestroySurfaceAsync(
        SurfaceId surfaceId,
        CancellationToken cancellationToken);
}

public sealed record SurfaceReplacement(
    IReadOnlyList<SurfaceId> ProvisionalSurfaceIds,
    IReadOnlyList<SurfaceId> ReplacedSurfaceIds,
    long DesktopTopologyRevision);
```

选择批量 `ReplaceSurfacesAsync`，而不是单个 `ActivateSurfaceAsync`，是为了让一次多屏 Apply 只有一个逻辑提交点。`DesktopTopologyRevision` 防止在准备期间发生 Explorer/显示器变化后激活过期 Surface。

“原子替换”在本项目中的精确定义是：

- Host CommandLoop 对活动会话和 Assignment 的提交是原子的；
- 同一父 HWND 下的显隐/Z 顺序变化尽量通过 `BeginDeferWindowPos / DeferWindowPos / EndDeferWindowPos` 在一个屏幕刷新周期提交；
- 多个不同父 HWND 或多显示器之间不承诺操作系统级事务原子性，只保证单次 Dispatcher 工作项、失败不提交 Host 新状态；
- 若交换失败，旧 Surface 保持或恢复可见，新 provisional Surface 被清理，Assignment 不保存。

当前 Application 端口、Platform.Windows 实现和 Host 调用链已经落地；真实多屏视觉原子性与图标层级仍属于验收项。

当前 `DesktopSurface.WindowHandle` 与 Renderer Protocol v1 的 `AttachSurface.windowHandle` 只表达 `HwndChild` 绑定：Host 创建容器 HWND，Renderer 在其中创建自有子窗口。Renderer 可以在自有 child HWND 内部使用 DirectComposition target/交换链，这不会改变协议。只有实验最终要求 Host 接收或合成 Renderer 的共享纹理、交换链句柄、同步 fence 等跨进程 GPU 资源，或传递对象不再是容器 HWND，才必须新增强类型 `SurfaceBinding`、能力协商和协议 minor 版本，并同步 Schema 与契约测试；不得继续复用 `windowHandle` 携带不同语义。

## 4. 应用壁纸顺序

```text
HostCommandLoop 接收 ApplyWallpaperCommand
  → 后台创建全部隐藏 provisional Surface
  → 启动/选择 Renderer
  → 按协商的 SurfaceBinding 附着（协议 v1 当前为 AttachSurface）+ LoadWallpaper
  → 等待全部 FirstFramePresented
  → 回投携带 Generation 的 ApplyPreparedCommand
  → HostCommandLoop 校验 Generation 与 DesktopTopologyRevision
  → DesktopHost.ReplaceSurfacesAsync
  → 原子提交 Host 活动 Session/Assignment
  → 停止旧 Renderer并销毁 retired Surface
```

不得在 `CreateSurfaceAsync` 时显示新 Surface，也不得仅依赖创建顺序或 `HWND_BOTTOM` 隐式实现切换。

## 5. Explorer/Shell 恢复

Microsoft 文档说明 Shell 创建任务栏时会广播注册字符串 `TaskbarCreated`，并且 Windows 10 在主显示器 DPI 变化时也可能广播。因此该消息表示“Shell 拓扑可能已变化”，不是 Explorer 重启的充分证明。

恢复流程：

```text
隐藏顶级信号窗口收到 TaskbarCreated
或后备探针发现 attach point 身份/层级失效
  → 合并事件并固定 350 ms 防抖
  → 重新枚举并验证 Shell 层级
  → 投递不可变 ExplorerRestartedCommand（名称后续可细化为 ShellTopologyInvalidatedCommand）
  → HostCommandLoop 提升 Generation / 标记恢复中
  → 为当前布局重建隐藏 Surface（不重挂旧 HWND）
  → Renderer.SetBounds + AttachSurface
  → 等待 SurfaceAttached 及恢复后的首帧
  → ReplaceSurfacesAsync 激活新 Surface
  → 清理旧/失效 Surface 记录，尽量保留 Renderer 和播放位置
```

`IsWindow` 只能作为提示，不能作为外部 HWND 身份证明：句柄可能在检查后销毁或被复用。验证必须重新枚举，并组合类名、进程、父子关系和当前适配器层级规则。若现有 Renderer 不支持重新附着或超时，再进入 Renderer 重启/熔断策略。

当前实现使用隐藏顶级窗口接收 `TaskbarCreated`，按 350 ms 合并验证请求，并以 2 秒探针作为后备；Host 已处理恢复命令并执行上述重建路径。Host-owned DComp 与独立 Renderer 同进程跨 generation 重新附着均已通过真实功能恢复验收。产品级无缝恢复尚未宣称通过：旧 Shell generation 被显式销毁至新 generation 可用之间允许短暂黑屏，后续只能优化发现/重建延迟，不能违反 provisional 或 HWND generation 边界。

## 6. 诊断和 Stage B 完成门槛

`DesktopHostDiagnostics` 当前提供以下入口：

```powershell
# 默认：只读显示器拓扑
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj

# 只读：执行上下文、完整 build/UBR、Shell 顶级窗口和 GetShellWindow 后代递归拓扑
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --shell-topology

# 显式实验：创建色块并在超时后清理；默认 10 秒，允许 1–30 秒
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --color-block --duration-seconds 10

# 显式 Raised 请求：预检通过后发送 0x052C / 0x0D / 0x01
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-probe

# 结构通过后创建限时 layered child 色块
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-color-block --duration-seconds 5

# 选择 presentation kind；当前包含 layered-gdi、non-layered-gdi、direct-composition-swap-chain
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-presentation-probe direct-composition-swap-chain --duration-seconds 5

# 双重显式确认后重启当前 Progman 所属 Explorer，并在新 generation 重建 DComp Surface
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj -- --raised-desktop-explorer-recovery-probe --confirm-explorer-restart --duration-seconds 5
```

`--shell-topology` 必须先输出当前进程的 session ID、window station 和 desktop。若 `GetShellWindow() == 0`，结果是 `Inconclusive / Shell unavailable` 并返回独立退出码 3；即使显示器拓扑存在，也不得把该结果记为“此 build 有 0 个候选”或返回成功。

`--color-block` 已实现，但它是显式、限时、失败关闭的诊断入口，不是生产支持开关，也不表示色块矩阵已经验收。当前完整 build `26200.9168` 没有符合既定 Legacy WorkerW 规则的候选，因此正确结果是拒绝附着并保持 allowlist 为空。Shell 不可见时色块模式必须在创建 Surface 前以退出码 3 停止；Shell 可见但无合规附着点时输出拒绝原因并以退出码 4 停止。

Raised Desktop 全部入口与 Legacy 完全隔离。预检拒绝退出码为 5；请求导致 Shell WorkerW 留存时返回 6 / `CleanupIncomplete`；Explorer 重建失败返回 7。普通入口只销毁 LiveWall 自有 HWND，不发送未经验证的恢复消息，也不静默重启 Explorer；只有恢复探针和确认参数同时存在才允许终止当前 Progman 所属 Explorer。结构指纹不含 HWND/PID，现场完整快照仅用于单次诊断。

真实验收必须验证：

- 测试 Surface 位于桌面图标下方，且测试像素确实进入当前桌面的可见合成路径；
- 自动 Z-order/窗口结构快照与人工像素可见性分别记录，任一失败均不得产生生产 attach point；
- 创建、定位、显隐、销毁均发生在 Window Dispatcher；
- provisional Surface 在激活前不可见；
- 首帧交换不出现明显黑帧或旧/新双重可见；
- Explorer 重启后重建 Surface，而不是复用旧 HWND；
- 独立 Renderer 进程通过生产候选 binding 创建并重建自己的可见内容，而不是只验证同进程 Host-owned 图形资源；
- 单屏/多屏、负坐标、100–200% DPI、热插拔均通过；
- 诊断退出、取消或异常时不留下孤儿窗口；LiveWall 自有资源清理与 Shell 请求副作用恢复必须分别记录。

完成这些真实桌面验收之前，Stage B 保持 `In progress / Not accepted`。

## 7. build 26200 的诊断进展与下一步

只读递归基线采样已经实现：工具记录进程 session、window station 和 thread desktop，使用 `GetShellWindow` 锚定 Shell 桌面窗口，`EnumChildWindows` 采集全部后代，并记录完整 build/UBR、class、PID/TID、parent/owner、style/ex-style、bounds、visibility、`SHELLDLL_DefView`/`SysListView32` 关系；同级顺序由 `GetTopWindow` 和 `GetWindow(GW_HWNDNEXT)` 独立计算。

同一机器上的两种已验证执行上下文必须明确区分：Codex 沙箱为 `session=2 / WinSta0 / CodexSandboxDesktop-*`，此时 Shell anchor 为 0、结果无结论、退出码为 3；登录用户交互式桌面为 `session=2 / WinSta0 / Default`，此时 Shell 状态为 Available，才能讨论候选数量。

2026-09-06 的 `26200.9168` 交互式采样已完成以下判定：

- 第三方动态壁纸退出后、Explorer 重启前连续三次采样一致：`Progman` 包含 `SHELLDLL_DefView → SysListView32 → SysHeader32`，以及一个同级的全屏 Explorer `WorkerW`；外部 `mpv` 已消失。该 WorkerW 在 Explorer 重启后消失，判定为旧 Shell 中的残留结构，不能作为干净基线。
- Explorer PID 从 `19776` 变为 `35652`，`Progman`、DefView、ListView 和 Header 的 HWND 全部变化，证明 Shell 确实重建而非复用旧句柄。
- Explorer 重启后连续三次采样稳定为上述三层图标树，没有 Progman 子 WorkerW，也没有“带 DefView 顶级窗口之后的全屏同进程无 owner WorkerW”。
- 显式 5 秒 `--color-block` 请求没有生成合规候选，在创建 Surface 前失败关闭；动作前后 Shell 拓扑一致，没有 LiveWall 临时窗口残留。
- 隔离 `--raised-desktop-probe` 的只读预检通过；`0x0D/0x01` 唯一新增 Progman 直接子 WorkerW，满足同 Explorer PID、owner 为零、全虚拟桌面 bounds、无 DefView 且位于 DefView 后方。初次请求后该 Shell WorkerW 留存，结果为 `CleanupIncomplete`，退出码 6。
- 在已解释的 WorkerW 上运行修正后的 5 秒 layered GDI probe，自动快照验证 `DefView(z0) > LiveWall layered child(z1) > WorkerW(z2)`。显式 GDI 填充、强制刷新并等待 DWM 提交后，人工观察仍完全没有洋红色或桌面变化；这证明该 HWND/Z-order 结构没有进入当前桌面的实际可见合成路径。
- 诊断输出已经拆分为 `OwnedResourcesCleanup` 与 `ShellMutationRecovery`。LiveWall 自有 HWND 已销毁，但初次请求生成的 Shell WorkerW 仍然存在，因此整体 Shell mutation recovery 仍是 `CleanupIncomplete`；两个结论不得合并。
- `--raised-desktop-presentation-probe non-layered-gdi` 已完成一次 5 秒运行并输出 `PresentationProbeCompleted`，但人工观察仍完全没有桌面变化，判定 `PresentationFailed`。该状态只表示探针执行完毕；人工失败后仍不是 `RenderableDesktopAttachment`。

因此准确结论是“现有 Legacy 不支持；Raised Desktop 的 layered GDI 与 non-layered GDI presentation probe 均失败；无边框 Host-owned DirectComposition composition swap chain 已通过单屏呈现和 Explorer generation 重建诊断验收；独立 Renderer-child 协议 v1 binding 已通过单屏可见性、生命周期和跨 Shell generation 自动重附着门禁”。第一次 DComp 实验因从顶层窗口转换时残留 `WS_CAPTION`，交换链只覆盖客户区，造成上、左非客户区缺口；探针改为直接创建无边框 Progman 子窗口后，第二次实验已人工确认完整覆盖壁纸区域、图标位于其上且任务栏不受影响。当前已有 Host-owned DComp 与 Renderer-child 两类诊断证据，但生产启用数量仍为 0；下一步不能修改 Legacy、直接使用 Progman 或立即启用生产 Raised Adapter：

1. 已实现不包含裸 HWND/PID 的 SHA-256 结构指纹和强类型拒绝理由；继续保持只输出证据、不自动修改 allowlist。
2. 隔离 Renderer-child DirectComposition 探针已通过：父进程创建无边框 Host Surface；独立 Renderer 辅助进程仅通过现有 `AttachSurface` 接收容器 HWND，在其中创建并拥有 child HWND、DComp target 和交换链；parent、独立 PID、bounds、事件顺序、真实像素与生命周期均已验证。测试 Renderer 现已补齐 `Hello → Initialize → Initialized`、首次 Attach/Load/ContentLoaded/FirstFrame、再次 Attach 与 ShutdownCompleted。
3. 生产形态 Host Surface 已使用 Raised 结构规则：内部 Lease 记录 Progman parent、DefView Z-order anchor、WorkerW backdrop、结构指纹与 Shell generation；`NativeDesktopSurfaceFactory` 使用无边框/no-redirection profile 创建 hidden provisional container，并在激活时显式维持 `DefView > LiveWall > WorkerW`。受控窗口自动测试已覆盖该行为，但不替代真实 Shell 矩阵。
4. Renderer Protocol v1 `HwndChild` binding 与正式控制面状态机均已接线并通过自动验证；Application 只接收无 Shell HWND 的能力描述，Renderer 只接收 LiveWall 容器 HWND。Protocol/Schema 不升级、不修改；交互式产品候选已在双屏环境的主屏单 Surface 上连续两次完成人工首帧验收。
5. 同一 Renderer 进程在 Explorer generation 变化后释放旧 child/target、接收新 `AttachSurface`、创建全新 child/target 并再次产生 `FirstFramePresented` 的自动门禁已通过，未复用旧 HWND；增强的每秒持续层级复验发现第三方 `mpv` 自动恢复并抢占 DefView 后第一位置，因此必须在无竞争桌面 Surface 的环境重测，不能实现周期性 Z-order 争抢作为规避。产品接线后还需验证播放状态保持。
6. 当前不进入共享纹理/交换链 handle、fence、资源所有权和协议 1.1 设计；只有未来证据表明 Host 必须合成 Renderer 帧或跨进程对象不再是容器 HWND 时才重开该决策，不能因内部使用 DirectComposition 就升级协议。
7. binding、Factory 与正式 Renderer v1 会话边界已经关闭；显式诊断开关也已接通真实 Host command loop / SessionCoordinator → Adapter → Lease → Native Factory → Renderer v1 产品候选链。恢复失败后的 previous Surface 回附已经删除，generation-safe abandon、全新会话退避重建和预算耗尽熔断已自动验证。每个 Apply generation 具有不可变时间线、强类型终态和按 Surface 数量推导的 deadline；P1.5.1 又将该 deadline lease 与长期活动 Session 解耦。物理单屏短时补测以及 P1.6 双屏 5 秒、90 秒、10 次 process-cold 和同一 Host 20 轮连续替换现均已通过。下一步是正式进程局部故障、双屏 Explorer generation 和显示拓扑重规划矩阵。Shell-owned HWND 只属于单次 Shell generation，不能进入持久 allowlist。
   P1.6 已增加 schema 1.0 结构化单次结果、直接启动 Release 可执行文件且不补跑失败的 process-cold 批次驱动、产品候选专用 soak，以及支持显示器集合的同一 Host 串行 Apply/Replace/retirement 循环。真实双屏 90 秒 soak、10/10 次 process-cold 和 20 轮同 Host 循环均已通过。循环的 `all` 模式每代等待两个 FirstFrame 后只提交一次，下一代提交后等待上一代两个 Renderer 全部退休；实际得到 40 个 graceful retirement（38 个 Replacement、2 个 Shutdown），全部 ShutdownCompleted/Dispose/Surface cleanup 成功，无自有 HWND/新增 WorkerW。用户确认两屏同步橙黄交替、无黑屏或覆盖并正常恢复。process-cold 的 Apply/最后首帧/批次退休耗时范围分别为 1245.6–1285.2 ms、1227.7–1267.7 ms、43.8–80.3 ms；每轮 Shell generation 与显示拓扑均稳定。
   双屏切片已实现显式 `primary / all / display-id` 选择和逐 Session 结果；`all` 由同一 generation 等待两个独立 FirstFrame 后执行一次批量 Replace，局部失败零提交并整批回收。多个 Renderer 的 retirement 并行启动、各自保留独立预算，Surface HWND 销毁仍由 Window Dispatcher 串行。`26200.9168 / session=7 / WinSta0\Default` 已在 200% 主屏和 150% 负坐标外接屏完成 5 秒基础、90 秒长驻、10 次 process-cold 及 20 轮连续替换；两屏同步、层级、40 个 Renderer retirement、自有资源和 Shell mutation 检查均通过。故障注入、双屏 Explorer、热插拔、主屏切换和更完整 DPI 矩阵仍未验收。
8. LiveWall 自有资源清理与 Shell mutation recovery 分开验收；经用户授权的 Explorer 重启只证明 generation 恢复路径，不得把重启本身当成 Raised 请求可撤销能力。
9. 兼容键至少包含完整 build revision/UBR、结构指纹、attachment presentation、Renderer binding 与验收矩阵，并继续要求运行时结构和实际呈现验证。基础 build `26200` 属于持续接收累积更新的 Windows 11 25H2 build 系列，单独使用它会把不同 Shell revision 错当成同一能力。

该方向已经回答“Progman 内部是否存在可验证层级候选、Host-owned DComp 与跨进程 `HwndChild` 是否可见并可重附着、生产形态 Lease/Factory 能否保持边界，以及正式 v1 双屏产品候选能否长驻、冷启动和连续原子替换”。下一步是故障、双屏 Explorer 与显示拓扑重规划矩阵，不是直接写入 allowlist。由于相关桌面宿主行为不是公开动态壁纸 API，只有精确 build/UBR 的生产矩阵通过后，结论才能形成版本化、可撤销、失败关闭的生产适配器能力。

## 8. 官方依据

- [DestroyWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-destroywindow)：创建窗口之外的线程不能销毁该窗口。
- [Window Messages](https://learn.microsoft.com/en-us/windows/win32/learnwin32/window-messages)：每个创建窗口的线程拥有消息队列，并通过消息循环分派消息。
- [Taskbar creation notification](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar#taskbar-creation-notification)：`TaskbarCreated` 广播行为，以及 DPI 变化也可能触发的说明。
- [IsWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindow)：外部 HWND 存在竞态且句柄可能复用。
- [EndDeferWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enddeferwindowpos)：在单个屏幕刷新周期更新多个窗口的位置和尺寸。
- [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)：显隐、Z 顺序和 `SWP_NOACTIVATE` 语义。
- [SetLayeredWindowAttributes](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes)：layered window 的 alpha 设置和后续绘制限制。
- [SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent)：父窗口、样式和 DPI awareness 限制。
- [GetShellWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getshellwindow)：获取 Shell 桌面窗口句柄。
- [EnumChildWindows](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumchildwindows)：递归枚举指定父窗口的子窗口。
- [GetNextWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getnextwindow)：按 `GW_HWNDNEXT` 等关系遍历 Z-order。
- [GetThreadDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getthreaddesktop)：获取指定线程关联的 desktop。
- [GetUserObjectInformation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw)：读取 window station/desktop 对象名称。
- [ProcessIdToSessionId](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-processidtosessionid)：读取当前进程所属登录会话。
- [Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)：顶级/子窗口的 Z-order 与 owned-window 规则。
- [Windows Insider Flight Hub](https://learn.microsoft.com/en-us/windows-insider/flight-hub/)：build 26200 所属 Windows 11 25H2 build 系列及其持续更新记录。

相关实现证据只用于形成诊断假设，不构成 Windows 平台契约：[Lively `WinDesktopCore.cs`](https://github.com/rocksdanister/lively/blob/d27589c821ed48248f2b9ff95e236400c20540a3/src/Lively/Lively/Core/WinDesktopCore.cs) 展示了 Raised Desktop 请求与层级操作；[Lively issue #3193](https://github.com/rocksdanister/lively/issues/3193) 同时记录了 build 26200 上偶发覆盖图标的问题，因此 LiveWall 仍要求自己的失败关闭和完整验收矩阵。
