# ADR-008：桌面结构候选、可呈现附着能力与 Surface Binding 分离

- 状态：Accepted
- 日期：2026-09-06

## 背景

在 Windows 11 25H2 `26200.9168` 的交互式单屏诊断中，Raised Desktop 请求能够创建结构合规的 Progman 子 `WorkerW`，测试 HWND 也满足 `DefView(z0) > LiveWall(z1) > WorkerW(z2)`。但是即使测试窗口使用 `WS_EX_LAYERED`、alpha 255、显式 GDI 像素填充、`GdiFlush` 与 `DwmFlush`，用户仍看不到洋红色色块。

该反例证明“Shell HWND、parent 和 Z-order 正确”只说明结构候选成立，不能证明该窗口进入当前桌面的实际可见合成路径。现有 `DesktopAttachPoint(WindowHandle, AdapterId)` 把结构位置和呈现能力压缩为一个 HWND；Renderer Protocol v1 的 `AttachSurface(windowHandle, ...)` 也默认 Renderer 子 HWND 在 Host 容器内即可可见。两项假设都不足以表达 Raised Desktop。

此外存在两种不同清理结果：LiveWall 自有 HWND 可以完全销毁，但未公开 `0x052C / 0x0D / 0x01` 请求创建的 Shell WorkerW 未必自行消失。两者不能共用一个 `cleanupComplete` 结论。

## 决定

1. 将桌面能力分为三个连续阶段：

   ```text
   StructuralCandidate
     → PresentationProbe
     → RenderableDesktopAttachment
   ```

   `StructurallyValidated` 只是诊断中间态，不是可用 `DesktopAttachPoint`，不得进入生产 allowlist。

2. 一个生产附着能力必须同时绑定：适配器 ID、完整 build/UBR、无裸 HWND/PID 的结构指纹、Shell generation、呈现模式及其已通过的验收矩阵。单个 parent HWND 不是完整附着点。

3. Windows 原生 parent、DefView Z-order anchor、Shell backdrop WorkerW 等短生命周期句柄组成 Platform.Windows 内部的 `DesktopAttachmentLease`。Lease 只在当前 Shell generation 有效，不持久化、不跨进程、不暴露给 UI/Domain；Explorer/Shell 重建后必须重新发现。

4. Application 的 `IDesktopHost` 生命周期语义暂不改变：仍创建 provisional Surface、首帧后批量替换并幂等销毁。但当前 `DesktopAttachPoint` 的单 HWND 形状只适用于已经验证的 single-parent HWND 模式；在生产 Raised Desktop 前必须改为不泄漏 Shell 句柄的能力描述符，由 Platform.Windows 在内部解析为 Lease。

5. Renderer Protocol v1.0 的 `AttachSurface.windowHandle` 明确定义为 `HwndChild` binding，只能用于已证明 Renderer 子 HWND 可见的 Surface。Renderer 在自有 child HWND 内绑定自有 DirectComposition target/交换链属于 Renderer 私有实现，跨进程仍只传 Host 容器 HWND，因此不需要仅因使用 DirectComposition 而升级协议。只有 Host 必须接收或合成 Renderer 的共享纹理、交换链句柄、同步 fence 等 GPU 资源，或跨进程对象不再是容器 HWND 时，才必须新增版本化 `SurfaceBinding`、协议 capability 和契约测试；不得静默改变 `windowHandle` 的含义。

6. Shell attachment presentation 与 Renderer surface binding 是两个独立维度。诊断侧至少区分 Host-owned layered GDI、non-layered GDI、DirectComposition/交换链等 Shell 呈现证据；跨进程侧当前只定义 `HwndChild`。Host 自有 DComp Surface 可见只证明 Shell 路径成立，不能替代 Renderer-child DComp 探针。诊断枚举不自动进入公开协议。

7. 验收同时要求：结构正确、真实像素可见、位于图标后方且不遮挡任务栏、首帧语义成立、LiveWall 自有资源清理完成、Shell 变更可恢复。自动 HWND 快照不能替代可见性结论。

8. 清理状态拆分为 `OwnedResourcesCleanup` 与 `ShellMutationRecovery`。前者成功不能覆盖后者的 `CleanupIncomplete`；工具不得静默重启 Explorer。

## 备选方案

- 把 Progman 直接作为 `DesktopAttachPoint`：拒绝。结构存在但像素不可见，而且无法表达 DefView/WorkerW Z-order anchor。
- 只给现有 record 增加更多 HWND：拒绝。会把易失 Shell 句柄扩散到 Application，并继续缺少呈现模式和 generation。
- 将 `StructurallyValidated` 视为支持：拒绝。`26200.9168` 已提供结构通过、人工视觉失败的反例。
- 立即把 Renderer Protocol 改为共享纹理：拒绝。当前还没有证据证明哪种提交路径可见，提前冻结协议会扩大错误设计。
- 只要内部使用 DirectComposition 就升级协议：拒绝。图形 API 选择是 Renderer 私有实现；协议是否变化由跨进程对象、资源所有权和同步语义决定。
- 放宽 Legacy WorkerW 规则：拒绝。Legacy 0 候选与 Raised 呈现失败是两个独立事实。

## 影响

- Stage B 在找到至少一种 `RenderableDesktopAttachment` 前不能接受。
- Raised Desktop 已获得 Host-owned DComp 单屏呈现与 Explorer generation 重建的诊断证据；下一步是 Renderer-child DComp 跨进程探针，而不是生产 Adapter 或 allowlist 工作。
- `DesktopAttachPoint` 与 Surface Factory 内部接口仍需要受控迁移；Renderer `AttachSurface` 只有在 Renderer-child 探针失败并证明必须跨进程共享 GPU 资源时才需要迁移。
- Legacy WorkerW 现有失败关闭行为保持不变。

## 迁移与回退

1. Host-owned layered/non-layered GDI 已验证不可见；无边框 Host-owned DirectComposition/交换链已验证单屏可见，并通过 Explorer generation 变化后的诊断级重建。
2. 下一步实现隔离 Renderer-child DComp 探针：Host 侧创建生产等价容器 HWND，独立 Renderer 辅助进程通过现有 `AttachSurface` 接收该 HWND、创建自有 child HWND，并在 child 上绑定自有 DComp target/交换链。
3. 若 Renderer-child 路径可见且恢复、首帧与清理语义成立，保留 Renderer Protocol v1；随后定义 Platform.Windows 内部 Lease 和 Application 能力描述符，不新增跨进程图形对象。
4. 只有 Renderer-child 路径不可见且证据证明必须由 Host 合成 Renderer 帧时，才设计协议 1.1 的强类型 `SurfaceBinding`、GPU 资源所有权、handle/fence 传递、Schema 和契约测试。
5. 完成 DPI、热插拔、多屏、异常退出以及产品 Host/Renderer Explorer 恢复矩阵后，才允许添加精确 build/UBR allowlist。
6. 任一步失败均回到 `Not supported`；不得回退为直接附着 Progman。

详细状态和实验边界见 `docs/windows-desktop-host.md` 与 `docs/implementation-status.md`。

## 实施记录（2026-09-06）

- layered GDI 与 non-layered GDI 在结构顺序正确时仍不可见，保留为“结构不等于呈现”的失败反例。
- DirectComposition composition swap chain 绑定 LiveWall 自有 Progman 子 HWND 后产生了真实桌面像素。首次窗口残留 `WS_CAPTION`，导致交换链客户区未覆盖上、左非客户区；直接创建无边框子窗口后问题消失。
- 第二次 5 秒实验自动验证 `DefView > LiveWall > WorkerW`、窗口 `0,0 2880x1800`、无 `WS_CAPTION` 和 owned-resource cleanup；用户人工确认像素完整覆盖壁纸区域，桌面图标位于其上且任务栏不受影响。
- 该结果形成 `26200.9168 + DirectCompositionSwapChain` 的单屏 `RenderableDesktopAttachment` 诊断证据，不等于生产接受。Shell mutation recovery、Explorer、DPI、热插拔、多屏和异常恢复矩阵完成前，生产 Raised allowlist 保持为空，Stage B 保持 `In progress / Not accepted`。
- 显式 Explorer 恢复诊断只在活动 Surface 已验证且用户提供双重确认参数后终止当前 Progman 所属 Explorer。首次有效执行确认新 Shell PID/核心 HWND generation 全部变化，并在新 Progman 上重新发现、重新请求和创建全新 DComp Surface；自动结果 `generationChanged=True`、`rebuilt=True`。人工观察到旧洋红短闪、Shell 黑屏重建、新黄色完整显示并自动还原，任务栏恢复且未被遮挡。该证据不允许复用旧 HWND，也不替代产品 Host/Renderer 恢复测试。
- 该成功路径仍是同一诊断进程拥有 Host Surface HWND 和 DComp target。它验证了 Shell attachment 与 generation 重建，但没有验证独立 Renderer 在 Host 容器内创建 child HWND 的协议 v1 路径；因此协议保持 v1，下一项门禁是 Renderer-child DirectComposition 探针。
