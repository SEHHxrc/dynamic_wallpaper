# Architecture Decision Records

ADR 用于记录会影响多个模块、公开协议、安全模型或长期维护成本的决定。

状态使用 `Proposed / Accepted / Superseded / Rejected`。新 ADR 必须包含：背景、决定、备选方案、影响、迁移和回退。若决定改变根架构边界，ADR 与 `ARCHITECTURE.md` 必须在同一变更中更新。

当前已接受 ADR：

- `001` 运行时仅消费规范化内容
- `002` Renderer 独立进程
- `003` Host 单写者命令循环
- `004` 桌面附着使用版本化适配器
- `005` Wallpaper Engine 仅作为导入来源
- `006` 本地 Named Pipe 身份、发现与单实例
- `007` Desktop Surface 窗口线程、显式交换与 Shell 恢复
- `008` 桌面结构候选、可呈现附着能力与 Surface Binding 分离
