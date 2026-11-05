# Mpv

封装 libmpv 创建、参数白名单、播放控制、事件泵和 SafeHandle。发行前必须固定并审计 mpv/FFmpeg 构建及许可证。

C0 私有 `MpvClient` 提供绝对路径和 SHA-256 校验、动态 client API、SafeHandle、禁用用户配置/脚本/输入、本地文件加载、暂停/恢复与单一事件轮询；其 wid/gpu-next/D3D11 分支保留为历史诊断。`MpvOutputEvidence` 不构成呈现证明。C1.1 正式 Program 已接下述 Render API 路线；不暴露任意 mpv 命令/指针到公开端口，无其他产品程序集引用。开发依赖和哈希见 `tools/libmpv-development.lock.json`，发行审计仍待完成。

2026-10-28 cover 几何与单屏可见运动/暂停代表实验通过用户确认，C0 呈现可行性切片关闭；原始自动/人工证据分开保留。该结果不赋予 FileLoaded、PlaybackRestarted 或 VO margins 以 FirstFramePresented 语义，C1.1 的目标提交证据由新路线独立实现，实际桌面验收仍待完成。Shell mutation 仍未恢复，默认产品内容链和发行审计不因可见实验成功而免除。

C1.0 开始的 `MpvD3D11RenderContext` 已用于 C1.1；ADR-011 已接受正式 `vo=libmpv` 路线。按固定 render.h/render_d3d11.h ABI 创建 context、区分新帧/redraw/repeat、渲染调用方纹理并 report swap；Render API 只在专属 render 线程调用，普通 client API 留在控制线程。更新回调只唤醒 render 线程，不反向等待控制；停止注销回调并释放 context 后，才释放 client/library。暂停重附着保留 core/context，把当前有效帧重新提交到新 target，不要求新解码或时间推进。

正式播放不做 GPU readback；首次/重附着等待 DComp Commit 完成，连续帧只 Render/Present，不逐帧 Commit/同步。Render API 模式允许音频但初始化为音量 0 且 mute=yes，后续仅接受已校验的 SetVolume；C0/null VO 继续禁用音频。头文件来源/提交/哈希见 `tools/libmpv-render-api.lock.json`，实际硬解和拷贝性能未验收。

显式诊断日志只在请求后启用，按 32 条/每条 512 字符限长；媒体结束错误保留 native error code。无窗口 GPU preflight 是单独开发工具，不是产品的 GPU readback 每帧策略，不给 Core 引入软件纹理拷贝。原型不导出图形指针到协议，仍无其他产品项目依赖。

C1.3 的 `VideoControlSnapshot` 读取 libmpv 实际 pause/time-pos/画幅/音量/静音值；控制及新 binding 的设置保持通过读回核对。`VideoControlEvidence` 是独立的只读开发证据：仅显式子进程环境 LIVEWALL_VIDEO_CONTROL_EVIDENCE 启用，读取后清除；全新本地非 reparse 文件最多 512 条，单调耗时，不包含令牌、Pipe、PID/HWND 或内容路径。常规播放不启用此文件或周期状态采样，不增加协议遥测能力，也不做逐帧 GPU 读回。真实隐藏控制案例记录暂停时间增量 0、恢复后推进约 0.5 秒；无音轨 fixture 的音量读回不代表听感验收。

