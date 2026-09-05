# 安全模型

所有壁纸、压缩包、Web 文件、manifest、属性和跨进程消息均视为不可信输入。

## 信任边界

| 边界 | 必须措施 |
|---|---|
| UI → Host | `LOCAL\` 会话作用域、当前用户 Pipe ACL、方法白名单、DTO 长度/枚举校验 |
| Host → Renderer | 随机会话 Pipe、一次性密钥、协议/能力握手、Generation、Job Object |
| Package → Importer | 魔数、边界、路径、条目/大小/深度限制、隔离 staging |
| Web content → Web Renderer | 默认离线、虚拟 Host、postMessage Schema、禁下载/新窗口 |
| Native library → Renderer | 入口必须在验证内容根内，只读消费 |

## 永久禁止

- 执行壁纸携带的 EXE、DLL、PowerShell、批处理或任意 shell 命令；
- 路径穿越、UNC/设备路径、符号链接/重解析点逃逸；
- 未授权联网、音频捕获、任意文件访问或 RGB 硬件控制；
- UI 构造 Renderer 启动参数；
- 远程 Named Pipe 客户端；
- 在日志中写入原始音频、浏览历史、密钥或敏感 URL 参数。

安全边界的任何放宽属于架构变更，必须先 ADR 和威胁模型评审。
