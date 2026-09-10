# 模块边界与调用关系

本文把根架构的依赖图落实为工程引用规则。公开 API 是“允许开放的最大表面”，不是要求每个模块立刻实现全部接口。

## 编译期依赖

| 项目 | 允许引用 | 禁止引用 | 公开表面 |
|---|---|---|---|
| `LiveWall.Domain` | BCL | 所有 LiveWall 项目、I/O/Win32/JSON | 领域记录、值对象、枚举、纯规则 |
| `LiveWall.Contracts` | BCL、System.Text.Json | 所有 LiveWall 项目、业务服务 | Wire Envelope、DTO、方法/事件名 |
| `LiveWall.Application` | Domain | Contracts 与所有外层实现 | 用例、端口、进程内模型 |
| `LiveWall.Platform.Windows` | Application、Domain | Host、Infrastructure、Importers、UI、Renderer | Windows 端口实现 |
| `LiveWall.Infrastructure` | Application、Domain、Contracts | Platform.Windows、Host、UI、Renderer | 存储、包、IPC、日志实现 |
| `LiveWall.Importers` | Application、Domain | Contracts、Host、UI、Renderer、Platform.Windows | 导入器、容器读取器、转换器实现 |
| `LiveWall.Host` | Domain、Contracts、Application、Platform.Windows、Infrastructure、Importers | UI、Renderer | 唯一组合根、RPC/协议适配、运行状态 |
| `LiveWall.UI` | Contracts | 其余所有产品项目 | RPC Client 与 WinUI 展示 |
| `LiveWall.Renderer.Video` | Contracts | 其余所有产品项目 | Renderer Pipe Client、libmpv 引擎 |
| `LiveWall.Renderer.Web` | Contracts | 其余所有产品项目 | Renderer Pipe Client、WebView2 引擎 |

新增项目引用必须修改本表；若新增方向改变根依赖图，还必须先提交 ADR。

## 运行时调用

```text
UI --UiRpc Request/Response/Event--> Host
                                         |
Windows callbacks --HostCommand--------> | CommandLoop (唯一写者)
Renderer events ------HostCommand-------> |
                                         v
                                  SessionCoordinator
                                    |     |      |
                                    v     v      v
                                 Desktop Store RendererSession
                                                   |
                                                   v
                                         Video/Web Renderer Process
```

## 模型转换所有权

| 转换 | 所有者 | 规则 |
|---|---|---|
| RPC DTO ↔ Application 输入输出 | `Host/Rpc` | 校验所有字符串枚举、长度、路径意图和权限 |
| Application Renderer 模型 ↔ Renderer DTO | `Host/Orchestration` 或 `Host/Rpc` 内专用 mapper | 必须保留 sessionId、generation、messageId |
| Windows 原生信息 ↔ Domain/Application | `Platform.Windows` | Shell 原生 HWND、Z-order anchor 和 backdrop 只存在于当前 Shell generation 的 Platform 内部 attachment lease；不泄漏 COM/句柄所有权，自有 HWND 操作只在 Window Dispatcher。Application 只接收已验证能力和强类型 Surface binding |
| 外部包 ↔ 规范化内容 | `Importers` | 生成兼容性报告；不直接写正式 library |
| 规范化内容 ↔ library | `Infrastructure/Persistence` | 校验后原子提交 |

## 状态所有权

| 状态 | 唯一权威 | 其他模块行为 |
|---|---|---|
| 活动会话、Surface、Generation | Host CommandLoop | 只投递命令或读取快照 |
| Desktop Surface HWND 生命周期 | Platform.Windows Window Dispatcher | 其他线程只提交异步工作项，不直接 Create/Show/SetParent/Destroy |
| Shell attachment lease 与结构指纹 | Platform.Windows Desktop Adapter | 只在当前 Shell generation 内有效；结构候选、presentation probe 与可渲染 attachment 分阶段，不持久化裸 HWND |
| 壁纸库持久状态 | Infrastructure Repository | Host 通过 Application 端口访问 |
| UI 展示状态 | UI | 可丢弃并从 Host 重建 |
| Renderer 内部播放状态 | 对应 Renderer | 通过事件同步给 Host，Host 决定目标状态 |
| 外部导入临时状态 | Importer 操作上下文 | 成功提交或失败清理，不进入仓库 |

## 架构自动检查要求

后续测试项目应至少验证：

- ProjectReference 方向符合上表；
- Domain/Contracts 不引用其他 LiveWall 程序集；
- Renderer/UI 只引用 Contracts；
- Renderer 会话的 wire 握手与 Application 映射位于 Host；`IRendererProvider.CreateAsync` 只返回已经完成 Hello/Initialize/Initialized 的会话，Platform.Windows 不得处理 Renderer IPC；
- `DllImport` / `LibraryImport` 只出现在允许的原生边界目录；
- Wallpaper Engine 命名空间不出现在 Renderer；
- 跨进程公开 DTO 可被 System.Text.Json 往返序列化。
- `IDesktopHost` 保持 hidden provisional + 批量显式替换语义，且所有自有 HWND 的创建和销毁线程一致。
- Shell 结构验证只能产生 `StructuralCandidate`；未通过真实像素 presentation probe 的候选不得成为生产 attachment 或进入 allowlist。
- Renderer Protocol v1 的 `AttachSurface.windowHandle` 只表示 `HwndChild` binding；Renderer 在自有 child HWND 内使用 DComp/交换链不改变协议。只有跨进程开始传递共享纹理、交换链句柄、fence 或其他非容器 HWND 对象时，才必须升级协议、Schema 与契约测试，且不能重载现有字段。
