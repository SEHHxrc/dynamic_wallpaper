# LiveWall.Renderer.Video

## 职责

隔离运行 libmpv/D3D11，将视频渲染窗口附着到 Host 提供的 Surface，通过 Renderer Protocol 报告首帧、播放状态和故障。GIF/编码矩阵和连续遥测按后续范围验收，不把职责目标等同于当前已支持能力。

## 对外接口

唯一控制面是 `LiveWall.Contracts.RendererProtocol`。输入为白名单命令，输出为白名单事件；不开放 libmpv 指针、任意 mpv 命令或任意文件访问 API。

## 依赖规则

只引用 Contracts。不得引用 Host、Application、Domain、Infrastructure 或 Importers；不得直接解析 `.lwpkg/.pkg/.mpkg`，只接受 Host 提供的已验证原生内容根和入口。

## 当前状态

C0 已实现私有 libmpv client 后端，并由独立 `tools/VideoPresentationProbe` 链接相同源码做开发验证。2026-10-28 cover 复测获用户确认全程无黑边、其余正常，机器几何/播放及自有清理通过，**物理单屏呈现可行性切片关闭**。依赖仅位于项目 `.cache`，固定来源/版本/归档和 DLL 哈希；本地生成原始视频，不含第三方媒体。C0 后端事件和几何仍不是运行时像素证明，未发送 `FirstFramePresented`；人工呈现记录只证明当前 build/单屏/测试内容的代表路径，不证明完整编码、硬解或 HDR/色准矩阵。

C1.0 的事务核与提交门禁 32 项通过。用户已明确接受 ADR-011，正式后端采用 `vo=libmpv + D3D11 Render API`；根架构同步，C0 路径不作为静默回退。C1.1 已将正式 Program、一次性 Hello、Initialize、单接收泵/有界命令队列、Envelope/路径门禁与阶段超时接入；创建自有 child/DComp target/交换链，真实提交连接首帧票据，支持暂停时新 generation 重附着和同 binding 的 Bounds 适配。

新增 37 项 Wire 会话测试通过；真实正式可执行文件在隐藏容器中完成加载、首帧、Pause、Bounds、新 generation 重附着、Resume、正常 Shutdown，以及提前 Shutdown、断 Pipe、非法路径代表场景，自有窗口均清理。这是 C1.1 当时的非可见正式会话证据，随后单屏人工与有限收口另见 C1.2/C1.3。默认产品 Host Provider/仓库仍未启用 Video，Throttle/完整属性/遥测、编码/硬解/资源矩阵独立保留。Shell WorkerW 残留生产门禁未关闭，发行许可证与编解码器审计待完成。

Program 仅接受 Host 提供的两个进程参数（Pipe、SessionId），认证令牌从环境读取后清除；开发 DLL 仅从显式进程环境 `LIVEWALL_VIDEO_LIBMPV_PATH` 或程序内 `native/libmpv-2.dll` 加载，并严格校验固定哈希，不搜全局 PATH。固定依赖和头文件见 tools 两份 libmpv lock，未成为发行构建决定。未给正式 Host 增加实验 Raised 参数。

2026-11-03 C1.2 修复后的显式主屏 10 秒产品候选通过正式首帧、一次 Replace、ShutdownCompleted、实际 Renderer ExitCode=0 与完整自有清理；用户确认覆盖、连续运动、图标/任务栏/窗口层级及结束恢复全部正常。单屏呈现与正常退休代表项通过，旧 ExitCode=-1 失败原样保留。该轮 Shell 新增 WorkerW 为 CleanupIncomplete、整轮退出码 6；单项不关闭 Stage B，后续 C1 有限收口见 C1.3，不扩大为双屏或环境/性能矩阵通过。

## 目录

2026-11-03 C1.3 已补实际控制读回、Shutdown 抢占迟到控制完成、Host 活动期失败通知和隐藏诊断。随后获准完成一次单屏 20 秒分阶段验收：播放→暂停→同 Renderer 新容器重附着并保持暂停→恢复→正常退出，用户确认全部符合预期；41 个暂停样本时间增量 0、20 个恢复样本、实际 ExitCode=0 和清理通过。本轮 WorkerW 未新增，整体退出码 0，不解释为既有 mutation 已恢复。**C1 按正式协议、最小控制/失败收口和单屏代表验收的有限范围关闭**，Debug/Release 各 573/573；C2 内容链、双屏/真实 Video Explorer/长期资源与完整编码矩阵、Platform 生产门禁独立保留。诊断日志显式启用、读取环境后清除、最多 512 条，不是生产配置或协议遥测。

| 命令/能力 | 当前支持 |
|---|---|
| Initialize / AttachSurface / LoadWallpaper | 正式会话已接入；Load 仅接受已验证本地视频及空 properties |
| Play / Resume / Pause / Suspend | 实际执行并读回暂停状态，成功后才发送 PlaybackStateChanged；停止抢占后禁止迟到成功 |
| SetBounds / 新 generation Attach | 当前 binding 的尺寸确认；新 child/target 重新提交有效帧，保留暂停和设置 |
| SetFit | Cover、Contain、Stretch、Center；映射及后端属性读回通过，完整可见画幅矩阵待补 |
| SetVolume / Mute | 0–1 有限音量与静音布尔值，实际 libmpv 状态读回；当前无音轨 fixture 不证明听感 |
| Shutdown | 清理后回执，Host 另核验实际进程退出；故障关闭不伪造正常回执 |
| Throttle / SetProperties / SetAudioFrame / CapturePreview | 未实现，明确 FatalError/message_unsupported 并清理，不返回成功 |
| 连续遥测、完整属性、编码/硬解/性能/发行 | 尚未承诺，诊断观测日志不等于这些能力 |

| 目录 | 功能 | 边界 |
|---|---|---|
| `Mpv` | libmpv 生命周期与属性映射 | 仅内部 SafeHandle/PInvoke |
| `Windowing` | Renderer 子窗口和 D3D11 上下文 | HWND 所有权明确 |
| `Protocol` | Wire DTO 校验与引擎命令映射 | 1 MiB 帧上限、Generation 校验 |
| `NativeMethods` | 自有 child/消息泵、Pipe server PID 与进程退出顺序 | 单文件白名单，无 Shell API |

