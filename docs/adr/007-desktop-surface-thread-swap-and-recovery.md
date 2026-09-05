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

## 影响

- Application 端口会先于代码实现发生受控不一致；Stage B 完成前必须补齐实现和契约测试。
- Surface 生命周期从二态创建/销毁变为 Provisional/Active/Retired/Destroyed。
- HostCommandLoop 需要在首帧后调用显式替换端口，再提交 Session/Assignment。
- Explorer 恢复可保留 Renderer 和播放位置，但 Surface 必须重建。
- `DesktopHostDiagnostics` 增加真实色块模式前，仍保持只读拓扑工具。

## 迁移与回退

迁移顺序为 Window Dispatcher → 隐藏 provisional 创建 → 批量替换端口 → Host 接线 → Explorer 信号/恢复 → 色块验收。任一步失败时保持 Raised Desktop 禁用；运行期交换失败保留旧 Surface，恢复失败则回退静态壁纸或报告不可用，不启用未经验证的 Shell 层级。

详细接口和验收条件见 `docs/windows-desktop-host.md`。

