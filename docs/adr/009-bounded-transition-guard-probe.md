# ADR-009：有限 Transition Guard 仅作为预呈现可行性探针

- 状态：Accepted
- 日期：2026-09-14

## 背景

显示器旋转后的正式 Surface 最终几何、批量替换、图标层级与清理已经正确，活动 Surface 的响应式适配也缩短到约 20.587 ms；但实机仍可能在 Windows 模式切换黑屏结束后短暂露出系统静态壁纸。`WM_DISPLAYCHANGE` 在显示分辨率已经改变后才发送，`QueryDisplayConfig` 只提供采样时刻的配置快照，项目没有可依赖的公开预变更通知。

继续压缩响应路径不能回答一个更基础的问题：显示变化前已创建、部分位于旧 Progman 可见区域之外的窗口内容，能否在 Progman/DWM 采用新几何时自动进入可见合成，而无需 LiveWall 在变化后再次调整窗口或提交图形内容。

## 决定

新增显式诊断模式 `--raised-desktop-transition-guard-probe`，只验证上述 DWM 呈现假设。探针在正式产品候选 Surface 已激活后，为一个明确目标显示器计算宽高互换后的有限候选矩形，并创建：

```text
GuardRegion = (CandidateBounds - CurrentBounds) - OtherConnectedDisplayBounds
```

Guard 被预先创建在 `DefView > active Surface > guard > WorkerW` 层级，使用与活动色块不同的诊断颜色。进入 `Armed` 后，探针不再对 Guard 调用 `SetWindowPos`、resize 或 DirectComposition Commit；显示变化后的第一次可见内容只能来自系统自动揭示预置内容。随后仍由既有产品候选拓扑链执行精确 Surface 重建和一次批量 Replace。

Guard 生命周期限定为 `Created → Armed → Observed → Retired`。Shell generation、Progman 身份、目标 DisplayId、预测几何或相邻显示器隔离不满足时，结果为 `GuardUnavailable` 或验证失败，并清理全部自有资源。

## 架构边界

本决定不修改 `IDesktopHost`、`SurfaceRequest`、`DesktopSurface`、`ReplaceSurfacesAsync`、Renderer Protocol v1、DTO、JSON Schema 或生产 allowlist。Guard 不是活动 Session、正式 Surface、Assignment、Renderer binding 或新的 topology generation；它只存在于 Platform.Windows 的诊断生命周期中。为允许正式 Raised 结构复验与 Guard 短暂共存，只按精确 HWND 登记诊断窗口，不按类名、PID 或宽松结构放行竞争 Surface。

Guard 区域必须可分解为有界的少量矩形，受最大尺寸与总像素面积限制，并且不得与其他已连接显示器相交。探针不创建虚拟桌面大小或覆盖所有可能布局的窗口，也不把单屏旋转算法泛化到热插拔、主屏切换或任意坐标重排。

## 验收与后续

人工第一可见帧分类为 `GuardVisible`、`StaticWallpaperVisible`、`SystemBlackOnly` 或 `Inconclusive`。只有 `GuardVisible` 才证明预置呈现路径成立；最终正式 Surface 正确不能替代该判断。

若横屏与纵屏方向的重复实测不能稳定得到 `GuardVisible`，停止扩张 Guard、超大 HWND 或第二套正式 Renderer，并将旋转期间的单帧静态壁纸暴露记录为 `TransientCoverageGap`。安全门禁仍禁止错误层级、错误显示器内容、半批提交和资源残留；是否接受短暂系统静态壁纸属于独立发布体验决策。

若 TG0 成立，TG1 再单独决定有限 Guard 的内容来源与资源预算。诊断色成功不自动授权保留 Renderer、多目标 binding、共享纹理或协议升级。

## 实施记录

2026-09-16 在 `26200.9445 / session=3 / WinSta0\Default` 完成首个内置屏横向 `2880×1800@200%` → 纵向 `1800×2880@200%` 样本。Guard 在显示变化前预置于 `(0,1800 1800×1080)`，总面积 1,944,000 像素，与负坐标外接屏交集为零；武装后窗口变更次数为零。显示变化后实际目标 bounds 与预测完全一致，Shell generation 未变化。用户确认 Windows 黑屏结束后的第一可见内容为一闪而过的洋红 Guard，未看到新增下方区域的黑屏或静态壁纸，随后正式黄色 Surface 完整显示；外接屏、图标、任务栏和普通窗口均未被遮挡，最后正常恢复。

自动结果为 `GuardVisible` 对应的结构正证据：`ActiveSurfacesResized=26.892 ms`、`SurfacesReplaced=1046.707 ms`、一次批量 Replace、Guard 已 Retired、最终 graceful/clean、无 LiveWall HWND 残留。该样本证明当前横→纵有限预置路径技术上成立，但不替代纵→横和重复性样本，也不授权生产接线。

同日完成反向纵向 `1800×2880@200%` → 横向 `2880×1800@200%`。Guard 预置于右侧 `(1800,0 1080×1800)`，面积同为 1,944,000 像素，与外接屏零交集，Armed 后没有窗口 mutation；实际目标 bounds 命中预测，Shell generation 不变。用户确认系统黑屏结束后首先出现洋红色，随后整个主屏变为黄色，直到正常恢复原壁纸，全程没有静态壁纸暴露、错误覆盖或双屏层级异常。自动时间线为 `ActiveSurfacesResized=18.620 ms`、`SurfacesReplaced=1027.028 ms`，最终清理完整。

因此首轮证明双向都能揭示预置内容，但当时尚不能证明整个预测区域会在第一可见帧原子出现。

后续重复矩阵得到：纵→横 `5/5` 均明确先显示完整洋红新增区域，其中一次目标为“横向（翻转）”，其物理左侧洋红与桌面坐标右侧 Guard 的旋转映射一致；横→纵得到 4 次明确完整 `GuardVisible`、1 次无法区分先后的 `Inconclusive`，以及最终补测中一次可重复描述的部分呈现。最后一次的第一可见帧是“上方小部分洋红、其余黑色”，随后才完全洋红，最终由黄色正式 Surface 接管。该过程没有静态壁纸暴露、相邻显示器侵入、窗口层级错误或资源残留，但证明 DWM/显示模式切换不会保证整个预置区域原子进入第一帧。

## TG0 最终决策

TG0 的“预置窗口可被新几何自动揭示”假设部分成立，但“无需变化后提交即可消除首帧覆盖缺口”的严格目标失败。项目不进入 TG1，不设计 Guard 内容来源，不增加第二套正式 Renderer、多目标 binding、共享纹理或超大窗口，也不把诊断 Guard 接入生产 Surface 生命周期。

旋转最终几何、原子 Replace、层级和清理继续视为通过；物理模式切换允许的短暂黑屏/部分揭示继续记录为 `TransientCoverageGap`。该体验限制与 attachment 安全门禁分开：错误层级、错误显示器内容、半批提交和资源残留仍阻止 allowlist，而当前 Guard 诊断结果本身不构成生产兼容授权。

## 依据

- [WM_DISPLAYCHANGE](https://learn.microsoft.com/en-us/windows/win32/gdi/wm-displaychange)
- [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)
- [DirectComposition clipping](https://learn.microsoft.com/en-us/windows/win32/directcomp/clipping)
- [CreateTargetForHwnd](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositiondesktopdevice-createtargetforhwnd)
