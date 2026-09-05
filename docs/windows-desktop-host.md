# Windows Desktop Host 设计与实现差距

> 文档性质：Stage B 目标设计和当前差距记录，不代表功能已经完成。根目录 `ARCHITECTURE.md` 仍是最高约束。

## 冲突处理结论

| 问题 | 结论 | 文档约束 |
|---|---|---|
| Raised Desktop 状态被写得过于成熟 | 修正文案 | 当前 `RaisedDesktopAdapter` 是会主动失败的占位实现；能力探测和分层验证完成前不得启用 |
| Surface 没有专属窗口线程 | 接受整改方向 | 引入一个 Host 进程级专用 Window Dispatcher，所有自有 HWND 操作封送到该线程 |
| 首帧切换缺少端口 | 接受并变更目标端口 | `CreateSurfaceAsync` 创建隐藏 provisional Surface；新增批量 `ReplaceSurfacesAsync` |
| Explorer 恢复只轮询且重挂旧 HWND | 接受整改方向 | `TaskbarCreated` 为主信号、句柄/进程验证为后备；Host 重建 Surface 并重新 Attach Renderer |
| 色块诊断尚未实现 | 修正状态 | 当前工具仅输出显示器拓扑；色块模式必须在 Dispatcher/替换语义落地后实现 |
| 防抖值 250 ms 与架构不一致 | 直接统一 | 文档固定为 350 ms，位于根架构允许的 300–500 ms 范围 |
| Stage B 状态过于超前 | 修正状态 | Stage B 未完成；产品入口未接线，桌面附着和恢复尚未完成真实验收 |

## 1. Shell 适配器边界

WorkerW/Progman/Raised Desktop 不是 LiveWall 可以依赖的公开动态壁纸 API，因此全部视为按 Windows Shell build 验证的适配策略，而不是平台保证。

当前状态：

- `RaisedDesktopAdapter` 仅是占位符，`DiscoverAsync` 主动报告未验证；
- Adapter Selector 随后尝试 Legacy WorkerW；
- Legacy WorkerW 已有发现代码，但在真实桌面层级、Explorer 重启、DPI/热插拔矩阵验收完成前仍标记为 Experimental；
- 若没有通过验证的附着点，必须失败关闭或回退静态壁纸，不得因为找到了 `Progman` 就宣称支持 Raised Desktop。

一个适配器只有同时满足以下条件才能返回 attach point：

1. 当前 Shell build 位于该适配器的已验证范围；
2. 重新枚举得到的类名、父子关系、进程身份和窗口层级符合该版本规则；
3. 附着点可接受测试子窗口，且测试窗口位于桌面图标下方；
4. Explorer 重启后能够重新发现，不依赖旧 HWND；
5. 失败不会遮挡图标、任务栏或普通应用窗口。

Build allowlist、层级指纹和验收结果由 `DesktopHostDiagnostics` 采集；未经记录的 build 默认不启用 Raised Desktop。

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

## 4. 应用壁纸顺序

```text
HostCommandLoop 接收 ApplyWallpaperCommand
  → 后台创建全部隐藏 provisional Surface
  → 启动/选择 Renderer
  → AttachSurface + LoadWallpaper
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

## 6. 诊断和 Stage B 完成门槛

`DesktopHostDiagnostics` 当前仅实现只读显示器拓扑输出。未来新增的色块模式必须显式参数启动、默认只读、超时自动清理，并验证：

- 测试 Surface 位于桌面图标下方，不遮挡任务栏和普通窗口；
- 创建、定位、显隐、销毁均发生在 Window Dispatcher；
- provisional Surface 在激活前不可见；
- 首帧交换不出现明显黑帧或旧/新双重可见；
- Explorer 重启后重建 Surface，而不是复用旧 HWND；
- 单屏/多屏、负坐标、100–200% DPI、热插拔均通过；
- 诊断退出、取消或异常时不留下孤儿窗口。

完成这些真实桌面验收之前，Stage B 保持 `In progress / Not accepted`。

## 7. 官方依据

- [DestroyWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-destroywindow)：创建窗口之外的线程不能销毁该窗口。
- [Window Messages](https://learn.microsoft.com/en-us/windows/win32/learnwin32/window-messages)：每个创建窗口的线程拥有消息队列，并通过消息循环分派消息。
- [Taskbar creation notification](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar#taskbar-creation-notification)：`TaskbarCreated` 广播行为，以及 DPI 变化也可能触发的说明。
- [IsWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindow)：外部 HWND 存在竞态且句柄可能复用。
- [EndDeferWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enddeferwindowpos)：在单个屏幕刷新周期更新多个窗口的位置和尺寸。
- [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)：显隐、Z 顺序和 `SWP_NOACTIVATE` 语义。
- [SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent)：父窗口、样式和 DPI awareness 限制。

