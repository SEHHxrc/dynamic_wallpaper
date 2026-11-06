# 尚未冻结的决策

2026-10-24 最新边界：**P1.9.5 按实现台账 E 节有限条件关闭。** 最后活动 Renderer 单屏真实注销重测获得 ShutdownCompleted、Renderer ExitCode=0、Surface=0，双回执与配置保留通过，用户确认注销前正常、登录后原壁纸。首轮缺少回执的失败原样保留，修正后的成功不倒推其唯一根因。最近一次 Debug/Release 428/428、合成证据 25/25、受控 Host 5/5、无 Surface Renderer 2/2 均通过。不新增重复注销或文件自证 Main/实际 Host 退出码的门槛，不泛化为真实关机/重启、双屏退出或登录自启动已验收。P1.9.2 双屏系统恢复与 P1.9.4 实机延期义务、生产内容入口和 Shell mutation 决策仍保留，Stage B 与空生产 allowlist 不变。

以下事项不影响当前模块边界，但在对应实现进入主分支前必须明确。开发者不得把临时选择写成事实标准。

P1.10.0 已由 ADR-010 冻结“自有清理不等于 Shell 恢复、无安全逆操作则继续生产禁用”的策略；这不是 mutation 生产验收通过。C0 历史黑边与 WorkerW 残留保持原记录。ADR-011 已接受正式 vo=libmpv + D3D11 Render API 路线，C1.1 Program/Wire/提交已接线。2026-11-03 C1.2 修复后的获准主屏 10 秒复测获得正式首帧、一次 Replace、ShutdownCompleted、实际 ExitCode=0 和完整自有清理，用户确认覆盖、运动、层级与恢复全部正常；单屏呈现与正常退休代表项通过。Shell 新增 WorkerW 仍为 CleanupIncomplete，整轮退出码 6；旧 ExitCode=-1/CandidateFailed 不追改。最近一次 Debug/Release 各 559/559，默认生产内容链未启用；完整环境、编码、硬解、HDR/色准、发行审计及生产门禁独立保留，C1 整体仍进行中。

C1.2 的正式产品候选单屏呈现与正常退休代表项已按用户确认关闭。C1.3 的控制/最小失败门禁及随后获准的单屏 20 秒代表验收也已通过，**C1 按约定有限范围关闭**，Debug/Release 各 573/573。实际暂停时间增量 0，同 Renderer 新容器重附着保持暂停后恢复播放，正常 ExitCode=0、自有清理完整，用户确认全部符合预期。本轮 WorkerW 1→1、未新增，整体退出码 0；不追改此前 mutation 的 CleanupIncomplete。首帧超时过滤器不冒充 GPU 挂死，未支持的 Throttle/属性/遥测不记成已实现。下一步 C2 最小持久库与默认 Provider 待启动；双屏/真实 Video Explorer/长期资源、发行和生产附着仍独立排期。

首版支持范围已于 2026-10-11 调整：RDP 动态壁纸呈现为 `OutOfScope`，RDP 实机矩阵为 `WaivedForV1`，不得再作为下表系统会话矩阵的首版阻塞项，也不得写成测试通过。通用会话状态机、远程会话检测/暂停和失败关闭保留；本地快速用户切换为 `NotValidated`，有现成安全环境时补测。P1.9.2 单屏代表场景已通过，双屏系统恢复独立待补；P1.9.3.0 代码/自动门禁和 P1.9.3.1 诊断入口已完成。P1.9.4 开发切片关闭，真实并发/双屏实机为 `Deferred / Pending hardware`，不是 Waived；P1.9.5 代码、自动/受控进程及约定的单屏真实退出代表项已关闭。生产内容入口、尚未完成的系统/并发验收和 Shell mutation 策略仍为 Stage B 接受门禁，两类 allowlist 继续为空。

2026-11-06：C2.0 持久库和默认 Video Provider 已接线，只有自动验证。默认 Host 能否真实恢复内容仍取决于生产 allowlist 决策，C2 不代为决定。

| 决策 | 最迟阶段 | 完成条件 |
|---|---|---|
| 开源/商业许可证 | 首次对外发布前 | 根目录加入正式 `LICENSE`，并与依赖许可证兼容 |
| Windows App SDK 精确版本 | UI 实现开始前 | 选择当时 Stable，记录最低 OS、部署方式和回退 |
| WebView2 SDK 精确版本 | Web Renderer 开始前 | 选择 Evergreen 兼容版本并完成生命周期验证 |
| libmpv/FFmpeg 发行构建 | Video Renderer 发布前 | C0 仅选定开发用 shinchiro 20261021 x64 / e471496c56（实际 mpv v0.41.0-1227、FFmpeg N-127893），项目内固定归档/DLL 哈希，见 `tools/libmpv-development.lock.json`。不是发行决定；仍须审计实际 mpv/FFmpeg 编译配置、编解码能力、源码及全部第三方许可/声明，不将 client.h 的 ISC 当作 core 许可 |
| Video Renderer 桌面与产品验收 | C1/C2 接受前 | ADR-011 路线及 C1.1 自动/隐藏目标通过；C1.2 修复后正式产品候选的单屏呈现与正常退休代表项通过，Shell mutation 及后续环境门禁保留，再接默认生产 Provider/仓库。首帧是目标提交，不是物理扫描承诺；暂停可重绘当前帧，新 binding 不复用旧结果。性能/硬解独立测量，协议仍为 v1 HwndChild |
| JSON 日志组件与轮转参数 | Host 日志实现前 | 完成依赖审计、磁盘上限和脱敏测试 |
| `CapturePreview` 返回事件 | 命令实现前 | 协议 minor 升级，新增强类型结果事件与 Schema |
| Raised Desktop / Legacy WorkerW build allowlist | 启用对应适配器前（P1 / Stage B 关键路径） | 在与 Explorer 相同的交互式 desktop 中记录完整 build revision/UBR、窗口层级指纹、attachment presentation 和 Renderer binding，运行时重新验证结构与呈现，并通过正式 Renderer v1 会话及实际产品候选全链、DPI/拓扑、异常退出、系统会话与 Shell mutation 矩阵；不得只使用 `CurrentBuildNumber`。Legacy 在现有参数下无合规候选；Raised 的单/双屏 DComp、Renderer-child、P1.6、P1.7 和 P1.8 已按既定实机/条件性边界关闭，包括 Span 代表矩阵。pure bounds-only 与物理零显示器明确为 `EnvironmentInconclusive`，未伪写实机通过；P1.9.0、P1.9.1 及 P1.9.5 已按各自边界关闭，P1.9.2 单屏代表场景通过但双屏待补。生产内容仓库/Renderer、本地用户切换未验证、P1.9.4 真实并发及双屏延期与 Shell mutation 尚未闭环；RDP 首版范围免除不算测试通过。因此两类生产 allowlist 均保持为空 |
