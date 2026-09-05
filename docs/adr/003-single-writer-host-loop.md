# ADR-003：Host 单写者命令循环

- 状态：Accepted
- 日期：2026-09-05

## 决定

只有 HostCommandLoop 可修改活动 Session、Surface、Assignment 和 Generation。RPC、Win32、Renderer 回调只投递不可变命令。

## 影响

并发推理和恢复行为更可测试；后台任务必须携带 Generation 回投，且不得阻塞命令循环。

