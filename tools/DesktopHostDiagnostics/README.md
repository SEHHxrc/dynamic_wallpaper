# DesktopHostDiagnostics

采集 Shell/显示器拓扑，并在后续受控色块模式中验证桌面附着。不得自动修改持久设置；输出必须脱敏稳定设备路径中的用户数据。

## 当前能力

目前只实现只读显示器拓扑输出，没有创建测试 Surface，也没有验证桌面图标层级、首帧交换或 Explorer 重启恢复。下面的默认命令不应被描述为 Stage B 色块验收。

当前可输出真实会话中的稳定显示器 ID、虚拟桌面坐标、缩放和刷新率：

```powershell
./tools/dotnet.ps1 run --project tools/DesktopHostDiagnostics/DesktopHostDiagnostics.csproj
```

## 后续色块模式边界

只有在专用 Window Dispatcher、hidden provisional Surface 和 `ReplaceSurfacesAsync` 落地后才能增加。该模式必须使用显式参数启动，显示明显的测试标识，设置自动超时，在退出/取消/异常时销毁全部窗口，并输出 attach point 的 build、类名、进程、父子关系与层级验证结果。默认运行继续保持只读。

验收矩阵见 `docs/windows-desktop-host.md`。
