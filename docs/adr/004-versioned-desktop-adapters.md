# ADR-004：桌面附着使用版本化适配器

- 状态：Accepted
- 日期：2026-09-05

## 决定

WorkerW/Progman 和 Raised Desktop 等 Shell 差异仅由 `IDesktopHostAdapter` 实现，按运行时 Shell 快照选择。

适配器的 `IsSupported` 必须基于已验证的 Shell build、层级规则和 presentation kind；用户设置或单一布尔 feature flag 只表达尝试意图，不能证明支持。`DiscoverAsync` 必须重新验证类名、父子关系、进程身份、层级以及与已验证内容提交路径匹配的能力。`StructurallyValidated` 只产生结构候选，不能单独形成生产附着点。Raised Desktop 在呈现、清理和恢复矩阵完成前保持禁用；Legacy WorkerW 是可回退的 Experimental 适配器，不是公开 Windows API 保证。

## 影响

未公开 Shell 行为被集中隔离，可按 Windows 版本替换和回退；上层不依赖窗口查找细节。未知 build、结构验证失败或实际像素不可见时必须失败关闭/回退静态壁纸，不能把 `Progman` 本身当作 Raised Desktop 附着点。结构候选、可见呈现和 Surface Binding 的细化决策见 ADR-008。
