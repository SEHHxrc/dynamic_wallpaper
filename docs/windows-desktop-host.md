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
| Stage B 总体状态 | `In progress / Not accepted` | Lease/Factory、v1 `HwndChild` binding、正式 Renderer 会话、P1.6 双屏可靠性、P1.7 故障/Explorer 代表矩阵以及当前双屏断开/重连、快速抢占和主屏切换已通过；完整 DPI/拓扑/会话、Shell mutation 和体验优化尚未完成 |

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

    // 显示几何过渡期间调整当前活动容器；不改变 Surface 身份或提交状态。
    Task ResizeSurfaceAsync(
        SurfaceId surfaceId,
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

### 4.1 显示拓扑重规划

`DisplayTopologyChangedCommand` 由 Host 单写者按严格递增 topology revision 处理。Assignment 是持久用户意图，与当前在线显示器集合分离：显示器离线时保留 Assignment；重新接入同一 DisplayId 时恢复该意图；没有显式 Assignment 的新显示器不自动继承其他屏内容。诊断命令的 `--target-display all` 仍只在 Apply 时展开，不新增持久化的“all”字段。

Host 使用不可变 `DisplayReconciliationPlan` 将变化分类为 `NoChange / Reconfigure / Suspend`：

- 仅 primary 标记或刷新率变化不会重建 HWND；
- bounds 或 scale/DPI 变化会重建受影响 Surface；
- 对同一 DisplayId、壁纸和 FitMode 的 bounds/scale 变化，在新 provisional 批次准备前先向旧 Renderer 发送 v1 `SetBounds`，让 Renderer resize 自有 child/swap chain 并重绘最后一帧，再 resize 旧活动 Host Surface；这是过渡连续性优化，不改变最终 FirstFrame 屏障和原子 Replace 规则；
- PerDisplay 拔插通常只影响对应 Session；但若负坐标显示器的加入/移除改变 Progman 的虚拟桌面原点，所有仍连接的 child Surface 坐标基准都会改变，必须整批重建，不能复用旧 HWND；
- Span 成员变化会重建整个 Span Surface；
- Span 使用物理像素 union 和单个 Renderer，跨多个成员时协议 `scaleFactor=1`；其矩形不得与任何非成员的已连接显示器产生正面积交集，否则以 `layout.span.intersects_non_member_display` 失败关闭，不依赖另一 Surface 的 Z-order 遮挡；
- 运行期从非空到空先进入 Platform 内部 `EmptyCandidate`；只有相隔正常 350 ms 稳定窗口的两次连续空采样才发布零显示器 revision、退休活动 Session 并进入 Suspend，但不删除 Assignment。采样异常或任何非空快照会取消候选；启动时首次采样即为空则建立明确的初始空拓扑；
- 新 Surface 全部完成 FirstFrame 后才进行一次批量 Replace；准备或 Replace 失败时不提交新状态；
- 更高 topology revision 会把仍在准备的旧计划终止为 `Cancelled / Superseded`，触发取消并回收 provisional Renderer/Surface；
- 若 Raised 结构检查明确返回 `UnexpectedTopologyMutation`，说明显示拓扑已经更新但 Explorer 子树仍在重排；Host 在同一 reconciliation generation 和总 deadline 内按 350 ms、1 s、2 s 有界重试。其他 TopologyFailed 不得借此放宽或无限重试；
- Raised attachment lease 必须记录发现时 Progman 的虚拟桌面坐标空间。Host 接受 DisplayTopology 时、创建 provisional Surface 时以及批量 Replace 前，Display bounds union、lease bounds 与当前 Progman bounds 必须一致；任一不一致都按 `UnexpectedTopologyMutation` 处理。若在 Replace 前发现，必须先退休本批 provisional Renderer/Surface，再在同一 reconciliation generation 和 deadline 内有界重建；准备阶段和 Replace 阶段各自拥有 350 ms、1 s、2 s 的独立尝试计数，仍共同受 generation 总 deadline 和更新 topology revision 抢占约束；禁止激活按旧/中间旋转几何创建的 Surface；
- 上述 bounds 门禁只适用于即将激活的 provisional Surface。待隐藏的旧 active Surface 在旋转/bounds 重规划后按定义保存旧坐标空间；只要 HWND、Shell generation、parent/anchor 和 Z-order 身份仍有效，就必须允许原子隐藏，不能因旧 lease bounds 与新 Progman bounds 不同而拒绝 Replace；
- 每个 revision 发布独立的、有界可等待终态和时间线，不能用 `Sessions.Count` 推断完成。
- 诊断工具在最后一个指定 revision 成功后仍持续采样整个观察期；额外 topology revision 必须把样本降级为 inconclusive，不能用前一 revision 的成功终态掩盖后续未接管变化。真实长期 Host 则由事件桥持续转发，不以诊断次数截断生命周期。

上述代码与自动事务测试已经接线。`26200.9445 / session=3 / WinSta0\Default` 已完成 200% 主屏与 150% 负坐标外接屏的真实断开 `2→1`、重连 `1→2`，随后又完成 `2→1→2→1→2` 快速抢占：诊断在 FirstFrame 后保持提交以确保形成竞态，前三个 revision 均 `Cancelled/Superseded` 且零 Replace，最终稳定 revision 恰好一次双屏 Replace；全部 provisional/Renderer 清理，无自有窗口残留。2026-09-14 的 session 6 主屏切换又使 Windows 把外接屏从负坐标移到 `(0,0)`、内置屏移到 `(3840,156)`；Host 正确执行一次双屏 `Reconfigure`。随后外接屏从 150% 改为 175% 且 bounds/原点不变时，仅外接屏 Session 被替换；内置屏从 `2880×1800@200%` 同时变为 `2560×1600@175%` 时，也仅替换内置屏 Session，外接屏原橙色持续显示。缩放/分辨率瞬间应用窗口的系统级布局重绘不记为壁纸闪烁；各次均无黑屏、覆盖或错误联动，旧 Session 与最终资源完全清理。旋转的最终拓扑、黄色批量替换、外接屏保持、层级与清理均已在双向样本通过；活动适配约为 14–27 ms。P1.8-TG0 的重复矩阵已确认预置 Guard 能自动显现，但不能保证整个预测区域原子进入第一可见帧，因此旋转准确登记为 `TransientCoverageGap`，不再继续 Guard 生产化。仅 Primary 标记变化的 Host 门禁另由自动测试验证为 `NoChange`、零 Replace/retirement 且 Session/Renderer 身份保持。2026-09-18 在 `26200.9457 / session=2 / WinSta0\Default` 又完成 refresh-only、位置/虚拟原点和 Span position/scale/成员/布局模式切换代表矩阵；pure bounds-only 与物理零显示器按既定条件登记为 `EnvironmentInconclusive`，对应自动门禁通过。P1.8 已关闭。多屏之间的“原子”仍遵循本节前述定义：Host 状态只有一个提交点，不承诺跨显示器的操作系统级事务。

后续纵屏→横屏补测确认最终提交成功，但在黄色新 Surface 就绪前，旧纵屏 Surface 未覆盖新增的横向区域，主屏右侧短暂露出静态壁纸。为关闭这一过渡缺口，Host 会在新 Renderer/Surface 准备前先适配旧活动会话：Renderer-child 处理既有 v1 `SetBounds`，释放 back buffer/RTV、执行 `ResizeBuffers`、调整 child HWND、重建 RTV 并重绘最后一帧；Platform 随后通过 `ResizeSurfaceAsync` 调整旧活动容器。第一版只等待命令写入管道，横屏→纵屏通过，但反向仍有不到 0.5 秒露底。现在 `SetBounds` 后的当前-generation `FirstFramePresented` 被定义为 resize/present 完成确认，Host 收到后才扩大容器，状态机也禁止确认前开始后续 Attach。该路径不改变 DTO、Schema、Surface 身份或最终 Replace 事务，自动基线 199/199 通过；反向纵屏→横屏仍需再次实机确认。

进一步实测表明，显示模式切换期间同步 `ResizeBuffers/DwmFlush` 与重新发现 Raised lease 会分别引入显著阻塞。过渡路径现改为：收到原生显示变化后立即采样，同时保留 350 ms 稳定复验；复用仍通过 generation/PID/parent/anchor 验证的当前 lease；Renderer 仅对既有最后一帧提交 DComp transform 并调整 child，Host 使用 Progman 实时坐标调整活动容器；新 provisional 批次仍按精确像素尺寸创建，并在完整 FirstFrame 与结构门禁后原子替换。`26200.9445` 实测把活动适配从约 741–747 ms 降到 20.587 ms，但黑屏结束后仍可能出现一瞬静态壁纸，说明剩余缺口位于系统输出切换到应用收到/合成下一提交之间。它继续记为单帧级 `TransientCoverageGap`，不是坐标计算错误，也不是完全通过。绝对零暴露需要变化前预置的有限 guard；任何 guard 都必须保持 per-display 内容隔离、不得侵入相邻显示器，并且不能用无界 HWND/纹理替代精确坐标。

P1.8-TG0 只验证“变化前预置的有限内容是否会被新 Progman/DWM 几何自动揭示”。Guard 区域限定为 `(宽高互换后的候选矩形 - 当前矩形) - 其他显示器矩形`，并受矩形数量、尺寸和总像素面积上限约束。Guard 在 `Armed` 后禁止窗口移动、resize 和再次 DComp Commit；正式重规划仍使用既有精确 Surface 与批量 Replace。该诊断不改变 `IDesktopHost`、Surface 模型、Renderer Protocol 或 allowlist，结论及回退边界见 ADR-009。

2026-09-16 首个横屏 `2880×1800` → 纵屏 `1800×2880` TG0 实机样本为明确 `GuardVisible`。预置洋红区域是 `(0,1800 1800×1080)`，与外接屏零交集，Armed 后没有窗口操作或图形提交；用户在系统黑屏结束后首先看到洋红色一闪而过，甚至未看到新增下方区域的黑屏/静态壁纸，随后正式黄色 Surface 完整显示。自动结果确认预测 bounds 命中、Shell generation 不变、一次 Replace、最终资源清理完整。该结果只证明一个方向的一次技术可行性；纵→横及双向重复样本完成前，TG0 仍未整体关闭。

紧接着的纵屏 `1800×2880` → 横屏 `2880×1800` 反向样本同样为 `GuardVisible`：右侧 `(1800,0 1080×1800)` 洋红 Guard 在黑屏后首先出现，随后主屏完整变黄直至恢复，未露出静态壁纸；外接屏与系统窗口层级正常。自动结果为 `ActiveSurfacesResized=18.620 ms`、`SurfacesReplaced=1027.028 ms`、一次 Replace 和完整清理。

完整重复矩阵最终得到纵→横 `5/5` 完整 GuardVisible；横→纵有 4 次完整 GuardVisible、1 次 Inconclusive，并在最终补测明确观察到“上方小部分洋红、其余黑色 → 完全洋红 → 黄色”。所有自动样本均命中预测 bounds，`postArmWindowMutationCount=0`、相邻屏交集为零、Shell generation 不变，最终 graceful/clean 且无 HWND 残留。该反例说明预置内容可以被 DWM 自动揭示，但显示模式切换不保证整个 Guard 区域原子进入第一可见帧。因此 TG0 按 ADR-009 关闭为“部分可行、严格目标失败”，不进入 TG1，不生产化 Guard；旋转保留 `TransientCoverageGap`。

`26200.9457` 的后续 bounds 探索显示该限制不只存在于旋转。Windows 将主屏 `2560×1600@200% → 2880×1800` 时自动联动到 225%，Host 正确只重建主屏：外接 Session 全程保持，活动适配 24.880 ms，一次 Replace 后最终黄色完整，退休和清理均通过；但扩大后的右侧与下侧在橙→黄之间短暂露出系统静态壁纸。第三次预设相同 scale 后，Windows 仍把 `2560×1600@225% → 2880×1800` 联动为 250%；这次人工未看到露底，但诊断终态输出未能留存，不计自动样本。连续三次都无法由系统设置产生只改变 bounds 的 revision，因此本机 pure bounds-only 物理项登记为 `EnvironmentInconclusive`，不再反复切换模式。已有样本足以把“bounds 扩张完美连续性”登记为 `TransientCoverageGap`；它不代表错误显示器内容、半批提交或 attachment 层级失败，也不授权重新进入 TG1。

随后完成了不改变虚拟原点的 position-only 样本。初始主屏为 `(0,0) 2880×1800@200%`，左侧外接屏从 `(-3840,14)` 下移到 `(-3840,144)`，两屏 refresh/scale/bounds 大小不变，虚拟原点始终为 `(-3840,0)`。Host 只重建外接屏：主屏橙色和 Session 全程保持，外接屏先用旧橙色快速适配、稳定后原子切换为黄色；活动适配 23.270 ms、Replace 1047.736 ms，只退休一个 Session，最终 graceful/clean 且无自有 HWND。该样本关闭 position-only 局部重建代表门禁，不替代虚拟原点变化时的整批重建样本。

随后完成了改变虚拟原点的 position 样本：左侧外接屏从 `(-3840,144)` 上移到 `(-3840,-369)`，使虚拟桌面原点从 `(-3840,0)` 变为 `(-3840,-369)`，Progman bounds 变为 `(-3840,-369) 6720×2169`。Host 按坐标基准变化整批重建两个 PerDisplay Session，活动适配 34.231 ms，两个新首帧齐备后在 1315.575 ms 处只执行一次批量 Replace，两个旧 Session 均退休；终态 `Succeeded`，最终 graceful/clean 且无自有 HWND。人工确认两屏同步刷新、同步稳定并同步变黄，没有错误显示器内容或半屏提交；主屏下部短暂露黑登记为 `TransientCoverageGap`，不改变 attachment safety 通过结论。

首次真实 Span position 样本暴露出一个诊断/生产共用发现路径缺口：旧 Span HWND 覆盖完整虚拟桌面，Raised attach-point 复验把当前 Host 自有窗口误判为 `CompetingDesktopSurface`。该次 revision 失败关闭、Replace 为零，旧橙色 Span 全程保持且最终完整清理，因此没有半提交；但不能登记为 Span 重配置通过。修复后，Native Surface Factory 会把每个 Raised Host Surface 的精确 HWND 按实际生命周期登记，发现路径只忽略该精确集合；正常销毁、创建异常和旧 generation abandon 均注销。它不按进程名或 PID 忽略窗口，所以同进程中的未登记窗口和真正第三方 Surface 仍会被拒绝。Release 基线更新为 `218/218`，等待复测 Span 实机。

修复后的 Span position 复测通过。初始双屏由一个 Session、一个 Renderer 和一个物理像素 union Surface 覆盖；外接屏从 `(-3840,-210)` 移到 `(-3840,-91)` 后，旧橙色 Span 在系统刷新期间持续可见且没有露出其他颜色或静态壁纸。Host 在 19.322 ms 完成活动适配，新 Span 首帧完成后于 1042.967 ms 只执行一次 Replace，两屏同步切换为黄色；终态 `Succeeded/Reconfigure`，活动 Session 1、退休 Session 1，最终 graceful/clean、无自有 HWND。该结果关闭 Span position 代表门禁，也验证精确 HWND 注册不会削弱真正竞争 Surface 的失败关闭规则。

紧接着的 Span scale-only 样本也通过：外接屏保持 `(-3840,-91) 3840×2160@60 Hz`，只把 scale 从 1.5 改为 1.75。系统刷新期间旧橙色 Span 连续显示，外接应用窗口按新 DPI 正常放大，没有黑屏或静态壁纸；Host 在 18.598 ms 完成活动适配，新 Span 于 1066.147 ms 一次 Replace 后两屏同步变黄。自动结果明确为 `scaleOnlyChangeObserved=True / scaleOnlyChangeInvariantValid=True`，活动/退休 Session 各 1，最终 graceful/clean、无自有 HWND。Span 继续按物理像素 union 与 `scaleFactor=1` 工作，不需要协议变化。

Span 成员 `2→1→2` 实机往返随后通过。断开外接屏时，双屏 Span 在 1008.265 ms 处一次 Replace 为单屏 Span，活动/退休 Session 各 1；人工观察主屏无黑屏、无静态壁纸、无需系统刷新即直接变黄。重新接入时，单屏黄色持续到外接屏可显示；首次 Raised 结构复验遇到暂态 mutation，既有退避在 1175.841 ms 开始重试，新双屏 Span 于 2184.797 ms 一次 Replace 后两屏同步变橙。两个 revision 均 `Succeeded/Reconfigure`，最终 graceful/clean、无自有 HWND。该结果关闭 Span 成员断开/重连代表门禁，不把物理零显示器状态包含在内。

同一 Host 的 `PerDisplay→Span` 布局模式切换也通过。Generation 1 以两个独立 Session/Surface 在 1351.030 ms 提交橙色双屏；Generation 2 以一个覆盖两个 DisplayId 的 Span Session/Surface 在 988.353 ms 提交黄色双屏。两个旧 PerDisplay Renderer 均由 Replacement 优雅退休，最终 Span Renderer 由 Shutdown 优雅退休；三者均收到 `ShutdownCompleted`，Dispose 与 Surface cleanup 全部成功，无新增 WorkerW 或自有 HWND。人工确认两屏全程同步、橙→黄切换及最终恢复均无黑屏、静态壁纸、半屏或覆盖。至此 P1.8 按既定条件关闭；pure bounds-only 与物理零显示器保持 `EnvironmentInconclusive`，对应自动门禁通过，旋转/扩张保留 ADR-009 的 `TransientCoverageGap`。

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

当前实现使用隐藏顶级窗口接收 `TaskbarCreated`，按 350 ms 合并验证请求，并以 2 秒探针作为后备；Host 已处理恢复命令并执行上述重建路径。P1.9.1 已用真实 `WindowsHostRuntime` 验证完整事件链：无变化的 10 次 burst 为严格 No-op；真实 Explorer PID/HWND generation 变化只产生一次验证、一次 recovery operation 和一次批量 Replace，额外 fallback 观察周期没有重复恢复。本次终态为 `Succeeded + Rebuilt`，两屏同步恢复且无自有 HWND 残留；它不能误写为 `Reattached`。Host-owned DComp 与独立 Renderer 同进程跨 generation 重新附着也已有早期真实功能证据。产品级无缝恢复尚未宣称通过：旧 Shell generation 被显式销毁至新 generation 可用之间允许短暂黑屏，后续只能优化发现/重建延迟，不能违反 provisional 或 HWND generation 边界。

P1.9.2 复用同一隐藏窗口接收当前 Session 的 `WM_WTSSESSION_CHANGE`、`WM_POWERBROADCAST`、console display state 与 AC/DC power source。Platform 只聚合完整 `SystemStateSnapshot`、单调 revision 和 reason；Host 单写者拒绝旧 revision，并通过既有 playback policy 向 Renderer 发送 `Suspended/Playing`。锁定、显示关闭和系统 suspend 立即暂停但保留已提交 Surface；解锁、点亮和唤醒先发布 `RequiresEnvironmentValidation` 且维持暂停，事件桥同步重采样显示拓扑并请求 Explorer 验证，只有 Shell 可用时才发布活动快照。新的 Apply、拓扑重建和 Explorer 恢复提交后必须再次应用当前系统策略，避免非交互状态下新 Renderer 误留在 Playing。代码和自动事务矩阵已通过；真实锁屏/睡眠样本仍待验收，RDP 和快速用户切换属于 P1.9.3。

## 6. 诊断和 Stage B 完成门槛

### 首版本地桌面支持范围（2026-10-11）

RDP 动态壁纸呈现不在首版支持范围，RDP 实机矩阵为 `OutOfScope / WaivedForV1`，不再单独阻塞 Stage B；豁免不能视为测试通过。保留 WTS 通用会话断开/重连门禁、远程会话检测与暂停/失败关闭策略。本地快速用户切换实机保持 `NotValidated`，不通过锁屏样本替代。P1.9.2 单屏锁解与睡眠代表场景已经通过，双屏系统恢复仍待补；后续开发优先完成 P1.9.4 Shell/display 并发恢复与 P1.9.5 退出竞态，后文早期包含 RDP 的探索顺序按本节范围执行。

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
7. binding、Factory 与正式 Renderer v1 会话边界已经关闭；显式诊断开关也已接通真实 Host command loop / SessionCoordinator → Adapter → Lease → Native Factory → Renderer v1 产品候选链。恢复失败后的 previous Surface 回附已经删除，generation-safe abandon、全新会话退避重建和预算耗尽熔断已自动验证。P1.6、P1.7 与 P1.8 已按各自边界关闭。P1.9.0 又接通正式 `Program`、Composition Root 与 Host 生命周期停止门禁，P1.9.1 关闭了 `TaskbarCreated` 误触发与真实事件链单次恢复门禁；下一步是锁屏/睡眠、RDP、Shell/display 并发恢复和退出竞态，而不是继续扩张桌面附着接口。Shell-owned HWND 只属于单次 Shell generation，不能进入持久 allowlist。
   P1.6 已增加 schema 1.0 结构化单次结果、直接启动 Release 可执行文件且不补跑失败的 process-cold 批次驱动、产品候选专用 soak，以及支持显示器集合的同一 Host 串行 Apply/Replace/retirement 循环。真实双屏 90 秒 soak、10/10 次 process-cold 和 20 轮同 Host 循环均已通过。循环的 `all` 模式每代等待两个 FirstFrame 后只提交一次，下一代提交后等待上一代两个 Renderer 全部退休；实际得到 40 个 graceful retirement（38 个 Replacement、2 个 Shutdown），全部 ShutdownCompleted/Dispose/Surface cleanup 成功，无自有 HWND/新增 WorkerW。用户确认两屏同步橙黄交替、无黑屏或覆盖并正常恢复。process-cold 的 Apply/最后首帧/批次退休耗时范围分别为 1245.6–1285.2 ms、1227.7–1267.7 ms、43.8–80.3 ms；每轮 Shell generation 与显示拓扑均稳定。
   双屏切片已实现显式 `primary / all / display-id` 选择和逐 Session 结果；`all` 由同一 generation 等待两个独立 FirstFrame 后执行一次批量 Replace，局部失败零提交并整批回收。多个 Renderer 的 retirement 并行启动、各自保留独立预算，Surface HWND 销毁仍由 Window Dispatcher 串行。`26200.9168 / session=7 / WinSta0\Default` 已在 200% 主屏和 150% 负坐标外接屏完成 5 秒基础、90 秒长驻、10 次 process-cold 及 20 轮连续替换；两屏同步、层级、40 个 Renderer retirement、自有资源和 Shell mutation 检查均通过。P1.7 故障注入和双屏 Explorer 代表矩阵随后关闭；`26200.9445` 又通过同一双屏组合的断开/重连、快速反复插拔和主屏切换。完整 DPI/bounds 与零显示器条件性实机矩阵仍未验收。
8. LiveWall 自有资源清理与 Shell mutation recovery 分开验收；经用户授权的 Explorer 重启只证明 generation 恢复路径，不得把重启本身当成 Raised 请求可撤销能力。
9. 兼容键至少包含完整 build revision/UBR、结构指纹、attachment presentation、Renderer binding 与验收矩阵，并继续要求运行时结构和实际呈现验证。基础 build `26200` 属于持续接收累积更新的 Windows 11 25H2 build 系列，单独使用它会把不同 Shell revision 错当成同一能力。

该方向已经回答“Progman 内部是否存在可验证层级候选、Host-owned DComp 与跨进程 `HwndChild` 是否可见并可重附着、生产形态 Lease/Factory 能否保持边界，以及正式 v1 双屏产品候选能否长驻、故障关闭并完成拓扑重规划”。P1.9.0 的真实 Host 生命周期骨架已完成；下一步是 Shell 误触发、锁屏/睡眠/RDP、Shell/display 并发恢复和退出竞态，不是直接写入 allowlist。由于相关桌面宿主行为不是公开动态壁纸 API，只有精确 build/UBR 的生产矩阵通过后，结论才能形成版本化、可撤销、失败关闭的生产适配器能力。

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
