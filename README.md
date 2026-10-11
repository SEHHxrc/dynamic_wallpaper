# LiveWall

LiveWall 是面向 Windows 10 1903+ / Windows 11 的轻量动态壁纸程序。本仓库已建立领域、应用、跨进程契约和进程边界，并已实现一组可自动验证的 Foundation 与 Windows Desktop Host 机制；这些代码尚未接入产品入口，也不表示 Stage B 已通过真实桌面兼容验收。

## 权威文档

- [`ARCHITECTURE.md`](ARCHITECTURE.md)：最高级架构约束；代码与其冲突时视为代码缺陷。
- [`docs/README.md`](docs/README.md)：设计文档索引及文档优先级。
- [`docs/windows-desktop-host.md`](docs/windows-desktop-host.md)：Stage B 的窗口线程、Surface 交换、Explorer 恢复规范与验收边界。
- [`docs/implementation-status.md`](docs/implementation-status.md)：实现、自动验证与真实桌面验收的统一状态台账。
- 各 `src/LiveWall.*/README.md`：模块局部契约、允许依赖和禁止事项。
- 各源码目录中的 `README.md`：该目录的职责、输入输出及扩展点。

## 工程入口

```text
LiveWall.sln               解决方案入口
src/                       产品代码
tests/                     Domain、Application、Package 与 Renderer Contract 测试
packages/                  公开格式 Schema、样例及 Web Runtime
tools/                     诊断与协议开发工具边界
packaging/                 MSIX、安装器及第三方声明
docs/adr/                  架构决策记录
```

## 开发前提

- 项目级 .NET SDK `10.0.100` 或可兼容的更新 Feature Band；
- Windows 10 1903+，首发 RID 为 `win-x64`；
- 任何新项目引用必须符合 [`docs/module-boundaries.md`](docs/module-boundaries.md)；
- 任何公开消息变更必须先更新对应 JSON Schema 与契约测试；
- 架构例外必须先新增 ADR，并同步更新根架构文档。

首次开发时运行：

```powershell
./tools/bootstrap-dotnet.ps1
./tools/dotnet.ps1 --info
```

SDK 安装在本仓库 `.dotnet/`，NuGet 包安装在 `.nuget/packages/`，CLI 状态安装在 `.dotnet-cli-home/`。这些目录均不会影响其他项目。需要在当前 PowerShell 会话直接使用 `dotnet` 时，可运行 `. ./tools/use-dotnet.ps1`。

Bootstrap 还会生成被 Git 忽略的 `.vscode/settings.json`，让 C# 与 C# Dev Kit 使用仓库内 SDK，并补齐当前 C# Dev Kit 所需的 .NET/ASP.NET Core 10.0.5 运行时。首次运行或仓库移动后，请在 VS Code 执行 **Developer: Reload Window**；若出现 `Project runtime paths are overridden` 确认框，请选择 **Use Overrides**。

## 当前边界

当前已实现专用 STA Window Dispatcher、隐藏 provisional Surface、批量 `ReplaceSurfacesAsync`、以 `TaskbarCreated` 为主信号并按 350 ms 防抖的 Shell 失效检测，以及由 Host 驱动的 Surface 重建式恢复。`DesktopHostDiagnostics` 已提供 `--shell-topology`、Legacy `--color-block`，以及隔离的 Raised/Desktop 产品候选诊断；默认运行仍只读。双屏基础、长驻、process-cold、连续替换、正式 Renderer 局部失败关闭、Explorer 正向重附着和局部失败整批重建已经通过。P1.8 显示拓扑重规划已接入 Host 单写者，并具备 revision 终态、失败回退和 stale 准备取消；当前真实断开/重连、`2→1→2→1→2` 快速 revision 抢占、主屏切换、scale-only 及 bounds+scale 已通过。旋转后的最终几何、黄色原子替换、外接屏保留和系统层级均正确；活动 Surface 快速适配已从约 0.74 秒缩短到约 20.6 ms。P1.8-TG0 的有限预置 Guard 重复矩阵证明 DWM 会自动揭示预置内容，但横→纵样本也确认首帧可能只有部分洋红、其余仍黑，无法保证整个预测区域原子呈现。因此 TG0 按 ADR-009 关闭为“部分可行、严格目标失败”，不进入 TG1、不生产化 Guard；旋转继续保留 `TransientCoverageGap` 已知限制。

P1.8.2 的自动安全边界已接线：运行中的非空拓扑只有在相隔 350 ms 的两次连续空采样后才发布零显示器 Suspend，采样失败或任意非空快照会取消空候选；进程启动即为空则形成独立的初始拓扑。Span 继续使用一个物理像素 Surface 和 `scaleFactor=1`，若其矩形包围区与任何非成员显示器相交则以强类型布局错误失败关闭，并在拓扑重规划失败时保留既有活动 Session。

P1.9.1 已把 `TaskbarCreated` 的正式事件链接入真实 `WindowsHostRuntime` 并完成双屏实机验收。无 Shell 变化的 10 次 burst 只触发一次防抖验证，Host generation、Session 身份、Replace 与 retirement 均保持不变；真实 Explorer 重启则由信号/后备探针确认一次 Shell generation 变化，只形成一个 recovery operation 和一次批量 Replace。本次真实恢复按安全策略得到 `Succeeded + Rebuilt`，两屏同步经历 Shell 黑屏、恢复后重新显示橙色，未出现半屏提交、重复恢复、层级覆盖或自有 HWND 残留。P1.9.2–P1.9.5 的系统会话与并发/退出矩阵仍未完成。

P1.9.2 自动门禁及物理单屏代表场景已通过：基线启动/退出、锁解 3/3、用户暂停锁解 1/1、S0 Modern Standby 睡眠恢复 2/2；首个未完成睡眠样本继续保留，双屏系统恢复独立待补。P1.9.3.0 已实现当前 Session 事件过滤、断开期间的呈现提交门禁、最新环境延后处理、revision 关联的重连验证和可等待终态；P1.9.3.1 显式诊断入口已完成。首版不支持 RDP 动态壁纸呈现，RDP 实机为 `OutOfScope / WaivedForV1`，不再阻塞开发；通用会话保护保留，本地快速用户切换实机为 `NotValidated`。已进入 P1.9.4 的 Shell/display 并发恢复自动收口，完整仲裁和实机矩阵仍待完成。最新验证结果以统一台账为准；协议 v1、DTO/Schema、生产 allowlist 和 Stage B 状态不变。

`26200.9457 / WinSta0\Default` 的双屏 refresh-only 实机样本已经通过：内置主屏从 120 Hz 改为 60 Hz 时 DisplayId、bounds、scale 和虚拟原点均不变，Host 产生 `NoChange / generation=none`，两个活动 Session 身份保持、零 retirement、零 Surface Replace，测试色和桌面层级持续正确，最终清理完整。

同一环境的 position-only 样本也已通过：左侧外接屏从 `(-3840,14)` 下移到 `(-3840,144)`，虚拟原点保持 `(-3840,0)`；Host 只快速适配并替换外接屏一个 Session，主屏持续保持，最终层级与资源清理正确。

随后改变外接屏纵向位置并使虚拟原点从 `(-3840,0)` 变为 `(-3840,-369)` 的样本也已通过：Host 将两个 PerDisplay Session 作为同一批次整批重建，两个新首帧齐备后只执行一次 Replace，最终 `Succeeded`、两个旧 Session 均退休且资源清理完整。人工确认两屏动作同步并同步变黄；主屏下部短暂露黑登记为已知 `TransientCoverageGap`，不属于半屏提交或层级错误。

Span 代表性 position 样本也已通过：双屏由一个 Session、一个 Renderer 和一个物理像素 union Surface 覆盖；外接屏 `Y=-210→-91` 后旧橙色 Span 在系统刷新期间持续保持，新首帧完成后两屏同步切换为黄色。Host 只执行一次 Replace，旧 Span Session 正常退休，最终清理完整。首次复测前发现的 Host 自有 Span 误判为竞争窗口问题，已通过精确 HWND 生命周期注册修复，未放宽第三方竞争检测。

同一 Span 的 scale-only 样本也已通过：外接屏仅从 150% 调整到 175%，bounds、位置和虚拟原点不变；旧橙色 Span 在外接屏 DPI 刷新期间连续保持，新 Span 首帧完成后两屏同步变黄。结果仍为一个活动 Session、一次 Replace 和完整清理，协议继续使用物理像素 `scaleFactor=1`。

Span 成员 `2→1→2` 实机往返也已通过：断开外接屏时双屏 Span 直接原子替换为单屏黄色 Span，无黑屏或露底；重新接入时单屏黄色持续到外接屏可显示，新双屏 Span 随后同步切换为橙色。每个 revision 都只有一个活动 Session、一次 Replace 和一个旧 Session 退休；重连期间的暂态 Shell 结构变化由既有有界退避吸收，最终清理完整。

同一 Host 的 `PerDisplay→Span` 也已通过：第一代两个独立 Session/Surface 同步显示橙色，第二代单 Span Session/Surface 首帧完成后两屏同步切换为黄色；两个旧 Renderer 与最终 Span Renderer 均优雅退休，无黑屏、静态壁纸、半屏、覆盖、HWND 或 WorkerW 残留。P1.8 按既定边界关闭；pure bounds-only 与物理零显示器在当前硬件上保留 `EnvironmentInconclusive`，自动安全门禁已通过，旋转/扩张保留 ADR-009 的 `TransientCoverageGap`。

Legacy WorkerW 与 Raised Desktop 的生产 allowlist 均为空，因此生产 Selector 会失败关闭，已采样的完整 build `26200.9168` 与 `26200.9445` 均不得标记为已支持。Raised 的单屏 DirectComposition、独立 Renderer-child 协议 v1 `HwndChild` binding、正常/取消/崩溃清理，以及同一 Renderer 跨 Explorer generation 重附着已经通过诊断和人工验收；功能恢复成立，但显式终止 Explorer 时的短暂黑屏不属于产品级无缝恢复承诺。正式 Renderer v1 会话和显式产品候选链已经接线并通过自动验证；主屏单 Surface 的短时物理单屏候选已经通过。P1.6 的双屏 90 秒长驻、10/10 次 process-cold 以及同一 Host 20 轮 Apply/Replace/retirement 均已在 `WinSta0\Default` 自动与人工通过。此前样本继续保留为历史 `UnclassifiedTimeout`，不能事后归因；这些结果不替代尚未执行的完整 DPI、会话切换和 Shell mutation 矩阵。

双屏候选代码现已支持显式 `--target-display primary|all|<display-id>`，并为每个显示器返回独立 Session/Surface/retirement 结果。`all` 必须等待全部 FirstFrame 后执行一次批量 Replace；任一屏失败会整批回收。`26200.9168 / WinSta0\Default` 已在 200% 主屏与 150% 负坐标外接屏完成真实双屏候选、90 秒长驻、10/10 次 process-cold 和同一 Host 20 轮连续替换验收；两屏同步完整显示并恢复，图标、任务栏和普通程序层级正确，40 个 Renderer 全部优雅退休且无自有 HWND/新增 WorkerW。P1.7-A/B/C/D 已通过自动与代表性人工验收；`26200.9445 / WinSta0\Default` 又完成同一双屏组合的断开/重连和主屏切换原子重规划。更完整 DPI/bounds 矩阵仍未关闭。

同 Host 连续候选入口也已支持 `--target-display all`：每代双屏首帧齐备后原子 Replace，下一代提交后等待上一代两个 Renderer 全部退休，并按 generation 在橙色/黄色间交替。真实 20 轮已完成：两屏全程同步交替、无黑屏或覆盖并正常恢复，40 个 Renderer 全部优雅退休，无自有 HWND 或新增 WorkerW。

P1.9.0 已将占位 `Program` 替换为真实 Host 组合根和生命周期运行时：同步外层持有单实例 Mutex，启动恢复等待明确 Apply 终态，进入 Stopping 后拒绝外部变更，迟到完成只清理不提交，关停等待 Renderer retirement 后持久化配置。P1.9.1 又接通正式 `TaskbarCreated` 验证/转发链路及诊断组合根；Debug/Release 当前为 224/224，Release `--validate-startup` 与两项事件链诊断均返回 0。生产壁纸仓库和 Video/Web Renderer 产品入口仍未实现，因此当前只完成生命周期、事件恢复门禁与失败关闭，不宣称正式内容恢复。

Platform.Windows 已落地内部 `DesktopAttachmentLease` 和 Raised Surface Factory 接线：Shell parent、DefView anchor、WorkerW backdrop、结构指纹和 generation 不再泄漏到 Application 或 Renderer；Application 只收到无 HWND 的能力描述，Renderer 仍只收到 LiveWall 自有容器 HWND。跨 generation 成功替换不会显隐、换父级或复用旧 HWND。恢复失败也不再把 Renderer 附回 previous Surface：Host 会原子失效活动会话、保留 Assignment、放弃旧及 provisional generation 记录，并以全新 Renderer/Surface 限次退避重建；预算耗尽后失败关闭，用户显式 Apply 可开启新的恢复尝试。Apply deadline 已与活动 Session 生命周期解耦：提交终态释放 deadline lease，正常替换/退出采用独立 Renderer retirement budget，并分别报告 graceful Shutdown 与自有资源清理。Raised 生产 Adapter 在精确 build/UBR allowlist、完整 DPI/拓扑/会话矩阵及 Shell mutation recovery 完成前仍保持禁用；Stage B 状态继续为 `In progress / Not accepted`。详细区分见 [`docs/implementation-status.md`](docs/implementation-status.md) 和 [ADR-008](docs/adr/008-desktop-attachment-capability-and-presentation.md)。

## 边界检查

安装 .NET 10 SDK 后，提交前至少运行：

```powershell
./tools/check.ps1 -Configuration Debug
```

该命令会检查 10 个产品项目的依赖边界，并运行当前 5 个测试工程。提交候选还应执行一次 `Release` 配置。
