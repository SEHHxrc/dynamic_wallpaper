# LiveWall.Renderer.Web

## 职责

隔离运行 WebView2 Composition Controller，承载本地 HTML/CSS/JavaScript/Canvas/WebGL2 壁纸，执行权限收敛和 Wallpaper Engine Web API 兼容脚本。

## 对外接口

唯一控制面是 Renderer Protocol；壁纸脚本只可通过经过 Schema 校验的 `postMessage` Bridge 请求授权能力。默认禁止网络、下载、打印、新窗口和生产 DevTools。

## 依赖规则

只引用 Contracts。不得引用 Host、Application、Domain、Infrastructure 或 Importers；不得直接解析任何包；兼容脚本只能模拟白名单 Web API，不能提供命令执行或任意文件读取。

## 目录

| 目录 | 功能 | 边界 |
|---|---|---|
| `WebView` | Environment/Controller 生命周期和虚拟 Host | 默认离线、共享 UDF 策略 |
| `Composition` | DirectComposition 视觉树和输入 | 不决定业务布局 |
| `CompatibilityScripts` | 受控 Wallpaper Engine Web API shim | 白名单 API、版本化 |
| `Protocol` | Wire DTO 与 Web 引擎映射 | Generation/能力校验 |

