# LiveWall.Renderer.Video

## 职责

隔离运行 libmpv/D3D11，播放视频和 GIF，将渲染窗口附着到 Host 提供的 Surface，并通过 Renderer Protocol 报告首帧、状态、遥测和故障。

## 对外接口

唯一控制面是 `LiveWall.Contracts.RendererProtocol`。输入为白名单命令，输出为白名单事件；不开放 libmpv 指针、任意 mpv 命令或任意文件访问 API。

## 依赖规则

只引用 Contracts。不得引用 Host、Application、Domain、Infrastructure 或 Importers；不得直接解析 `.lwpkg/.pkg/.mpkg`，只接受 Host 提供的已验证原生内容根和入口。

## 目录

| 目录 | 功能 | 边界 |
|---|---|---|
| `Mpv` | libmpv 生命周期与属性映射 | 仅内部 SafeHandle/PInvoke |
| `Windowing` | Renderer 子窗口和 D3D11 上下文 | HWND 所有权明确 |
| `Protocol` | Wire DTO 校验与引擎命令映射 | 1 MiB 帧上限、Generation 校验 |

