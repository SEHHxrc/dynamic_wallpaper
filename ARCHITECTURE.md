# Windows 动态壁纸项目技术架构设计

> **架构治理声明**：本文件是本仓库开发、评审、测试和发布的根本技术边界。文中的“必须 / 不得 / 仅允许”均为强制约束。任何偏离都必须先提交 ADR，说明动机、替代方案、迁移影响和回退方式；ADR 获准并同步修订本文后，相关实现方可合入。代码与本文冲突时，应优先视为代码缺陷，而不是默认放宽架构边界。

> 文档状态：实现基线  
> 代号：`LiveWall`（占位名，产品命名后统一替换）  
> 目标平台：Windows 10 1903+、Windows 11，首发 `win-x64`  
> 核心原则：原生轻量、渲染隔离、事件驱动、外部格式只导入不渗透运行时

## 1. 产品目标与边界

### 1.1 首版目标

首版交付一个可日常使用的 Windows 动态壁纸程序，支持：

- 本地视频：MP4、WebM、MKV 等 libmpv 可解码格式；
- GIF：统一交由视频渲染器处理，不自研 GIF 播放器；
- 本地 Web：HTML、CSS、JavaScript、Canvas、WebGL2；
- 多显示器：每屏独立、复制、跨屏三种布局；
- 显示模式：Cover、Contain、Stretch、Center；
- 托盘、开机启动、壁纸库、属性配置、播放列表基础能力；
- 锁屏、息屏、远程会话、全屏应用、电池模式下的自动暂停或降频；
- Explorer 重启恢复、播放器崩溃隔离与熔断；
- Wallpaper Engine 视频和 Web 工程的高兼容导入；
- `.pkg/.mpkg` 的识别、资源提取、兼容性分析和安全降级导入。

### 1.2 明确不属于首版

- 不承诺完整播放所有 Wallpaper Engine `scene.pkg`；
- 不实现完整粒子编辑器、3D 编辑器或 SceneScript 运行时；
- 不执行壁纸包中的任意 EXE、DLL、PowerShell 或批处理；
- 不内置 Steam Workshop 非官方下载器；
- 不在首版建设在线商店、账号、云同步、社交和内容审核；
- 不支持 Windows 锁屏动态壁纸；
- 不以跨平台为目标；
- 不把 Unity、Godot、Electron、CEF 或 Qt 作为程序核心依赖。

### 1.3 Wallpaper Engine 兼容等级

| 内容 | 预期兼容度 | 实现方式 |
|---|---:|---|
| 视频壁纸目录 | 完整或接近完整 | 读取 `project.json`，导入原视频 |
| Web 壁纸目录 | 高 | WebView2 + Wallpaper Engine Web API 兼容脚本 |
| 普通 `scene.pkg` | 部分 | 解包、分析、提取素材；简单场景后续转换 |
| 移动版 `.mpkg` 中的视频/图片 | 高 | 解包后转为原生视频或图片包 |
| 移动版 `.mpkg` Scene | 部分 | 简单场景转换，否则预览降级 |
| 任意复杂 Scene | 不承诺 | 可选委托给用户已安装的 Wallpaper Engine |
| Application/EXE 壁纸 | 不支持 | 安全边界禁止执行 |

兼容必须在 UI 中显示为 `Native / Full / Partial / PreviewOnly / Unsupported`，不得使用含糊的“已兼容”标识。

## 2. 技术栈定稿

| 领域 | 技术选型 | 约束 |
|---|---|---|
| 语言与运行时 | C#、.NET 10 LTS | 开启 nullable、分析器和警告即错误 |
| 设置界面 | WinUI 3、Windows App SDK 当前 Stable | 独立进程，用户关闭界面后完全退出 |
| 后台核心 | C# WinExe + Win32 P/Invoke | 无主窗口，仅托盘和消息窗口常驻 |
| 桌面嵌入 | Win32 HWND + WorkerW/Progman 适配层 | 未公开 Shell 细节限定在一个项目内；按 build 验证，未知环境失败关闭 |
| GPU 合成 | DirectComposition | Web 使用 Composition Controller；视频以子 HWND 接入 |
| 视频播放 | libmpv，D3D11，`gpu-next`，`hwdec=auto-safe` | 独立进程；发行前审计 mpv/FFmpeg 许可证构建 |
| Web 播放 | Microsoft Edge WebView2 Evergreen | 共享 Environment/UDF；按需创建，暂停时 TrySuspend |
| 音频分析 | WASAPI Loopback + Hann Window + Real FFT | 仅壁纸声明需要时启用，默认不捕获 |
| 进程间通信 | Windows Named Pipe + 长度前缀 UTF-8 JSON | 不引入 gRPC；命令和事件有协议版本 |
| 进程监管 | Windows Job Object + Process Supervisor | Core 退出时清理自有 Renderer；崩溃指数退避 |
| 配置与状态 | System.Text.Json + 原子文件替换 | 首版不引入数据库；仓库接口允许后续换 SQLite |
| 日志 | Microsoft.Extensions.Logging + 滚动 JSON + EventSource | 日志脱敏；Renderer 日志按会话区分 |
| 原生包格式 | ZIP 容器，扩展名 `.lwpkg` | 公开、版本化、可检查，不另造压缩格式 |
| 分发 | 签名 MSIX；可选按用户安装器 | 不要求管理员权限；优先 Store/App Installer 更新 |
| 测试 | xUnit + FluentAssertions；集成测试使用真实 Windows 会话 | 外部格式使用自制、获授权或最小合成样本 |

## 3. 总体运行架构

```text
┌─────────────────────────────┐
│ LiveWall.UI                 │
│ WinUI 3，按需运行           │
└──────────────┬──────────────┘
               │ UI RPC / Named Pipe
               ▼
┌──────────────────────────────────────────────────────────────┐
│ LiveWall.Host                                                │
│ 单实例、托盘、命令循环、会话编排、Renderer 监管、状态查询   │
└──────┬───────────────────┬──────────────────────┬────────────┘
       │                   │                      │
       ▼                   ▼                      ▼
 Platform.Windows      Importers             Infrastructure
 Desktop/Display/      Native/WE             Store/Files/IPC/
 Power/Session/Input   pkg/mpkg/WebShim      Logging/Config
       │
       │ HWND / Surface + Renderer IPC
       ▼
┌───────────────────────┐       ┌──────────────────────────────┐
│ Renderer.Video        │       │ Renderer.Web                 │
│ libmpv + D3D11        │       │ WebView2 + DirectComposition │
└───────────────────────┘       └──────────────────────────────┘
```

长期常驻进程只有 `LiveWall.Host` 和当前实际需要的 Renderer。UI 关闭后不得残留 UI 进程。

## 4. 仓库与项目树

```text
LiveWall.sln
├─ global.json
├─ Directory.Build.props
├─ Directory.Packages.props
├─ README.md
├─ LICENSE
│
├─ docs/
│  ├─ architecture.md
│  ├─ package-format.md
│  ├─ renderer-protocol.md
│  ├─ windows-desktop-host.md
│  ├─ wallpaper-engine-compatibility.md
│  ├─ security-model.md
│  └─ adr/
│     ├─ 001-canonical-package-only-at-runtime.md
│     ├─ 002-renderers-out-of-process.md
│     ├─ 003-single-writer-host-loop.md
│     ├─ 004-versioned-desktop-adapters.md
│     ├─ 005-wallpaper-engine-is-import-only.md
│     ├─ 006-local-named-pipe-identity.md
│     └─ 007-desktop-surface-thread-swap-and-recovery.md
│
├─ src/
│  ├─ LiveWall.Domain/
│  │  ├─ Wallpapers/
│  │  ├─ Displays/
│  │  ├─ Layouts/
│  │  ├─ Playback/
│  │  ├─ Sessions/
│  │  ├─ Importing/
│  │  └─ Compatibility/
│  │
│  ├─ LiveWall.Contracts/
│  │  ├─ Rpc/
│  │  ├─ RendererProtocol/
│  │  ├─ Commands/
│  │  ├─ Events/
│  │  └─ Dtos/
│  │
│  ├─ LiveWall.Application/
│  │  ├─ Abstractions/
│  │  ├─ Library/
│  │  ├─ Importing/
│  │  ├─ Layouts/
│  │  ├─ Playback/
│  │  ├─ Policies/
│  │  └─ Sessions/
│  │
│  ├─ LiveWall.Platform.Windows/
│  │  ├─ Desktop/
│  │  │  ├─ DesktopWindowDispatcher.cs
│  │  │  ├─ LegacyWorkerWAdapter.cs
│  │  │  ├─ RaisedDesktopAdapter.cs
│  │  │  └─ ExplorerMonitor.cs
│  │  ├─ Displays/
│  │  ├─ Power/
│  │  ├─ Sessions/
│  │  ├─ Foreground/
│  │  ├─ Input/
│  │  ├─ Audio/
│  │  ├─ Processes/
│  │  └─ NativeMethods/
│  │
│  ├─ LiveWall.Infrastructure/
│  │  ├─ Configuration/
│  │  ├─ Persistence/
│  │  ├─ FileSystem/
│  │  ├─ Packages/
│  │  ├─ Ipc/
│  │  └─ Logging/
│  │
│  ├─ LiveWall.Importers/
│  │  ├─ Native/
│  │  └─ WallpaperEngine/
│  │     ├─ ProjectDirectory/
│  │     ├─ Pkg/
│  │     ├─ Mpkg/
│  │     ├─ Tex/
│  │     ├─ SceneAnalysis/
│  │     ├─ WebCompat/
│  │     └─ Delegate/
│  │
│  ├─ LiveWall.Host/
│  │  ├─ Bootstrap/
│  │  ├─ CommandLoop/
│  │  ├─ Orchestration/
│  │  ├─ Rpc/
│  │  ├─ Tray/
│  │  └─ Program.cs
│  │
│  ├─ LiveWall.UI/
│  │  ├─ Pages/
│  │  ├─ ViewModels/
│  │  ├─ Controls/
│  │  ├─ Services/
│  │  └─ App.xaml
│  │
│  ├─ LiveWall.Renderer.Video/
│  │  ├─ Mpv/
│  │  ├─ Windowing/
│  │  ├─ Protocol/
│  │  └─ Program.cs
│  │
│  └─ LiveWall.Renderer.Web/
│     ├─ WebView/
│     ├─ Composition/
│     ├─ CompatibilityScripts/
│     ├─ Protocol/
│     └─ Program.cs
│
├─ packages/
│  ├─ schemas/
│  ├─ examples/
│  └─ web-runtime/
│
├─ tests/
│  ├─ LiveWall.Domain.Tests/
│  ├─ LiveWall.Application.Tests/
│  ├─ LiveWall.Package.Tests/
│  ├─ LiveWall.WallpaperEngine.Tests/
│  ├─ LiveWall.Renderer.ContractTests/
│  └─ LiveWall.Windows.IntegrationTests/
│
├─ tools/
│  ├─ PackageInspector/
│  ├─ RendererHarness/
│  └─ DesktopHostDiagnostics/
│
└─ packaging/
   ├─ msix/
   ├─ installer/
   └─ third-party-notices/
```

## 5. 项目依赖边界

```text
UI ───────────────────────────────→ Contracts
                                           ▲
                                           │ Named Pipe
                                           ▼
Host ───────────────→ Application ───────→ Domain
 │                         ▲
 ├─→ Platform.Windows ─────┤
 ├─→ Infrastructure ───────┤
 └─→ Importers ────────────┘

Renderer.Video ───────────────────→ Contracts
Renderer.Web ─────────────────────→ Contracts
```

硬性规则：

1. `Domain` 不引用任何本项目程序集，不含 Win32、文件系统、IPC、JSON 和 UI 类型。
2. `Contracts` 只含跨进程可序列化的数据，不放业务服务实现。
3. `Application` 定义用例和端口，不直接 P/Invoke、不访问真实磁盘。
4. `Platform.Windows` 只实现 Windows 平台能力，不决定产品业务策略。
5. `Infrastructure` 不引用 UI 和 Renderer。
6. `UI` 只通过 RPC DTO 操作 Host，不得直接访问壁纸目录或启动 Renderer。
7. Renderer 不引用 Host、Application、Importer；只理解 Renderer Protocol 和自己的引擎。
8. Wallpaper Engine 类型不得出现在 Video/Web Renderer 的公开接口中。
9. `LiveWall.Host` 是唯一组合根，负责依赖注入和进程生命周期。
10. 首版不引入 MediatR、事件溯源、分布式消息总线或微服务框架。

## 6. 核心领域模型

应严格区分“壁纸定义”“显示器分配”和“运行会话”：

```csharp
public sealed record WallpaperDefinition(
    WallpaperId Id,
    string Version,
    WallpaperKind Kind,
    string EntryPoint,
    WallpaperMetadata Metadata,
    WallpaperCapabilities Capabilities,
    WallpaperOrigin Origin);

public sealed record WallpaperAssignment(
    DisplayId DisplayId,
    WallpaperId WallpaperId,
    LayoutMode LayoutMode,
    FitMode FitMode,
    PropertyPresetId? PresetId);

public sealed record WallpaperSession(
    SessionId Id,
    WallpaperId WallpaperId,
    IReadOnlyList<DisplayId> Displays,
    RendererId RendererId,
    PlaybackState State,
    long Generation);
```

推荐枚举：

```csharp
public enum WallpaperKind
{
    Video,
    Web,
    Image,
    Scene,
    External
}

public enum PlaybackState
{
    Starting,
    Playing,
    Throttled,
    Paused,
    Suspended,
    Stopped,
    Faulted
}

public enum WallpaperOrigin
{
    Native,
    WallpaperEngineProject,
    WallpaperEnginePkg,
    WallpaperEngineMpkg,
    WallpaperEngineDelegate
}

public enum CompatibilityGrade
{
    Native,
    Full,
    Partial,
    PreviewOnly,
    Unsupported
}
```

`Generation` 是并发正确性的边界：每次重新应用、换屏或恢复都增加代数，旧代异步操作完成后不得修改新会话。

## 7. Application 层接口

### 7.1 壁纸仓库

```csharp
public interface IWallpaperRepository
{
    Task<WallpaperLibraryEntry?> FindAsync(
        WallpaperId id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WallpaperLibraryEntry>> ListAsync(
        WallpaperQuery query,
        CancellationToken cancellationToken);

    Task SaveImportedAsync(
        ImportedWallpaper wallpaper,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        WallpaperId id,
        CancellationToken cancellationToken);
}
```

`WallpaperLibraryEntry` 同时提供 `WallpaperDefinition`、经验证的 `CanonicalContentRoot` 和兼容性报告。仓库只管理已验证的原生化内容，不保存临时解包状态。

### 7.2 外部格式导入

```csharp
public interface IWallpaperImporter
{
    string Id { get; }

    ValueTask<ImportProbeResult> ProbeAsync(
        ImportSource source,
        CancellationToken cancellationToken);

    Task<ImportResult> ImportAsync(
        ImportRequest request,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken);
}
```

`ProbeAsync` 只读少量文件头和元数据，不完整解包；`ImportAsync` 必须在隔离临时目录中完成并生成兼容性报告。

### 7.3 包容器读取

```csharp
public interface IPackageContainerReader
{
    string FormatId { get; }

    bool CanRead(ReadOnlyMemory<byte> header);

    Task<PackageIndex> ReadIndexAsync(
        Stream stream,
        CancellationToken cancellationToken);

    Task ExtractAsync(
        Stream stream,
        PackageIndex index,
        string outputDirectory,
        ExtractionPolicy policy,
        CancellationToken cancellationToken);
}
```

实现包括：

- `NativeZipPackageReader`
- `WallpaperEnginePkgContainerReader`
- `WallpaperEngineMpkgContainerReader`

容器读取器只理解目录表和二进制范围，不理解场景语义。

### 7.4 内容转换

```csharp
public interface IContentConverter
{
    bool CanConvert(PackageAsset asset, TargetContentFormat target);

    Task<ConvertedAsset> ConvertAsync(
        PackageAsset asset,
        TargetContentFormat target,
        CancellationToken cancellationToken);
}
```

实现可包括 TEX、图片、视频标准化和未来的 Scene 子集转换。转换失败必须返回特性损失报告，不得静默丢失。

### 7.5 Renderer Provider

```csharp
public interface IRendererProvider
{
    RendererDescriptor Descriptor { get; }

    RendererMatch Match(
        WallpaperDefinition wallpaper,
        RuntimeEnvironment environment);

    Task<IRendererSession> CreateAsync(
        RendererLaunchContext context,
        CancellationToken cancellationToken);
}
```

实现：

- `VideoRendererProvider`
- `WebRendererProvider`
- `StaticImageRendererProvider`
- `WallpaperEngineDelegateProvider`（可选）
- `SceneRendererProvider`（预留，不在首版）

Provider 由能力匹配决定，不通过 `switch WallpaperKind` 散落在 Host 中。

### 7.6 Renderer 会话

```csharp
public interface IRendererSession : IAsyncDisposable
{
    SessionId Id { get; }

    IAsyncEnumerable<RendererEvent> ReadEventsAsync(
        CancellationToken cancellationToken);

    Task SendAsync(
        RendererCommand command,
        CancellationToken cancellationToken);
}
```

`IRendererSession` 是 Host 侧的进程代理；具体 libmpv/WebView2 类型不能越过此接口。

### 7.7 桌面宿主

```csharp
public interface IDesktopHost
{
    Task<DesktopTopology> EnsureTopologyAsync(
        DisplayTopology displays,
        CancellationToken cancellationToken);

    Task<DesktopSurface> CreateSurfaceAsync(
        SurfaceRequest request,
        CancellationToken cancellationToken);

    Task ReplaceSurfacesAsync(
        SurfaceReplacement replacement,
        CancellationToken cancellationToken);

    Task DestroySurfaceAsync(
        SurfaceId surfaceId,
        CancellationToken cancellationToken);
}

public sealed record SurfaceReplacement(
    IReadOnlyList<SurfaceId> ProvisionalSurfaceIds,
    IReadOnlyList<SurfaceId> ReplacedSurfaceIds,
    long DesktopTopologyRevision);
```

`CreateSurfaceAsync` 创建隐藏的 provisional 容器 HWND。Host 通过 Renderer Protocol 的 `AttachSurface` 把该句柄交给 Renderer；Renderer 只在容器内创建自己的子窗口并返回 `SurfaceAttached`，Renderer HWND 不反向穿过 Application 端口。全部新 Renderer 报告 `FirstFramePresented` 后，Host 单写者调用 `ReplaceSurfacesAsync` 显式激活新 Surface 并隐藏旧 Surface；只有替换成功才能提交活动会话和 Assignment。

Platform.Windows 必须通过唯一专用 Window Dispatcher 线程创建、定位、显隐、换父级和销毁所有自有 HWND。该线程设为 STA 并运行 Win32 消息循环；`DestroyWindow` 必须在创建窗口的线程执行。逻辑状态提交是原子的；同一父 HWND 的视觉交换使用 DeferWindowPos 系列尽量在一个屏幕刷新周期完成，不承诺跨父窗口的操作系统事务原子性。

Shell 版本差异继续隔离为：

```csharp
public interface IDesktopHostAdapter
{
    bool IsSupported(WindowsShellSnapshot snapshot);

    Task<DesktopAttachPoint> DiscoverAsync(
        CancellationToken cancellationToken);

    Task RecoverAsync(
        CancellationToken cancellationToken);
}
```

`IsSupported` 必须基于已验证的 Shell build 能力和层级规则，不能只依赖用户开关。`RecoverAsync` 只重新探测/准备附着点，不得尝试复用或重挂旧 Surface HWND。Raised Desktop 在诊断矩阵完成前保持禁用；Legacy WorkerW 也属于 Experimental 回退策略。

### 7.8 显示器与布局

```csharp
public interface IDisplayTopologySource
{
    DisplayTopology Current { get; }
    event EventHandler<DisplayTopologyChangedEventArgs> Changed;
}

public interface ILayoutPlanner
{
    LayoutPlan CreatePlan(
        DisplayTopology topology,
        IReadOnlyList<WallpaperAssignment> assignments);
}
```

显示器持久 ID 使用 DisplayConfig 设备路径/目标信息，不使用数组序号。

### 7.9 播放策略

```csharp
public interface IPlaybackPolicyEvaluator
{
    PlaybackDecision Evaluate(
        SystemStateSnapshot system,
        UserPlaybackPolicy policy,
        WallpaperSessionSnapshot session);
}
```

这是纯函数，不发 IPC、不查进程、不操作文件。输出包括目标状态、原因、FPS、静音和质量档位。

`WallpaperSessionSnapshot` 必须同时包含 `UserPlaybackIntent` 与实际 `PlaybackState`，以区分用户 Pause/Stop 和系统策略造成的暂停，防止系统恢复时错误自动播放。

### 7.10 进程监管

```csharp
public interface IProcessSupervisor
{
    Task<SupervisedProcess> StartAsync(
        ProcessLaunchSpec spec,
        CancellationToken cancellationToken);

    Task StopAsync(
        ProcessId processId,
        ShutdownMode mode,
        CancellationToken cancellationToken);

    event EventHandler<SupervisedProcessExitedEventArgs> ProcessExited;
}
```

仅该接口拥有 Renderer 进程句柄和 Job Object；Application 不直接调用 `Process.Start`。

## 8. Renderer 协议

### 8.1 协议帧

```json
{
  "protocol": { "major": 1, "minor": 0 },
  "messageId": "01J...",
  "sessionId": "01J...",
  "generation": 7,
  "type": "LoadWallpaper",
  "payload": {}
}
```

要求：

- 4 字节 little-endian 长度前缀；
- UTF-8 JSON；
- 单帧默认上限 1 MiB；
- AudioFrame 等高频数据后续可切共享内存，但控制消息仍走 Pipe；
- `messageId` 用于幂等和日志关联；
- `sessionId + generation` 用于拒绝过期命令；
- 主版本不匹配拒绝连接，次版本依能力协商降级。

### 8.2 Host 发往 Renderer 的命令

```text
Initialize
AttachSurface
LoadWallpaper
Play
Pause
Throttle
Suspend
Resume
SetBounds
SetFit
SetVolume
SetProperties
SetAudioFrame
Shutdown
```

`CapturePreview` 不属于协议 v1.0；只有在定义强类型结果事件并把协议 minor 提升到 1.1 后才能加入。

### 8.3 Renderer 发往 Host 的事件

```text
Hello
Initialized
SurfaceAttached
ContentLoaded
FirstFramePresented
PlaybackStateChanged
TelemetryUpdated
RecoverableError
FatalError
ShutdownCompleted
```

Host 必须等待 `FirstFramePresented` 再移除旧壁纸，避免黑屏切换。

## 9. UI RPC 边界

UI 允许调用：

```text
GetAppState
ListWallpapers
ProbeImport
ImportWallpaper
CancelImport
ApplyWallpaper
RemoveWallpaper
SetWallpaperProperty
SetAssignment
SetPlaybackPolicy
Play / Pause / Stop
GetDiagnostics
OpenLogFolder
ShutdownHost
```

Host 推送 UI 事件：

```text
AppStateChanged
LibraryChanged
ImportProgressChanged
AssignmentChanged
PlaybackStateChanged
DisplayTopologyChanged
RendererFaulted
CompatibilityWarningRaised
```

UI 事件信封和 `AppStateDto` 必须携带 Host 的 `stateRevision`。UI 不得传递任意可执行命令行；所有请求都使用强类型 DTO 和枚举。

## 10. Host 并发模型

Host 采用单写者 Actor 风格命令循环：

```text
UI RPC ───────────────┐
Win32 系统事件 ──────┤
Renderer 事件 ───────┼→ Channel<HostCommand> → HostCommandLoop
Explorer 事件 ───────┤                         │
后台任务完成事件 ────┘                         ▼
                                         SessionCoordinator
```

Win32 HWND 另有一个专用 Window Dispatcher：

```text
HostCommandLoop / SessionCoordinator
  → async dispatcher work item
  → STA Window Dispatcher + GetMessage loop
  → 注册/创建/定位/显隐/换父级/销毁 HWND
  → Task 完成或发布不可变 Shell 事件
```

硬性约束：

- 只有 `HostCommandLoop` 可以修改活动会话、Surface 和 Assignment 内存状态；
- Win32 回调、Pipe 回调和 Renderer 回调只能投递命令；
- 解包、转码、哈希在后台执行；结果携带 Generation 回到命令循环；
- 不在 Win32 WindowProc 中执行阻塞 I/O；
- 系统事件做合并和 300–500 ms 防抖；Desktop/Display 默认统一为 350 ms；
- 状态转换必须幂等。
- WindowProc 只发布事件；任何自有 Surface 的 `DestroyWindow` 都必须封送到创建该窗口的 Dispatcher 线程。

Host 命令至少包括：

```text
ApplyWallpaperCommand
RemoveWallpaperCommand
DisplayTopologyChangedCommand
SystemPolicyChangedCommand
RendererEventCommand
RendererExitedCommand
ExplorerRestartedCommand
ImportCompletedCommand
ShutdownCommand
```

## 11. 关键调用关系

### 11.1 启动恢复

```text
Host.Program
  → 获取单实例 Mutex
  → 初始化日志和配置
  → 启动 HostCommandLoop
  → 初始化电源/会话/前台/显示器事件源
  → DesktopHost 发现 Progman/WorkerW/DefView
  → Repository 读取上次 Assignment
  → LayoutPlanner 生成恢复计划
  → SessionCoordinator 启动所需 Renderer
  → 等待首帧
  → 发布 AppStateChanged
```

### 11.2 应用壁纸

```text
UI.ApplyWallpaper
  → Named Pipe RPC
  → ApplyWallpaperCommand
  → Repository.Find
  → LayoutPlanner.CreatePlan
  → RendererRegistry.Match
  → ProcessSupervisor.Start
  → Renderer.Initialize/Hello
  → DesktopHost.CreateSurface（hidden/provisional）
  → Renderer.AttachSurface
  → Renderer.LoadWallpaper
  → 等待全部 FirstFramePresented
  → 回投 ApplyPreparedCommand
  → HostCommandLoop 校验 Generation + DesktopTopologyRevision
  → DesktopHost.ReplaceSurfaces（显式激活新 Surface、隐藏旧 Surface）
  → 原子提交活动 Session/Assignment
  → 停止旧 Renderer
  → 保存 Assignment
  → UI 收到 AssignmentChanged
```

### 11.3 系统策略变化

```text
Power/Session/Foreground/Display Signal
  → SystemStateAggregator
  → SystemPolicyChangedCommand
  → PlaybackPolicyEvaluator.Evaluate
  → 比较旧/新 PlaybackDecision
  → 仅发送所需 RendererCommand
```

状态优先级：

```text
息屏 / 锁屏 / 会话断开
  > 用户显式 Stop
  > 全屏应用策略
  > 电池与节能策略
  > 最大化应用策略
  > 正常播放
```

### 11.4 Explorer 重启

```text
隐藏顶级信号窗口收到 TaskbarCreated，或后备探针发现附着点身份/层级失效
  → 350 ms 合并防抖
  → 重新枚举并验证 Shell 层级
  → ExplorerRestartedCommand
  → HostCommandLoop 提升 Generation / 标记恢复中
  → DesktopHostAdapter.Discover
  → 重建 hidden/provisional DesktopSurface（不得重挂旧 HWND）
  → Renderer.SetBounds/AttachSurface
  → 等待 SurfaceAttached 与恢复后的首帧
  → DesktopHost.ReplaceSurfaces
  → 尽量保留 Renderer 与播放位置
```

`TaskbarCreated` 是 Shell 拓扑可能变化的提示，不是 Explorer 重启的充分条件；Windows 10 在主显示器 DPI 变化时也可能广播该消息。`IsWindow` 对外部 HWND 仅能作为后备提示，因为检查后窗口可能销毁且句柄可能被复用；恢复前必须重新枚举并组合验证类名、进程、父子关系和适配器层级规则。

### 11.5 Renderer 崩溃

```text
ProcessSupervisor.ProcessExited
  → RendererExitedCommand
  → 60 秒窗口内统计次数
  → 第 1 次立即恢复
  → 第 2 次延迟 2 秒
  → 第 3 次延迟 8 秒
  → 再次失败则熔断
  → 显示静态预览并通知 UI
```

## 12. Wallpaper Engine 导入边界

### 12.1 总原则

外部格式永远执行：

```text
Probe → Analyze → Extract → Convert → Validate → Commit
```

运行时只接收 `WallpaperDefinition + 原生内容目录`，绝不让 Renderer 直接打开 `.pkg/.mpkg`。

### 12.2 Wallpaper Engine Importer 结构

```text
WallpaperEngineImporter
├─ ProjectJsonReader
├─ ContentClassifier
├─ PkgContainerReader
├─ MpkgContainerReader
├─ TexAssetReader
├─ SceneFeatureAnalyzer
├─ VideoImportStrategy
├─ WebImportStrategy
├─ SceneSubsetImportStrategy      # 非首版
├─ PreviewOnlyImportStrategy
├─ WallpaperEngineWebShim
└─ WallpaperEngineDelegateProvider
```

### 12.3 决策规则

```text
project.json:type=video
  → 导入原媒体
  → Full

project.json:type=web
  → 复制 Web 项目
  → 注入兼容脚本
  → Full 或 Partial

project.json:type=scene + scene.pkg
  → 解包与特性分析
  ├─ 能转视频/图片 → 原生包
  ├─ 有动画预览 → PreviewOnly
  ├─ 有未来 Scene 子集能力 → Partial
  ├─ 已安装 Wallpaper Engine 且用户选择委托 → Delegate
  └─ Unsupported

*.mpkg
  → 解包
  ├─ 嵌入视频 → Video/Full
  ├─ 图片或时段图片 → Image/Full
  ├─ 简单 Scene → Partial
  └─ PreviewOnly 或 Unsupported
```

### 12.4 Web 兼容脚本范围

首批兼容：

- `window.wallpaperPropertyListener.applyUserProperties`
- `window.wallpaperPropertyListener.applyGeneralProperties`
- `window.wallpaperRegisterAudioListener`
- FPS 参数；
- 鼠标位置；
- 常用 bool、slider、combo、color、text 属性。

后续兼容：

- 媒体标题、封面、播放状态；
- 用户文件和目录属性；
- 时钟和基本系统状态。

永不默认兼容：

- 任意命令执行；
- 无限制本地文件访问；
- RGB 硬件直接控制；
- 未经用户授权的网络访问。

## 13. 原生 `.lwpkg` 格式

`.lwpkg` 是普通 ZIP：

```text
wallpaper.lwpkg
├─ wallpaper.json
├─ properties.schema.json       # 可选
├─ preview.webp
├─ license.txt                  # 可选
├─ content/
│  └─ wallpaper.mp4
├─ web/
│  ├─ index.html
│  ├─ scripts/
│  └─ assets/
└─ compatibility/
   └─ import-report.json        # 外部导入时生成
```

`wallpaper.json` 示例：

```json
{
  "schemaVersion": 1,
  "id": "com.example.aurora",
  "version": "1.0.0",
  "title": "Aurora",
  "type": "web",
  "entry": "web/index.html",
  "preview": "preview.webp",
  "permissions": {
    "network": false,
    "audioCapture": true,
    "pointerInput": true,
    "systemMetrics": false
  },
  "defaults": {
    "fit": "cover",
    "fpsLimit": 30,
    "muted": true
  }
}
```

用户属性值和显示器分配保存在 AppData，不回写包本身。

## 14. 数据目录

```text
%LocalAppData%/LiveWall/
├─ config/
│  ├─ settings.json
│  ├─ playback-policy.json
│  └─ assignments.json
├─ library/
│  └─ <wallpaper-id>/<version>/...
├─ state/
│  ├─ presets/
│  └─ last-session.json
├─ cache/
│  ├─ thumbnails/
│  ├─ webview2/
│  └─ imports/
├─ logs/
└─ crash/
```

持久化要求：

- 写入临时文件、flush、校验后原子替换；
- 配置带 `schemaVersion`；
- 迁移失败保留原文件并回退默认配置；
- 导入按内容 SHA-256 去重；
- 不在配置中保存原始音频样本、浏览历史或敏感 URL 参数。

## 15. 安全边界

所有壁纸包和 Web 内容都视为不可信输入。

### 15.1 包解析

- 根据魔数识别，不只看扩展名；
- 限制条目数量、单文件大小、总解压大小和嵌套深度；
- 检查 `offset + size` 溢出及文件边界；
- 拒绝绝对路径、`..`、设备路径、重解析点和符号链接；
- 解包到随机临时目录；
- 完整验证后才原子移动到 library；
- Pkg/Mpkg/Tex 解析器必须进入 fuzz 测试；
- 解析失败不能导致 Host 崩溃。

### 15.2 Web

- 默认禁止网络；
- 本地资源通过 WebView2 Virtual Host Mapping 提供；
- 禁用下载、打印、新窗口和生产环境 DevTools；
- 外部链接交给默认浏览器；
- Bridge 使用 JSON Schema 校验的 `postMessage`；
- 不使用宽泛的 `AddHostObjectToScript`；
- 用户文件访问必须通过显式权限和受限映射。

### 15.3 进程和 IPC

- 所有进程以普通用户权限运行；
- Pipe 使用 `LOCAL\` 登录会话作用域，ACL 仅允许当前用户与相同提权级别；
- 自有 Renderer 纳入 Job Object；
- 命令有大小上限和类型白名单；
- 不接受远程 Pipe；每个 Renderer 使用随机 Pipe 名与一次性握手密钥；
- 不允许 UI 构造任意 Renderer 启动参数；
- 第三方 EXE 壁纸首版完全禁止。

## 16. 性能边界与验收目标

| 场景 | 目标 |
|---|---|
| Host 空闲 | CPU 平均低于 0.1%，工作集目标低于 50 MiB |
| UI 关闭 | UI 进程完全退出 |
| 息屏/锁屏 | Renderer 暂停、深度挂起或卸载，GPU 接近 0 |
| 1080p30 H.264 | 开启硬解，CPU 目标低于 3% |
| 全屏应用出现 | 500 ms 内暂停受影响显示器 |
| Explorer 重启 | 3 秒内恢复，不重复创建僵尸 Renderer |
| 显示器热插拔 | 不崩溃，不依赖旧显示器数组序号 |
| 切换壁纸 | 新壁纸首帧出现前保留旧壁纸 |
| Renderer 连续崩溃 | 指数退避并熔断，无无限重启闪烁 |

性能优化优先级：

1. 正确暂停/卸载；
2. 硬件视频解码；
3. 避免重复 Renderer 和重复 WebView2 Environment；
4. 默认 30 FPS；
5. 减少纹理和跨 GPU 拷贝；
6. 最后才考虑把 C# Host 改写成 C++/Rust。

## 17. 测试边界

### 17.1 单元测试

- Cover/Contain/Stretch 和负坐标虚拟桌面；
- PlaybackPolicy 优先级；
- Generation 丢弃过期结果；
- Renderer Provider 能力选择；
- manifest 版本和权限校验；
- 导入兼容等级计算；
- 重启退避和熔断。

### 17.2 契约测试

- UI RPC DTO 序列化兼容；
- Renderer 主/次协议版本握手；
- 未知字段向前兼容；
- 重复 messageId 幂等；
- 超大、截断和畸形帧拒绝。
- `IDesktopHost` 的 provisional 创建、批量替换、幂等销毁和过期 topology revision 拒绝。

### 17.3 包安全测试

- 路径穿越；
- 整数溢出；
- 重叠条目；
- 超大声明大小；
- 截断 `.pkg/.mpkg/.tex`；
- 压缩炸弹；
- 随机字节 fuzz；
- 格式版本变化。

### 17.4 Windows 集成矩阵

- Windows 10 22H2；
- 当前支持的 Windows 11 稳定版本；
- 100%、125%、150%、200% DPI；
- 单屏、横竖双屏、负坐标、不同刷新率；
- HDR 开关；
- 睡眠、唤醒、锁屏、解锁；
- RDP 连接和断开；
- Explorer 手动重启；
- 验证 Surface 的创建线程等于销毁/显隐/换父级操作线程；
- 验证 provisional Surface 在首帧前不可见，替换失败时旧 Surface 保持可见；
- 验证 `TaskbarCreated` 的 DPI 变化误触发不会造成 Surface/Renderer 重建风暴；
- 全屏独占、无边框全屏、最大化窗口；
- 集成显卡、独显和混合 GPU 笔记本。

## 18. 实现顺序

### 阶段 A：Foundation

- 创建 solution 和依赖约束；
- Domain、Contracts、Application；
- HostCommandLoop；
- UI RPC 和 Renderer Protocol；
- JSON 配置、日志、单实例。

完成定义：无真实播放器也能通过 FakeRenderer 完成完整 Apply/Pause/Stop/Crash 状态测试。

### 阶段 B：Windows Desktop Host

- 显示器枚举与稳定 ID；
- 专用 STA Window Dispatcher 和消息循环；
- Legacy WorkerW（Experimental，按 build/层级验证）；
- Raised Desktop（验证矩阵完成前保持禁用）；
- `TaskbarCreated` 主信号、后备探针与 350 ms 防抖；
- Explorer 恢复：重建 Surface 并重新 Attach Renderer；
- hidden provisional / Active / Retired / Destroyed Surface 生命周期；
- `ReplaceSurfacesAsync` 显式首帧交换；
- PerMonitorV2 DPI。

完成定义：显式启动、超时自动清理的测试色块窗口能稳定显示在图标下方；窗口操作线程一致；首帧前新 Surface 不可见；并通过 Explorer 重启、DPI 误触发、热插拔和多屏交换测试。完成这些真实桌面验收前 Stage B 状态必须保持 `In progress / Not accepted`。

### 阶段 C：Video Renderer

- libmpv 进程；
- HWND 嵌入；
- Load/Play/Pause/SetBounds/Volume；
- 首帧事件；
- 硬解和循环；
- 崩溃恢复。

完成定义：单屏、多屏、跨屏和 4K 视频稳定运行。

### 阶段 D：Policy Engine

- 锁屏、息屏、电源、RDP；
- 全屏与最大化检测；
- 策略纯函数；
- 防抖、幂等和恢复。

完成定义：系统状态变化不会产生重启风暴或音频误播放。

### 阶段 E：Web Renderer

- WebView2 Composition Controller；
- 共享 Environment；
- 本地虚拟 Host；
- 网络权限；
- Suspend/Resume；
- 鼠标位置与交互模式。

完成定义：本地 Canvas/WebGL 壁纸运行，暂停后 CPU 显著下降，桌面图标操作正常。

### 阶段 F：Package & Import

- `.lwpkg`；
- manifest 与属性 Schema；
- 原子导入；
- 视频/Web 工程导入；
- `.pkg/.mpkg` Probe 与安全提取；
- Wallpaper Engine Web Shim；
- 兼容性报告。

完成定义：任何外部导入都必须先变成原生包，Renderer 中不存在 Wallpaper Engine 容器解析代码。

### 阶段 G：Release

- MSIX/安装器；
- 代码签名；
- 第三方许可证清单；
- 崩溃日志导出；
- 性能基线；
- 升级和卸载数据策略。

## 19. 必须长期保持的架构不变量

1. 运行时只消费本项目规范化内容。
2. UI 不是系统状态的权威来源，Host 才是。
3. 只有 HostCommandLoop 修改活动会话。
4. Renderer 崩溃不能导致 Host 或其他 Renderer 崩溃。
5. Shell 私有行为只存在于 DesktopHostAdapter。
6. 外部包永远是不可信输入。
7. 用户未授权时不联网、不捕获音频、不读取任意文件。
8. 新 Renderer 通过 Provider 和协议加入，不修改 UI/Host 核心分支。
9. 兼容性损失必须对用户可见，不静默伪装成完整支持。
10. 未经性能测量，不为“看起来更原生”而引入 C++/Rust 重写。
11. 所有自有 Desktop Surface HWND 只能由专用 Window Dispatcher 创建和销毁。
12. 新 Surface 必须隐藏准备并经显式替换端口激活；等待首帧本身不等于完成切换。
13. Explorer/Shell 恢复必须重建 Surface，不依赖旧 HWND 仍有效。

## 20. 关键研究依据

- Wallpaper Engine 官方说明 `scene.pkg` 是打包后的 Scene，缺少完整项目数据，第三方解包工具不受官方支持：<https://help.wallpaperengine.io/en/functionality/editingwallpapers.html>
- Wallpaper Engine 官方 CLI 支持加载、暂停、停止及在窗口中打开壁纸：<https://help.wallpaperengine.io/en/functionality/cli.html>
- Wallpaper Engine Web 用户属性 API：<https://docs.wallpaperengine.io/en/web/customization/properties.html>
- Wallpaper Engine Web 音频 API：<https://docs.wallpaperengine.io/en/web/audio/visualizer.html>
- Wallpaper Engine 移动包和 Scene 移动优化流程：<https://help.wallpaperengine.io/en/mobile/pairing.html>
- 社区 RePKG-ng 证明 `.pkg/.mpkg/.tex` 容器与资源解析可实现，但不等于完整 Scene 运行时：<https://github.com/addallno/repkg-ng>
- WebView2 共享 Environment 和进程模型：<https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance>
- Windows 公开桌面壁纸接口只覆盖图片与幻灯片：<https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper>
- `DestroyWindow` 不能销毁其他线程创建的窗口：<https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-destroywindow>
- Win32 窗口消息队列和消息循环：<https://learn.microsoft.com/en-us/windows/win32/learnwin32/window-messages>
- Shell `TaskbarCreated` 广播及 DPI 变化触发说明：<https://learn.microsoft.com/en-us/windows/win32/shell/taskbar#taskbar-creation-notification>
- 外部 HWND 的 `IsWindow` 检查存在销毁竞态和句柄复用：<https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindow>
- `EndDeferWindowPos` 可在单个屏幕刷新周期更新多个窗口：<https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enddeferwindowpos>
- `SetParent` 的样式和 DPI awareness 限制：<https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent>
