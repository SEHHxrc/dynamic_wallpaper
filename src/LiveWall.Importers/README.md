# LiveWall.Importers

## 职责

把外部内容转换为经验证的原生内容。固定流水线为 `Probe → Analyze → Extract → Convert → Validate → Commit`。任何外部格式都不得穿透到 Renderer 运行时。

## 对外接口

实现 `IWallpaperImporter`、`IPackageContainerReader` 和 `IContentConverter`。`ProbeAsync` 仅读取少量头与元数据；`ImportAsync` 只在随机隔离目录工作，并返回明确 `CompatibilityReport`。

## 依赖规则

只引用 Application 和 Domain。不得引用 Host、UI、Renderer 或 Platform.Windows；不得执行包中 EXE/DLL/脚本；不得静默丢失特性。

## 目录

| 目录 | 功能 | 输出 |
|---|---|---|
| `Native` | 原生目录与 `.lwpkg` 导入 | Native 兼容等级 |
| `WallpaperEngine/ProjectDirectory` | `project.json` 分类 | 视频/Web 导入计划 |
| `WallpaperEngine/Pkg` | `.pkg` 容器索引与安全提取 | PackageIndex/素材，不直接运行 |
| `WallpaperEngine/Mpkg` | `.mpkg` 容器索引与移动素材提取 | 视频、图片或降级结果 |
| `WallpaperEngine/Tex` | TEX 素材读取和转换 | 标准图片资产 |
| `WallpaperEngine/SceneAnalysis` | Scene 特性识别 | 兼容等级与损失报告 |
| `WallpaperEngine/WebCompat` | Web API 兼容脚本注入计划 | 规范化 Web 工程 |
| `WallpaperEngine/Delegate` | 可选外部委托策略 | 用户明确选择后才启用 |

