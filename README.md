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

当前已实现专用 STA Window Dispatcher、隐藏 provisional Surface、批量 `ReplaceSurfacesAsync`、以 `TaskbarCreated` 为主信号并按 350 ms 防抖的 Shell 失效检测，以及由 Host 驱动的 Surface 重建式恢复。`DesktopHostDiagnostics` 已提供 `--shell-topology`、Legacy `--color-block`，以及隔离的 Raised/Desktop 产品候选诊断；默认运行仍只读。双屏基础、长驻、process-cold 和连续替换已经通过，但完整 Explorer、故障注入、DPI 范围、热插拔和显示拓扑重规划矩阵尚未完成。

Legacy WorkerW 与 Raised Desktop 的生产 allowlist 均为空，因此生产 Selector 会失败关闭，完整 build `26200.9168` 仍不得标记为已支持。Raised 的单屏 DirectComposition、独立 Renderer-child 协议 v1 `HwndChild` binding、正常/取消/崩溃清理，以及同一 Renderer 跨 Explorer generation 重附着已经通过诊断和人工验收；功能恢复成立，但显式终止 Explorer 时的短暂黑屏不属于产品级无缝恢复承诺。正式 Renderer v1 会话和显式产品候选链已经接线并通过自动验证；主屏单 Surface 的短时物理单屏候选已经通过。P1.6 的双屏 90 秒长驻、10/10 次 process-cold 以及同一 Host 20 轮 Apply/Replace/retirement 均已在 `WinSta0\Default` 自动与人工通过。此前样本继续保留为历史 `UnclassifiedTimeout`，不能事后归因；P1.6 通过不替代 Explorer、热插拔和异常矩阵。

双屏候选代码现已支持显式 `--target-display primary|all|<display-id>`，并为每个显示器返回独立 Session/Surface/retirement 结果。`all` 必须等待全部 FirstFrame 后执行一次批量 Replace；任一屏失败会整批回收。`26200.9168 / WinSta0\Default` 已在 200% 主屏与 150% 负坐标外接屏完成真实双屏候选、90 秒长驻、10/10 次 process-cold 和同一 Host 20 轮连续替换验收；两屏同步完整显示并恢复，图标、任务栏和普通程序层级正确，40 个 Renderer 全部优雅退休且无自有 HWND/新增 WorkerW。该证据关闭 P1.6，但不替代局部故障、Explorer、热插拔、主屏切换和更完整 DPI 矩阵。

同 Host 连续候选入口也已支持 `--target-display all`：每代双屏首帧齐备后原子 Replace，下一代提交后等待上一代两个 Renderer 全部退休，并按 generation 在橙色/黄色间交替。真实 20 轮已完成：两屏全程同步交替、无黑屏或覆盖并正常恢复，40 个 Renderer 全部优雅退休，无自有 HWND 或新增 WorkerW。

Platform.Windows 已落地内部 `DesktopAttachmentLease` 和 Raised Surface Factory 接线：Shell parent、DefView anchor、WorkerW backdrop、结构指纹和 generation 不再泄漏到 Application 或 Renderer；Application 只收到无 HWND 的能力描述，Renderer 仍只收到 LiveWall 自有容器 HWND。跨 generation 成功替换不会显隐、换父级或复用旧 HWND。恢复失败也不再把 Renderer 附回 previous Surface：Host 会原子失效活动会话、保留 Assignment、放弃旧及 provisional generation 记录，并以全新 Renderer/Surface 限次退避重建；预算耗尽后失败关闭，用户显式 Apply 可开启新的恢复尝试。Apply deadline 已与活动 Session 生命周期解耦：提交终态释放 deadline lease，正常替换/退出采用独立 Renderer retirement budget，并分别报告 graceful Shutdown 与自有资源清理。Raised 生产 Adapter 在精确 build/UBR allowlist、DPI、热插拔、多屏、完整恢复异常矩阵及 Shell mutation recovery 完成前仍保持禁用；Stage B 状态继续为 `In progress / Not accepted`。详细区分见 [`docs/implementation-status.md`](docs/implementation-status.md) 和 [ADR-008](docs/adr/008-desktop-attachment-capability-and-presentation.md)。

## 边界检查

安装 .NET 10 SDK 后，提交前至少运行：

```powershell
./tools/check.ps1 -Configuration Debug
```

该命令会检查 10 个产品项目的依赖边界，并运行当前 5 个测试工程。提交候选还应执行一次 `Release` 配置。
