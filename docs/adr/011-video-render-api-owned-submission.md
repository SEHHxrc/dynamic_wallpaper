# ADR-011：Video Renderer 使用 D3D11 Render API 掌握帧提交

- 状态：Accepted（后端选择已接受，C1 有限单屏代表验收完成；C2.1 单屏导入与重启内容恢复代表项通过；生产接受待完成）
- 日期：2026-10-28
- 范围：C1 正式内部后端；用户于 2026-10-28 明确接受，替代正式 wid/gpu-next 输出

## 背景与设计差异

C0 证明 `wid + gpu-next + d3d11` 可以在当前单屏桌面显示视频，但其客户端 FileLoaded、PlaybackRestart、VO 配置及画幅数据不能证明某次 Present/Commit 成功。原根架构技术表选择 gpu-next；本 ADR 经用户明确接受后同步调整该表，不将加载事件重新命名为首帧。

固定开发版本的 render.h 已声明 D3D11 Render API，尽管该发行归档遗漏了 render_d3d11.h 文件。同一固定提交的[原生头文件](https://github.com/mpv-player/mpv/blob/e471496c56/include/mpv/render_d3d11.h)规定由调用方创建目标纹理并呈现，render 返回前向同一 immediate context 提交工作。该路线可以让同一 Renderer 进程掌握 GPU 提交结果，无需向 Host 传图形资源。

## 决定

Video Renderer 内部由 `vo=libmpv` 和 D3D11 Render API 生成帧；Renderer 自己拥有 device、child HWND、DComp target/visual 与 swap chain，按当前 target/binding 记录 Render、Present、Commit，以及首次/重附着所需的同步完成结果。跨进程仍只传 Host 容器 HWND，继续 Protocol v1 HwndChild，不改 DTO、JSON Schema 或 Application/Host Surface 端口。

本决定改变内部 `wid/gpu-next` 输出方式，不改变图形资源的进程所有权。根架构与正式入口按此迁移；C0 路径保留为历史诊断，不能成为正式会话静默回退，也不能把其人工成功转移为新后端桌面呈现已通过。采用决定不等于呈现/产品验收完成。

首帧证据限定为当前 generation、binding epoch、content revision、目标尺寸与操作序号；成功提交必须来自 Renderer 自有目标，不能来自机器计时、文件加载、C0 人工反馈、仅有 GPU readback 或无窗口测试。正 HRESULT 状态（如 occluded）不等于 S_OK。首次/重附着需要成功 Render + Present + DComp Commit/同步；已有 SetBounds 允许在同一 binding 上对已成功呈现帧变换并 Commit，不要求等待物理屏幕显示后才能激活隐藏 provisional Surface。

`FrameSequence` 明确为成功目标提交序列，不是解码帧序列。暂停时允许将当前有效视频帧重新渲染到新 target，并以新序列记录该 target 的 Render/Present/Commit；不能等待媒体时间推进或复用旧目标结果。首帧证明有效提交，不承诺物理屏幕已扫描显示。正常循环不做逐帧读回/像素哈希或合成强制同步，首次/重附着才等待对应 Commit 完成。

专属 render 线程串行拥有 immediate context、render API 与 child HWND 的消息泵；控制线程调用普通 client API。render 线程不得反向等待控制线程。停止先拒绝新任务，再在 render 线程释放 context 和 GPU/窗口资源，最后控制侧销毁 core/卸载库。Renderer 私有 Win32 声明限于其 `NativeMethods/VideoNativeMethods.cs`，只管理自有 child、消息泵及进程退出顺序，不提供 Shell 枚举/附着。通用 IPC 原语由 Infrastructure 维护，以源码链接编入独立 Renderer，仍只引用 Contracts 产品项目。

## 备选方案

- 将加载/VO 事件直接当成 FirstFramePresented：拒绝，缺少提交证据。
- 单独使用 [GetLastPresentCount](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getlastpresentcount)：不足，该 API 表示调用次数。
- 依赖物理扫描/显示统计：不作为隐藏 provisional 的放行前提，不能引入等显示才能激活的循环。[GetFrameStatistics](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics)还有后端及多屏可靠性限制。
- patch/fork libmpv VO 发布成功 Present 回调：可研究，但增加原生构建、ABI 与维护成本，当前不选。
- 软件帧拷贝、跨进程纹理或 GPU handle：不引入，当前没有这类需求。

## 实施与证据

C1.0 已实现私有首帧票据/严格门禁、单写者事务核及 D3D11 render ABI 原型。新增无窗口 GPU preflight：真实解码到 Renderer 侧自有纹理、读回两帧不同 RGB 数据哈希，并由自身调用 Present 检查 S_OK；没有 HWND、DComp target、Shell 或协议首帧事件，不能据此判定桌面呈现或正式协议会话通过。

最初预检拿到两帧就释放 Render Context，导致控制线程最后的媒体观察与 VO 结束竞争，样本保持失败。修正为控制者统一退休、render 线程在收到停止前继续服务后，预检通过。失败和成功的独立路径见实现台账，不删除旧样本或放宽错误条件。

C1.1 已接正式 Program、Pipe/Envelope、child/DComp 目标及真实提交证据。新增 37 项 Wire 门禁通过；正式进程在隐藏容器完成首帧、暂停重附着、Bounds、正常 Shutdown 和代表失败清理。包括此前 32 项事务核，Debug/Release 全量各 520/520。该结果不涉及 Shell 或实际桌面呈现，C1 整体验收仍进行中。原生头文件来源/哈希见 tools/libmpv-render-api.lock.json；历史失败、原始证据和后续桌面义务见实现台账。

## 迁移、验收与回退

1. 后端选择已明确接受；同步根架构与模块说明。
2. C1.1 已接正式 Renderer Pipe Client、Hello/Initialize、命令/事件顺序和超时，保持仅 Contracts 产品项目依赖。
3. C1.1 已建立 child/DComp 目标、提交门禁、正常/取消/异常清理和进程退出顺序，自动及隐藏目标验证通过。
4. C1.2 经用户单独授权完成物理单屏正式产品候选，真实首帧后一次 Replace、实际 ExitCode=0 与人工覆盖/运动/层级/恢复通过，代表项关闭；Shell mutation 仍为 CleanupIncomplete/整轮退出码 6，旧失败保留。
5. C1.3 的正式 Host 隐藏控制/恢复与最小失败门禁、随后获准的单屏控制代表验收通过：实际暂停与恢复、同 Renderer 新 binding、正常 ExitCode=0 与人工反馈齐备。C1 按约定有限范围关闭；双屏/真实 Video Explorer、剩余环境和资源矩阵、C2 内容链独立验收。后端选择不变，不新增协议对象，本轮未新增 WorkerW 不代表旧 mutation 恢复。

固定构建 API 不可用、提交失败或证据不完整时失败关闭，不回退成只等 FileLoaded 的假首帧。原生库/头文件来源、提交、哈希和实际能力均需核对；硬解、GPU 拷贝和持续开销另行测量。发行依赖许可证审计、Stage B、Shell mutation、双屏延期及空生产 allowlist 不因此豁免。
