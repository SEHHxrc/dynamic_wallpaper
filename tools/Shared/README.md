# Shared diagnostic contracts

仅存放多个本地诊断工具共同编译的测试契约。当前 `ProbeFaultPlan` 只通过目标 `RendererChildProbe` 子进程的环境变量传递故障计划；它不属于 Renderer Protocol、Application 端口、生产配置或持久化 Schema。

生产 Host 和 Renderer 不得依赖此目录。故障环境变量必须由诊断 composition root 定点注入，子进程读取后立即清除；未注入计划时测试 Renderer 的协议和呈现行为必须保持不变。
