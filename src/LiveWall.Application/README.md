# LiveWall.Application

## 职责

编排用例并定义由外层实现的端口。这里描述“系统要做什么”，但不知道 Win32、磁盘、Named Pipe、libmpv 或 WebView2 如何完成。

## 公开接口

- `Abstractions`：通用时钟、标识、事务/原子提交等端口。
- `Configuration`：播放策略与显示器分配的恢复/持久化端口。
- `Library`：`IWallpaperRepository`。
- `Importing`：`IWallpaperImporter`、`IPackageContainerReader`、`IContentConverter`。
- `Layouts`：`ILayoutPlanner`。
- `Playback` / `Policies`：`IPlaybackPolicyEvaluator`。
- `Sessions`：`IRendererProvider`、`IRendererSession`、`IDesktopHost`、`IDisplayTopologySource`、`IProcessSupervisor`。

所有异步 I/O 接口必须接收 `CancellationToken`。查询返回只读集合；失败使用明确结果/异常映射，不返回含义不明的 `null`，唯一例外是按 ID 未找到。

## 依赖规则

只引用 `LiveWall.Domain`。不得引用 Contracts、Platform.Windows、Infrastructure、Importers、UI、Host 或 Renderer。协议 DTO 到应用模型的映射由 Host/RPC/Protocol 适配层完成。

## 目录

| 目录 | 功能 | 禁止事项 |
|---|---|---|
| `Abstractions` | 无技术倾向的基础端口 | 不放工具类垃圾桶 |
| `Configuration` | 可恢复 Host 设置端口 | 不绑定 JSON 或真实路径 |
| `Library` | 壁纸库用例和持久化端口 | 不读写真实目录 |
| `Importing` | Probe/Import/Extract/Convert 流程端口 | 不绑定具体 pkg 实现 |
| `Layouts` | 布局规划用例 | 不枚举 Windows 显示器 |
| `Playback` | 播放控制用例 | 不直接发送 Pipe |
| `Policies` | 策略纯函数 | 不产生 I/O 副作用 |
| `Sessions` | Renderer、Surface、进程生命周期端口 | 不持有具体引擎类型 |
