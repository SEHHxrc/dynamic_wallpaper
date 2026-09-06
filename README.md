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

当前已实现专用 STA Window Dispatcher、隐藏 provisional Surface、批量 `ReplaceSurfacesAsync`、以 `TaskbarCreated` 为主信号并按 350 ms 防抖的 Shell 失效检测，以及由 Host 驱动的 Surface 重建式恢复。`DesktopHostDiagnostics` 已提供 `--shell-topology`、Legacy `--color-block`，以及隔离的 `--raised-desktop-probe`/`--raised-desktop-color-block`；默认运行仍只读。上述机制已有自动化测试，但尚未通过支持矩阵中的完整 Explorer、DPI、热插拔和多屏验收。

`RaisedDesktopAdapter` 仍是主动失败的占位实现；Legacy WorkerW 与 Raised Desktop 的生产 allowlist 均为空。更准确的当前结论是：完整 build `26200.9168` 在现有 Legacy 参数下不受支持；隔离 Raised Desktop 探针已证明 `0x0D/0x01` 可生成结构合规的 Progman 子 WorkerW。无边框 Host-owned DirectComposition composition swap chain 已通过单屏呈现和 Explorer generation 重建诊断验收：新旧 Shell PID/HWND 不复用，新 Surface 完整覆盖壁纸区域、桌面图标位于其上且任务栏不受影响。下一项门禁是独立 Renderer 在 Host 容器内创建 child HWND 和自有 DComp target/交换链；该路径通过则保留 Renderer Protocol v1，不能仅因使用 DirectComposition 就升级协议。DPI、热插拔、多屏、产品恢复和 Shell mutation recovery 仍未闭环，因此不能启用生产适配器或 allowlist。Stage B 状态保持 `In progress / Not accepted`。详细区分见 [`docs/implementation-status.md`](docs/implementation-status.md) 和 [ADR-008](docs/adr/008-desktop-attachment-capability-and-presentation.md)。

## 边界检查

安装 .NET 10 SDK 后，提交前至少运行：

```powershell
./tools/check.ps1 -Configuration Debug
```

该命令会检查 10 个产品项目的依赖边界，并运行当前 5 个测试工程。提交候选还应执行一次 `Release` 配置。
