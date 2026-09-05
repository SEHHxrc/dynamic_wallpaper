# LiveWall.Domain

## 职责

表达与运行技术无关的业务概念和不变量：壁纸定义、显示器分配、布局、播放状态、运行会话、导入兼容等级。该程序集是业务语义的最内层。

## 公开接口

- `Wallpapers`：`WallpaperDefinition`、`WallpaperMetadata`、`WallpaperCapabilities` 和壁纸标识。
- `Displays` / `Layouts`：稳定显示器标识、显示器拓扑、布局和适配模式。
- `Playback` / `Sessions`：播放状态、策略、决策和带 `Generation` 的会话。
- `Importing` / `Compatibility`：来源和兼容等级等纯值对象。

公开成员仅接受领域类型、BCL 值类型和只读集合；不得出现 JSON、文件路径操作、Stream、HWND、IPC DTO 或 UI 类型。

## 依赖规则

不引用任何其他 `LiveWall.*` 项目和第三方运行时框架。领域对象不得执行 I/O，不发布进程间消息，也不持有原生资源。

## 目录

| 目录 | 功能 | 对外边界 |
|---|---|---|
| `Wallpapers` | 壁纸定义和能力 | 不包含实际文件访问 |
| `Displays` | 显示器稳定身份与几何信息 | 不包含 DisplayConfig 调用 |
| `Layouts` | 显示布局与适配语义 | 只做纯计算输入输出 |
| `Playback` | 播放状态与策略值 | 不直接控制 Renderer |
| `Sessions` | 活动会话及 Generation | 旧代结果不得覆盖新代 |
| `Importing` | 导入后的领域结果 | 不表示临时解包状态 |
| `Compatibility` | 兼容等级和损失描述 | 损失必须显式表达 |

