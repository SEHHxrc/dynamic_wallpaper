# LiveWall

LiveWall 是面向 Windows 10 1903+ / Windows 11 的轻量动态壁纸程序。本仓库已建立领域、应用、跨进程契约和进程边界，并具备部分 Foundation 与 Windows Desktop Host 骨架；这些代码尚未接入产品入口，也不表示 Foundation 或 Stage B 已通过完成验收。

## 权威文档

- [`ARCHITECTURE.md`](ARCHITECTURE.md)：最高级架构约束；代码与其冲突时视为代码缺陷。
- [`docs/README.md`](docs/README.md)：设计文档索引及文档优先级。
- [`docs/windows-desktop-host.md`](docs/windows-desktop-host.md)：Stage B 的窗口线程、Surface 交换、Explorer 恢复目标设计与当前差距。
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

当前可验证的是部分纯逻辑、配置/IPC 基础类、DisplayConfig 拓扑和实验性桌面附着骨架。`RaisedDesktopAdapter` 仍是主动失败并回退的占位实现；现有 Surface 尚无专属窗口线程、隐藏 provisional 状态和显式替换端口；Explorer 监视仍以轮询为主，Host 未处理恢复命令，现有恢复也只尝试重挂旧 HWND；色块诊断尚未实现。产品入口仍是占位程序，Stage B 状态为 `In progress / Not accepted`，不得将当前工程视为可运行产品或已完成桌面宿主。

## 边界检查

安装 .NET 10 SDK 后，提交前至少运行：

```powershell
./tools/check.ps1 -Configuration Debug
```

该命令会检查 10 个产品项目的依赖边界，并运行当前 5 个测试工程。提交候选还应执行一次 `Release` 配置。
