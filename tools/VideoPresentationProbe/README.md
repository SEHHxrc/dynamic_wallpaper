# C0 Video Presentation Probe

该工具还承载 C1.0 无窗口 GPU、C1.1 正式进程隐藏容器、C1.2 隐藏 Host 产品链及 C1.3 控制/失败验证；默认 C0 命令和可见测试许可边界不变。

独立的开发诊断工具，不是正式 Video Renderer。C0 链接 `Renderer.Video/Mpv` 的私有后端源码；C1 则启动独立正式 Video 可执行文件，不用工具代替产品实现。工具可引用 Host/Platform/Infrastructure，产品 Video 仍只引用 Contracts。工具的 Win32 声明不链接产品；正式 Video Program 已接协议，但默认生产 Host 仓库/Provider 尚未启用，v1 DTO/Schema、Surface/Replace 契约及 allowlist 不变。

## C1.3 隐藏 Host 控制与失败门禁

```powershell
& $probe --host-control-preflight $absoluteFormalVideoExe $absoluteDll $absoluteFixtureRoot $freshDirectory controls
```

其他独立场景为 load-failure、first-frame-timeout、process-exit、pipe-disconnect；每次使用全新绝对目录。另有 candidate-plan，复用与可见入口相同的四阶段驱动，隐藏预检每阶段保留 250 ms，而实际桌面入口固定各 5 秒。显式构建正式 Video 和本工具再运行，不能只依赖 dotnet test 更新非测试 exe。复用正式 Host 单写者、SessionCoordinator、Provider 和独立 Renderer，隐藏容器从不显示，不使用 Shell/Raised override。

controls 通过子进程专用的只读原生日志验证媒体时间暂停/推进、四种 Fit 映射、实际音量/静音、当前 binding Bounds 及同 Renderer 暂停重附着设置保持。工具仅向 Host 投递合成恢复命令，不重启真实 Explorer。日志环境变量读取后清除，最多 512 条、CreateNew、不含令牌/Pipe/媒体路径或 HWND/PID；它不是连续协议遥测。原 fixture 无音轨，音量属性验证不能扩大为听感验收。

加载失败使用独立临时根中的真实坏 AVI；首帧超时由 IRendererSession 诊断投递过滤器扣留第二轮真实 FirstFrame，明确验证 Host 等待/回收，不伪称原生 GPU 挂死。两者都必须保留旧 Session/Assignment、零失败批次 Replace，后续 Apply 能在同一 Host 成功。process-exit 只终止本轮创建的 Renderer，pipe-disconnect 只关闭其 Host 通道；必须自动进入 Host 失败通知并清理，Assignment 保留、不伪造 ShutdownCompleted，真实非零进程退出码照录。断连时 Host 可以强制结束未回执进程，Terminations/Dispose 次数独立观测，不要求假正常退出。

预期故障及全部不变量成立时驱动返回 0，索引分类为 ExpectedFailureValidated；未知异常或清理缺失仍失败。2026-11-03 五类有效结果、两次原始驱动分类失败和哈希见实现台账的 C1.3。所有原始记录保留，desktopPresentationValidated=False；可见桌面控制代表项须另行安排。

## C1.2 隐藏 Host 产品链

```powershell
& $probe --product-chain-preflight $absoluteFormalVideoExe $absoluteDll $absoluteFixtureRoot $freshResultJson
```

复用真实 HostCommandLoop、SessionCoordinator、正式 Provider/握手/Video 进程、首帧与一次 Replace、正常 retirement。只把 IDesktopHost 替换为不可见的自有容器，不调用 Shell、Raised lease 或生产 Native Factory，容器不会显示。内部单屏 320×180，1 秒保留仅用于提交后正常退休；70 秒取消预算和最终真实 process handle/退出码检查独立于 Apply budget。结果必须 replacementCount=1、首帧已提交、ShutdownCompleted、ExitCode=0、Surface 已销毁；attachmentBackendValidated/desktopPresentationValidated 明确 False。该入口用于不打扰桌面的集成验证，不代替真实主屏呈现测试；实际入口见 DesktopHostDiagnostics README。

## 依赖与原始素材

先运行 `tools/bootstrap-libmpv.ps1`，仅将固定的 x64 `20261021 / e471496c56` libmpv 开发构建放进项目 `.cache`。来源、归档和 DLL SHA-256 见 `tools/libmpv-development.lock.json`。动态加载前复核 DLL 哈希；不查找系统安装、不改全局 PATH、不执行安装器。该包没有完整发行审计，禁止据此分发产品。头文件的 ISC 许可不能被当作 mpv core/FFmpeg 的许可；保留构建配置并在发布前完成源代码、编解码器和依赖声明审计。

```powershell
. ./tools/use-dotnet.ps1
./tools/bootstrap-libmpv.ps1
dotnet build tools/VideoPresentationProbe/VideoPresentationProbe.csproj -c Release -m:1
$probe = './tools/VideoPresentationProbe/bin/Release/net10.0-windows10.0.18362.0/VideoPresentationProbe.exe'
$lock = Get-Content ./tools/libmpv-development.lock.json -Raw | ConvertFrom-Json
$dll = [IO.Path]::GetFullPath('./.cache/libmpv/20261021-x64/libmpv-2.dll')
& $probe --inspect-library $dll $lock.librarySha256 `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/library-inspection-unique.json'))
& $probe --generate-fixture ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/fixture-unique'))
& $probe --decode-fixture $dll $lock.librarySha256 `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/fixture-unique')) `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/headless-unique.json'))
```

素材是本地确定性生成的 `320×180 / 24fps / 72 帧 / 3 秒` 无压缩 BGR AVI：移动青色条、变化的橙黄背景和方向标记，无音频、无第三方素材，循环边界明显。生成目录包含现有 schema v1 `manifest.json`、`content/motion.avi` 和带哈希的 `fixture.json`。生成数据采用 CC0-1.0，仅限这个几何测试数据，不涉及工程代码或 libmpv。它验证真实媒体解码/输出，但不证明 H.264/HEVC 硬解或发行编解码能力；这些留到 C3。

颜色预期须分区说明：顶部 12 源像素是黄色方向条（非青色运动区域 RGB=255,255,0）；中部从 RGB=255,96,0 变化到 255,238,0；底部 12 源像素刻意降低红色为 100，后半段会偏绿，例如第 36 帧为 RGB=100,168,0。因此底部绿条本身不是解码偏色的证据，之前只说“橙黄背景”不够准确。素材及首次实机原始文件保持不变，不以重生成素材追改样本。

`--inspect-library` 和 `--decode-fixture` 使用 `vo=null`，不创建 HWND、D3D11 output 或 Shell Surface。前者输出实际 mpv/FFmpeg 版本和构建参数；后者检查 FileLoaded、PlaybackRestart 与媒体时间推进，并明确记录 `desktopPresentationValidated=false`。这两种模式不需要人工看屏。

## 有意隔离的呈现尝试

以下命令会改变桌面，必须先说明画面并取得人工许可，不能作为自动测试执行：

```powershell
& $probe --desktop $dll $lock.librarySha256 `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/fixture-unique')) `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/run-unique.json')) `
  --duration-seconds 10 --confirm-desktop-presentation
```

只支持物理单屏 `WinSta0\Default`。复用既有**显式可见的诊断 Host lease**，不调用产品 Create/Replace，不创建活动产品 Session。独立子进程只接收自有容器 HWND（使用已有 `AttachSurfacePayload` 的形状）及本地 fixture/DLL 身份，不接收 Progman/DefView/WorkerW。内部的受限、当前用户 Named Pipe 与一次性环境凭据只是 C0 诊断控制，不宣称已经实现 C1 正式 Renderer 会话；凭据读取后清除，不持久化。

Renderer 在自有 STA 线程创建、pump、resize、销毁子 HWND；libmpv 使用 `gpu-next / gpu-api=d3d11 / gpu-context=d3d11 / hwdec=auto-safe`，禁用用户配置、脚本、输入和音频。先持续运动，随后暂停约 1.2 秒、恢复，并让子画布短暂缩小 64 像素后恢复完整尺寸；总时长略大于指定运动观察时间。缩小时可能露出静态背景，这是显式 resize 样本，不应误记为完整覆盖通过。正常、错误、取消均先释放 mpv，再销毁 child，再退出子进程，最后释放 Host lease；无响应时仅强制结束本工具创建的子进程，保留失败与 forced 标记。

首轮发现 C0 没有应用 fixture 的 cover 设置：16:9 视频在 16:10 屏幕按默认适配可能产生稳定黑边。已显式设置 keepaspect=yes、keepaspect-window=no、video-unscaled=no、panscan=1，以保持比例的裁切填满；不扩大 HWND、不改 DPI/Shell 层级或拉伸视频。新结果增加 Host/child 实际 client 尺寸、mpv osd-dimensions 的 w/h 与四边距、cover 参数；缺失/dummy/不一致或正留边均拒绝几何通过。负边距代表裁切，不是黑边；这些后端几何记录仍不代替人工像素/颜色确认。正式 C1 的 SetFit 映射尚未实现。

## 证据边界

FileLoaded、PlaybackRestarted、VOConfigured、时间推进、循环重启和暂停位置稳定是**后端证据**，不是 DWM 像素或首帧回执。工具不发送 `FirstFramePresented`，不以这些信号开放产品 provisional Surface；C0 的显式可见诊断是为获取人工呈现证据的隔离实验，不能被当作产品激活路径。C1 仍须选择可验证的呈现证据后才接正式首帧门禁。

结果分类最多为 `PresentationAttemptCompleted / manualPresentation=NotYetConfirmed`，不能自动升级为生产可用或 C0 人工通过。需人工确认连续运动、循环、暂停/恢复、尺寸变化及图标/任务栏/普通窗口层级。结果仅保存完整 build、会话上下文、无句柄结构、媒体/依赖哈希、播放及清理事实；认证令牌、Pipe 名称、HWND/PID 不写结果。采样前后 Shell generation/显示拓扑若变化，该次不可视为稳定环境成功。

按 ADR-010 分开报告 OwnedResourcesCleanup 与 ShellMutationRecovery；新 WorkerW 残留返回退出码 6，不静默重启 Explorer，也不尝试销毁 Shell-owned 窗口。当前代码、依赖检查与无桌面测试的执行结果见统一台账；真实呈现须另行授权，不因构建通过而提前验收。

2026-10-24 经用户授权的首轮实机结果为 `artifacts/diagnostics/video-c0/run-76c5350ff3074a89b8170b677b14b5ab.json`：gpu-next/d3d11、child parent、媒体推进、暂停稳定/恢复及 resize 程序检查通过；正常 ShutdownCompleted，Renderer ExitCode=0，自有窗口销毁，无进程残留。Shell/显示拓扑未变，但本次请求新增 1 个 Shell WorkerW 且未消失，因此进程整体退出码 **6**，OwnedResourcesCleanup=Complete 与 ShellMutationRecovery=CleanupIncomplete 分开保留。原始 manualPresentation=NotYetConfirmed，实际运动画面和桌面层级尚待人工确认；未自动重启 Explorer 或执行第二次可见实验。

随后用户确认运动/控制等其余项目正常（含约 1 秒暂停），但顶部全程约 1 cm 黑边、底部约 1 cm 偏绿。人工代表性可见/运动/暂停及层级按反馈记录，完整覆盖未通过；底部绿条有上述原始像素依据，整体颜色对应关系不据此自动判通过。首轮缺少 client/VO 边距数据，90 px 留边只是比例推导，不能当成已测量根因。cover 与观测修正后仅执行构建、自动门禁和 vo=null 检查，实际消除黑边仍待单独授权重测。C0 尚未关闭，Shell mutation 生产门禁不变。

2026-10-28 已经单独授权执行修正后复测，结果 `artifacts/diagnostics/video-c0/cover-retest-20261028-ebc2e645ae174de4b933411c50054030.json`。环境仍为 `26200.9711 / WinSta0\Default` 单屏 2880×1800@200%，本次 session=14。初始/最终 Host 与 child client、最终 VO 均为 2880×1800；top/bottom=0、left/right=-160，cover 几何门禁通过；播放控制、ShutdownCompleted、Renderer ExitCode=0、自有清理与零进程残留通过。Shell/显示拓扑未变，但本次新 WorkerW 从 0→1 且残留，整体退出码 6 / ShellMutationRecovery=CleanupIncomplete。没有执行额外清理或自动重跑，人工覆盖/色彩/层级仍待本次反馈；不能只凭几何记录关闭 C0，旧失败和原始 manualPresentation 字段保持不变。

随后用户明确反馈“这次全程没有黑色，其他仍然正常”，结合机器记录，**C0 物理单屏代表性呈现可行性验收通过、开发切片关闭（2026-10-28）**。证明本次连续运动/循环、暂停恢复、cover 全覆盖、层级及正常结束成立；不是所有编码、硬解、HDR/仪器色准或双屏视频验收。用户反馈关联同一原始结果及 SHA256 `3ACABEDE677F899372075DF62969BF47F0F4EDBDD31D927DDB644B10EF7DBB64`，只在状态台账补录，不改写原始 manualPresentation 或自动发出 FirstFramePresented。OwnedResourcesCleanup=Complete 与 ShellMutationRecovery=CleanupIncomplete/整体退出码 6 继续并存，不能改成整轮恢复成功或据此放行生产。下一步 C1 需要真实运行时首帧判据及正式协议接线，不重复本轮可见实验。

## C1.0 无窗口 D3D11 Render API 预检

```powershell
& $probe --render-api-preflight $dll $lock.librarySha256 `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/fixture-unique')) `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c1/preflight-unique.json'))
```

该模式会使用 GPU，但不创建 HWND/DComp target、不采样或操作 Shell、不显示视频。控制线程调用普通 client API；专属 render 线程拥有 D3D11 device、纹理与未绑定窗口的 composition swap chain，仅调用 mpv_render_*。真实解码两帧到 GPU，开发预检将纹理读回并要求不同非空 RGB 哈希，Present 需精确 S_OK；不是以 Present 调用次数代替成功结果。读回只用于诊断，不是生产渲染设计。

两帧就绪后 render 线程继续服务，直到控制者采完状态并统一取消/退休；不能立即释放 context 把正常清理与媒体故障混在一起。内部预算 10 秒，调试驱动可另设 20 秒硬进程门禁，只能结束自身创建的 helper。结果明确 firstFramePresentedEmitted/desktopPresentationValidated/formalProtocolSessionValidated=False，即使 GPU 预检 valid=True，也不得放行产品 Surface 或宣称 C1 完成。

该路线内部从 C0 的 wid/gpu-next 改为 vo=libmpv Render API，ADR-011 已获用户接受并同步根架构，正式 Program 已在 C1.1 接入。C1.0 首次失败样本及成功路径保留在 `artifacts/diagnostics/video-c1`；当时没有真实 child/DComp 或 Wire 事件，不与下述新验证混淆。可见测试必须另行得到用户同意。

## C1.1 正式 Video 进程隐藏容器预检

```powershell
& $probe --protocol-preflight `
  ([IO.Path]::GetFullPath('./src/LiveWall.Renderer.Video/bin/Release/net10.0-windows10.0.18362.0/LiveWall.Renderer.Video.exe')) `
  $dll `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c0/fixture-v1-20261024')) `
  ([IO.Path]::GetFullPath('./artifacts/diagnostics/video-c1/formal-unique.json')) `
  normal
```

使用真实正式可执行文件、正式 v1 Wire、固定 DLL 和合法原始 fixture。工具只创建两个**不显示的自有顶级容器**并泵送消息，不调用 Raised/Shell API、不操作桌面，不启用任何生产 allowlist。Renderer 在其中创建自己的 child/DComp target/交换链，验证 Hello → Initialize → Attach → Load → 首帧 → Pause → Bounds 当前 generation 确认 → 新 generation 暂停重附着 → Resume → Shutdown。首帧确认目标提交，不承诺屏幕扫描；该模式的 desktopPresentationValidated 始终 False。

末尾 scenario 可为 `normal`、`shutdown-after-attach`、`disconnect-after-attach`、`invalid-path`。后两项验证预期失败：必须零首帧、Renderer ExitCode=1、自有 child 清理；工具只有门禁全部满足才返回 0，不能把该返回码当作 Renderer 正常成功。路径场景还严格要求 ContentInvalidPath，不满足即保留失败结果。正常场景要求 3 次对应首帧、目标 parent/PID/client size 正确、旧 child 不复用、ShutdownCompleted 到达时 child 已销毁和 ExitCode=0。

整体 40 秒取消预算及退出额外 3 秒预算，超时仅结束自己创建的 Renderer，记录 ForcedExit，不能算通过；native 调用不合作时 CTS 本身不是硬终止。临时 HWND/PID/认证/Pipe 不进入持久结果。固定提交头文件来源与哈希见 `tools/libmpv-render-api.lock.json`。所有历史结果保持原样，具体路径/自动基线见统一台账。
