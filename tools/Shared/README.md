# Shared diagnostic contracts

仅存放多个本地诊断工具共同编译的测试契约。当前 `ProbeFaultPlan` 只通过目标 `RendererChildProbe` 子进程的环境变量传递故障计划；它不属于 Renderer Protocol、Application 端口、生产配置或持久化 Schema。

生产 Host 和 Renderer 不得依赖此目录。故障环境变量必须由诊断 composition root 定点注入，子进程读取后立即清除；未注入计划时测试 Renderer 的协议和呈现行为必须保持不变。

`ShellMutationAssessment` 是 C1.2 工具/测试共用的只读三基线分类器：临时比较 Shell generation、WorkerW 身份和结构，输出 NotRequired/Recovered/CleanupIncomplete/Inconclusive 及数量，不持久化句柄，不操作或“恢复”Shell。LiveWall 自有资源清理不能代替该结果。

`VideoCandidateCleanupAssessment` 分开自有资源移除与正常退休：全部资源释放、无自有窗口且每个实际进程已退出，才能认定自有清理完成；正常退休还需要 ShutdownCompleted 及所有实际退出码为 0。异常退出不允许通过候选验收，也不影响 Shell mutation 的独立分类。
