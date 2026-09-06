# ADR-007：Desktop Surface 窗口线程、显式交换与 Shell 恢复

- 状态：Accepted
- 日期：2026-09-05

## 背景

现有骨架在调用线程创建 Surface HWND，`DestroySurfaceAsync` 可能从另一个异步线程调用。现有 Surface 创建后立即可见，虽然 Host 等待 `FirstFramePresented`，但缺少显式激活/替换端口。Explorer 监视仅轮询，恢复仅尝试把旧 HWND 换父级；这不能满足根架构的首帧切换和 Explorer 重建要求。

Microsoft 明确规定 `DestroyWindow` 不能销毁其他线程创建的窗口；创建窗口的线程需要处理自己的消息队列。Shell 的 `TaskbarCreated` 会广播到顶级窗口，但也可能由 DPI 变化触发；`IsWindow` 对外部 HWND 还存在销毁竞态和句柄复用风险。

## 决定

1. Host 进程建立唯一专用 Window Dispatcher 线程，设置 STA 并运行标准 Win32 消息循环。
2. 所有自有 Desktop Surface 的窗口类注册、创建、定位、显隐、换父级和销毁只能在该线程执行。
3. Dispatcher 创建隐藏顶级信号窗口接收 `TaskbarCreated`；WindowProc 只发布事件，不做恢复工作。
4. `CreateSurfaceAsync` 的契约改为创建隐藏 provisional Surface。
5. `IDesktopHost` 新增批量 `ReplaceSurfacesAsync(SurfaceReplacement, CancellationToken)`，在首帧后显式激活新 Surface 并隐藏旧 Surface。
6. Host 逻辑状态原子提交；同一父 HWND 的视觉交换使用 DeferWindowPos 系列尽量在一个刷新周期执行，不承诺跨父窗口的 OS 事务原子性。
7. `TaskbarCreated` 和后备探针事件按 350 ms 合并；重新探测/验证后投递 HostCommand，由 Host 重建 Surface、重新发送 `SetBounds/AttachSurface`，不复用旧 HWND。
8. Raised Desktop 在 build 能力探测和真实窗口层级验收完成前保持禁用；Legacy WorkerW 也标记为 Experimental。

## 备选方案

- 继续在调用线程创建窗口：拒绝。无法保证消息循环和同线程销毁。
- 只增加单 Surface `ActivateSurfaceAsync`：拒绝。多显示器 Apply 缺少统一逻辑提交点。
- 仅用 1 秒轮询检测 Explorer：拒绝。恢复迟缓且不能接收 Shell 广播；轮询只保留为后备。
- Explorer 恢复时 `SetParent` 旧 Surface：拒绝。旧父级销毁后句柄可能失效或复用，且无法重新验证完整层级。
- 把 `TaskbarCreated` 直接解释为 Explorer 重启：拒绝。官方说明 DPI 变化也可能触发。

## 影响（决策时）

- Application 端口会先于代码实现发生受控不一致；Stage B 完成前必须补齐实现和契约测试。
- Surface 生命周期从二态创建/销毁变为 Provisional/Active/Retired/Destroyed。
- HostCommandLoop 需要在首帧后调用显式替换端口，再提交 Session/Assignment。
- Explorer 恢复可保留 Renderer 和播放位置，但 Surface 必须重建。
- `DesktopHostDiagnostics` 增加真实色块模式前，仍保持只读拓扑工具。

## 迁移与回退

迁移顺序为 Window Dispatcher → 隐藏 provisional 创建 → 批量替换端口 → Host 接线 → Explorer 信号/恢复 → 色块验收。任一步失败时保持 Raised Desktop 禁用；运行期交换失败保留旧 Surface，恢复失败则回退静态壁纸或报告不可用，不启用未经验证的 Shell 层级。

详细接口和验收条件见 `docs/windows-desktop-host.md`。

## 实施记录

截至 2026-09-06，专用 STA Window Dispatcher、hidden provisional Surface、批量 `ReplaceSurfacesAsync`、`TaskbarCreated` 主信号与 350 ms 防抖、Host 重建式恢复，以及诊断工具的 `--shell-topology`/限时 `--color-block` 均已实现并通过自动化测试。P1 Raised Desktop 研究另以 `--raised-desktop-probe`/`--raised-desktop-color-block` 隔离实现，不修改 Legacy 路径或生产 Adapter。

`--shell-topology` 还必须记录 session/window station/desktop，并把 `GetShellWindow() == 0` 解释为 `Inconclusive / Shell unavailable`，而不是该 build 的 0 候选。窗口关系算法使用可注入只读探针和合成窗口树自动验证；真实 Explorer 结果仍只属于交互式桌面验收。

这只关闭了 ADR 的代码迁移项，没有关闭真实 Windows Shell 验收项。Legacy WorkerW 的生产 allowlist 仍为空；`26200.9168` 在当前 Legacy 参数下不受支持。Raised 的两种 GDI 路径不可见，但无边框 Host-owned DComp 已通过单屏呈现和 Explorer generation 重建诊断验收，确认新旧 Shell PID/HWND 不复用、重新发现和新 Surface 创建成立。该结果仍未覆盖独立 Renderer child、产品恢复和完整显示矩阵，初次 Raised 请求的 Shell WorkerW 副作用也未证明可撤销；生产 Raised Desktop 继续主动失败。结构候选、可见呈现与生产 attachment 的后续边界由 ADR-008 定义。Stage B 保持 `In progress / Not accepted`，详情见 `docs/implementation-status.md`。
