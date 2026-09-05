# LiveWall.UI

## 职责

按需运行的 WinUI 3 设置进程，展示壁纸库、导入进度、显示器分配、播放策略与诊断。用户关闭窗口后 UI 进程必须完全退出，Host 继续运行。

## 对外接口

仅通过 `LiveWall.Contracts` 与 Host 的 UI RPC 通信。允许的方法和事件见 `docs/ui-rpc.md`。ViewModel 只持有 DTO/界面状态，不持有 Domain 实体或 Renderer 会话。

## 依赖规则

只引用 Contracts。不得直接读写壁纸库目录、P/Invoke、启动 Renderer、加载 `.pkg/.mpkg` 或自行维护权威播放状态。

## 目录

| 目录 | 功能 | 边界 |
|---|---|---|
| `Pages` | 页面和导航 | 无业务 I/O |
| `ViewModels` | DTO 到展示状态映射 | 不包含 Host 规则 |
| `Controls` | 可复用视觉控件 | 不直接调用 RPC |
| `Services` | RPC Client、导航、对话框 | 只使用允许的方法 |

> WinUI 3/Windows App SDK 的包引用和 `App.xaml` 将在 UI 阶段经版本审计后加入；当前为无外部包的边界占位项目。

