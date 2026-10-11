# Bootstrap

唯一组合根：单实例 Mutex、配置加载、日志、依赖注册、恢复和关闭顺序。其他项目不得自建全局容器或 Service Locator。

P1.9.0 已由 `Program → WindowsHostCompositionRoot → WindowsHostRuntime` 接通真实 Windows 进程入口。同步 `Main` 在同一线程取得并释放当前用户/Session 的 `HostInstanceMutex`；运行时按 `Created → Starting → Restoring → Running → Stopping → Stopped` 管理事件桥、命令循环、配置和平台资源。启动恢复会等待 Apply generation 明确终态，再启动事件桥并补采环境；失败恢复仍保留完整持久 Assignment。关停先停止环境事件，再通过单写者执行幂等 Shutdown、等待 Session retirement、持久化状态并按反序释放资源。`--validate-startup` 只用于组合根生命周期烟雾测试。

当前生产壁纸仓库以及 Video/Web Renderer 产品入口仍未实现，默认组合根因此使用明确的 unavailable repository 与空 Renderer 集合：已有 Assignment 可以加载并保留，但内容恢复会强类型失败关闭，Host 仍可进入无活动壁纸的 `Running`。不得把这一状态写成正式产品内容恢复完成。

P1.9.1 的事件桥通过内部 `IExplorerChangeMonitor` 接收 `TaskbarCreated` 验证结果，并分别记录 Shell 验证失败、恢复命令转发失败和正常 `HostStopping` 丢弃。诊断组合根可以显式注入合成仓库、测试 Renderer 和未验证 Raised policy，但默认 `Program` 不接受这些实验参数，也不会把 override 写入配置或 allowlist。真实双屏验证确认无变化 burst 不改变 generation/Session，而 Explorer 重建只投递一次恢复；本次安全路径整批 `Rebuilt` 成功。

P1.9.2 继续复用同一隐藏窗口和事件桥接收 WTS/Power 广播。Bridge 只把带 revision 的完整系统快照投递给 Host 单写者，并保留最多 64 项诊断 journal；解锁、显示点亮和睡眠恢复会同步重采样拓扑、请求 Explorer 验证，验证成功前不会发布活动快照。Stopping 后的系统事件按正常丢弃分类，不会重新激活 Renderer。
