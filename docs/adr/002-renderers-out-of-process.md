# ADR-002：Renderer 独立进程

- 状态：Accepted
- 日期：2026-09-05

## 决定

视频与 Web Renderer 均在 Host 外运行，经版本化 Named Pipe 协议控制，并纳入 Job Object。

## 影响

引擎崩溃与内存泄漏不会直接终止 Host；需要维护协议、握手、恢复和首帧切换逻辑。

