# Wallpaper Engine 兼容边界

## 结论

`.pkg/.mpkg` 仅作为不可信导入源，不是 LiveWall 运行格式。运行时 Renderer 永远只接收规范化目录和 `WallpaperDefinition`。

## 流程

`Probe → Analyze → Extract → Convert → Validate → Commit`

| 输入 | 首版行为 | 最高承诺 |
|---|---|---|
| 视频工程目录 | 读取 project.json，复制原媒体 | Full |
| Web 工程目录 | 复制项目并注入白名单 shim | Full/Partial |
| `scene.pkg` | 安全提取、特性分析、预览降级 | Partial/PreviewOnly |
| `.mpkg` 视频/图片 | 安全提取并规范化 | Full |
| `.mpkg` Scene | 简单素材降级；否则预览/拒绝 | Partial/PreviewOnly |
| Application/EXE | 拒绝 | Unsupported |

禁止把第三方反编译结果伪装为官方稳定规范。解析器必须基于最小样本、格式探测、边界校验和 fuzz 测试演进；失败输出明确兼容性报告。

