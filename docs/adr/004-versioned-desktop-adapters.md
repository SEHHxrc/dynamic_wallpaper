# ADR-004：桌面附着使用版本化适配器

- 状态：Accepted
- 日期：2026-09-05

## 决定

WorkerW/Progman 和 Raised Desktop 等 Shell 差异仅由 `IDesktopHostAdapter` 实现，按运行时 Shell 快照选择。

适配器的 `IsSupported` 必须基于已验证的 Shell build 能力和层级规则；用户设置或单一布尔 feature flag 只表达尝试意图，不能证明支持。`DiscoverAsync` 必须重新验证类名、父子关系、进程身份和层级。Raised Desktop 在诊断矩阵完成前保持禁用；Legacy WorkerW 是可回退的 Experimental 适配器，不是公开 Windows API 保证。

## 影响

未公开 Shell 行为被集中隔离，可按 Windows 版本替换和回退；上层不依赖窗口查找细节。未知 build 或验证失败时必须失败关闭/回退静态壁纸，不能把 `Progman` 本身当作 Raised Desktop 附着点。
