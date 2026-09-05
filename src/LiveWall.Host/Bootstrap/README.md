# Bootstrap

唯一组合根：单实例 Mutex、配置加载、日志、依赖注册、恢复和关闭顺序。其他项目不得自建全局容器或 Service Locator。

`HostBootstrapper` 已固定可测试的启动顺序：加载配置、应用播放策略、发布显示拓扑、以单一 Generation 恢复当前已连接显示器的分配；关闭前通过同一配置端口持久化策略与分配。可执行程序的真实 Windows 服务装配将在平台适配器可用后接入。
