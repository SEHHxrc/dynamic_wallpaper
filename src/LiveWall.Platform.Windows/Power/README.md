# Power

`WindowsSystemStateSource` 聚合隐藏窗口收到的 WTS、console display state、系统 suspend/resume 与 AC/DC 广播，并输出完整事实快照、单调 revision 和变化原因。锁屏/显示关闭/系统 suspend 立即形成非活动快照；解锁、点亮与唤醒先形成 `RequiresEnvironmentValidation` 请求，只有事件桥重新采样显示拓扑并确认 Shell 可用后才发布活动快照。平台回调不得直接操作 Renderer、Surface 或 Host 状态。

