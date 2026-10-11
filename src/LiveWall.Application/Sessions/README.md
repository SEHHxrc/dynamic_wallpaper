# Sessions

定义 Renderer Provider/Session、桌面 Surface、显示器事件和进程监管端口。Platform.Windows 拥有桌面容器 Surface，Renderer 在 Host 发送的容器 HWND 内创建自有子窗口；协议和 Win32 细节由外层适配。Renderer 可在自有 child HWND 内部使用 DirectComposition/交换链而不改变 Application 端口或协议 v1；只有跨进程 Surface 对象不再是容器 HWND 时才需要新的 binding。

`IRendererProvider.CreateAsync` 的成功后置条件是 Renderer 进程已启动、一次性令牌和协议主版本/能力已验证，并已完成 wire 层 `Hello → Initialize → Initialized`；握手失败时不得返回半初始化的 `IRendererSession`。Application 从 `AttachRendererSurface` 开始驱动会话，不感知 Contracts DTO、Named Pipe 或认证密钥。首次加载必须按 `Attach → SurfaceAttached → Load → ContentLoaded → FirstFrame` 推进。活动会话的 `SetRendererBounds` 必须由 Renderer 在把既有已呈现帧适配到新 child bounds 并 Commit（或完成重绘/Present）后，以当前 Renderer generation 的 `FirstFramePresented` 确认，Host 才能扩大活动容器；这只是过渡确认，不替代新 provisional Surface 的完整首帧门禁。同一 Renderer 跨 Shell generation 重附着随后按新的 `Attach → SurfaceAttached → FirstFrame` 推进。缺失、超时、乱序或错误 generation 的中间事件不能被跳过。

`IDesktopHost` 契约规定：`CreateSurfaceAsync` 返回隐藏 provisional Surface；首帧准备完成后，由 Host 单写者调用批量 `ReplaceSurfacesAsync(SurfaceReplacement, CancellationToken)` 激活新 Surface 并隐藏旧 Surface；`ResizeSurfaceAsync` 只用于拓扑变化时让既有活动容器先适配新几何，不改变 Surface 身份、活动状态或最终提交屏障；`DestroySurfaceAsync` 幂等且由平台封送到 HWND 创建线程。后续实现不得绕过端口或重新依赖隐式 Z 顺序。真实桌面验收状态见 `docs/implementation-status.md`。

`WindowsShellSnapshot` 分开携带基础 build 和 UBR，并提供完整 build 兼容键。生产适配器不得在缺少 UBR 时按基础 build 放行；显式诊断模式可以采样未知版本，但不能修改生产 allowlist。

`DesktopTopology.Attachments` 只包含 `DesktopAttachmentCapability(AdapterId, PresentationKind, RendererBinding)`，不得包含 Progman、DefView、WorkerW 或其他 Shell HWND/PID。Shell generation 和原生层级租约由 Platform.Windows 内部持有；Application 只管理拓扑 revision 与 LiveWall 自有 `DesktopSurface.WindowHandle`。
