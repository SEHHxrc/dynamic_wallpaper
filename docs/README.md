# 设计文档索引

## 文档优先级

发生冲突时按以下顺序处理：

1. 根目录 [`ARCHITECTURE.md`](../ARCHITECTURE.md)；
2. 已接受的 `adr/` 决策（若改变根边界，必须同时修改根文档）；
3. 本目录专题设计；
4. 模块和子目录 `README.md`；
5. 代码注释。

低优先级文档不得暗中放宽高优先级边界。

## 专题文档

| 文档 | 说明 |
|---|---|
| [`module-boundaries.md`](module-boundaries.md) | 项目依赖、所有权和公开 API 总表 |
| [`interface-conventions.md`](interface-conventions.md) | C# 接口、错误、异步、版本规则 |
| [`renderer-protocol.md`](renderer-protocol.md) | Host ↔ Renderer 帧和消息契约 |
| [`ui-rpc.md`](ui-rpc.md) | UI ↔ Host RPC 契约 |
| [`package-format.md`](package-format.md) | 原生 `.lwpkg` 公开格式 |
| [`wallpaper-engine-compatibility.md`](wallpaper-engine-compatibility.md) | `.pkg/.mpkg` 导入边界 |
| [`security-model.md`](security-model.md) | 不可信内容、进程和 IPC 安全边界 |
| [`windows-desktop-host.md`](windows-desktop-host.md) | Windows 窗口线程、Surface 交换与 Explorer 恢复设计 |
| [`open-decisions.md`](open-decisions.md) | 尚未冻结、不得擅自假定的决策 |
| [`adr/README.md`](adr/README.md) | 架构决策工作流 |
