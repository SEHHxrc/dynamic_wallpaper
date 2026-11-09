# CommandLoop

实现 `Channel<HostCommand>` 单写者循环和不可变命令。只有本目录代码可改变活动 Session、Surface、Assignment 与 Generation。

C3.0 起，活动 Renderer 失败由本目录按 ARCHITECTURE §11.5 恢复：`RendererRecoveryTracker` 按“壁纸 + 显示器”在 60 秒窗口内计数，`RestartRendererCommand` 在退避后对崩溃的显示器重新走 Apply，`RendererRestartCompletedCommand` 汇报结果；第 4 次失败熔断，显式 Apply 或启动恢复清零。计时走构造时注入的 `TimeProvider`，默认系统时钟。

