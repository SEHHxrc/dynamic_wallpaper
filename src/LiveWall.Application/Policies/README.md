# Policies

定义 `IPlaybackPolicyEvaluator` 和 `ISystemStateSource`。策略实现必须是确定性纯函数：同一系统快照、用户策略和会话快照得到同一决策，无 I/O 和全局状态。系统状态源每次发布完整 `SystemStateSnapshot`、单调 revision、强类型 reason 以及是否必须先完成环境验证；Application 不理解 WTS、PowerBroadcast 或 HWND。

