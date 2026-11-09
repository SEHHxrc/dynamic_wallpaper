# Tools

开发与诊断工具，不随 Host 常驻，也不构成产品公开 API。工具必须复用公开 Contracts/Application 端口，禁止通过反射绕过内部边界。

`verify-boundaries.ps1` 是无第三方依赖的架构守卫，检查项目引用、目录说明、Schema/XML、解决方案路径和典型越界代码。CI 应在编译与测试之前运行它。

## 项目级 .NET 环境

- `bootstrap-dotnet.ps1`：按 `global.json` 将 SDK 安装到仓库的 `.dotnet/`，补齐 C# Dev Kit 所需的项目级运行时，把 CLI 状态与 NuGet 缓存限定在仓库内，并生成仅供本机使用的 `.vscode/settings.json`。
- `dotnet.ps1`：使用项目 SDK 执行任意 `dotnet` 命令，例如 `./tools/dotnet.ps1 build LiveWall.sln`。
- `use-dotnet.ps1`：在当前 PowerShell 中激活环境；需要以 `. ./tools/use-dotnet.ps1` 的方式调用。
- `check.ps1`：依次执行架构检查、隔离恢复、构建和全部 5 个测试工程；支持 `-Configuration Debug/Release`。

`.dotnet/`、`.dotnet-cli-home/`、`.nuget/` 与下载缓存均被 Git 忽略，不影响系统安装或其他仓库。

`run-video-product-candidate.ps1` 是 C1.2 的预构建 Release 入口：`-PreflightOnly` 仅检查固定资产/交互桌面；可见播放必须单独使用 `-ConfirmDesktopPresentation`，默认主屏 10 秒。只临时设置项目运行时并精确恢复环境，拒绝覆盖证据、不会重启 Explorer 或修改生产 allowlist。行为、退出码和验收范围见 `DesktopHostDiagnostics/README.md`。

`run-video-host-restore.ps1` 是 C2.1/C3.0 的预构建 Release 入口（加 `-CrashRecovery [-Crashes 1..4]` 时运行 C3.0 崩溃恢复，结果写入 `artifacts/diagnostics/video-c3/crash-<随机>/`）：`-PreflightOnly` 只读检查源视频、Renderer、libmpv 哈希和交互桌面；`-ConfirmDesktopPresentation` 在用户同意后运行“导入 → Apply → 停止 → 重启恢复”的可见实验，结果写入 `artifacts/diagnostics/video-c2/restore-<随机>/`。脚本兼容 Windows PowerShell 5.1，只在本次调用中设置项目 .NET 运行时。

`VideoPresentationProbe --host-control-preflight` 是 C1.3 隐藏验证入口：复用正式 Host/Video，覆盖实际控制、暂停重附着设置保持和最小失败收口。五个独立场景、只读原生日志和证据边界见工具 README；该命令不显示桌面或重启 Explorer，不替代后续人工控制验收。

系统 `dotnet` Host 低于 10 时无法识别 `global.json` 的 `sdk.paths`。Bootstrap 会额外把 C#、C# Dev Kit 和 VS Code 集成终端指向仓库内的 Host，并安装当前 C# Dev Kit 3.20.x 要求的 .NET/ASP.NET Core 10.0.5。运行后需要执行一次 **Developer: Reload Window**；若 C# Dev Kit 询问是否采用项目运行时覆盖，请选择 **Use Overrides**。
