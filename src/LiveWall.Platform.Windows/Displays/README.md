# Displays

通过 DisplayConfig 目标设备路径生成 SHA-256 稳定身份，并结合 `EnumDisplayMonitors` 发布包含虚拟桌面坐标、Per-Monitor DPI 和刷新率的不可变拓扑快照。相同事实不增加 revision；系统消息通过 `RequestRefresh` 进入 350 ms 防抖刷新，与根架构规定的 300–500 ms 范围一致。
