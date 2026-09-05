# Policies

定义 `IPlaybackPolicyEvaluator`。实现必须是确定性纯函数：同一系统快照、用户策略和会话快照得到同一决策，无 I/O 和全局状态。

