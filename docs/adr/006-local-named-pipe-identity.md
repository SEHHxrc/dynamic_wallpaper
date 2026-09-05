# ADR-006：本地 Named Pipe 身份、发现与单实例

- 状态：Accepted
- 日期：2026-09-05

## 背景

UI 需要找到长期运行的 Host，Host 需要为每个 Renderer 建立独立通道。Pipe 名称必须兼容无打包开发、未来 MSIX、快速升级和多 Windows 登录会话，同时不能把远程 Pipe 或其他用户当作合法客户端。

## 决定

1. 所有 Pipe 名以 `LOCAL\` 开头。该 Windows 命名空间限定在当前登录会话，且是 MSIX 中 Named Pipe 的规定形式。
2. 名称只由小写 ASCII 组成：
   - UI RPC：`LOCAL\LiveWall.<installationScope>.p<major>.host`
   - Renderer：`LOCAL\LiveWall.<installationScope>.p<major>.renderer.<128-bit-random-hex>`
3. `installationScope` 由 Bootstrap/打包配置提供并保持稳定；正式通道使用 `stable`，预览通道使用 `preview`，本地开发使用仓库身份的稳定短哈希。不同 scope 可并存，同一 scope 不并存。
4. Host 是两类 Pipe 的服务端。服务端使用 byte mode、异步 I/O、`CurrentUserOnly`；第一实例同时使用 `FirstPipeInstance`。客户端只连接 server name `.`，并同样使用 `CurrentUserOnly`。
5. Host 单实例 Mutex 使用 `Local\LiveWall.<installationScope>.Host`，限定当前用户与当前登录会话。只有持有 Mutex 的进程可以创建稳定 UI Pipe。
6. Host 在启动 Renderer 之前创建单实例会话 Pipe，通过子进程环境传入名称和 256-bit 一次性密钥。Renderer 的第一条 `Hello` 必须回送该密钥；Host 使用固定时间比较，成功后立即从会话状态清除密钥。认证失败、超时或第二客户端连接都终止该 Renderer 会话。
7. 所有进程必须以普通用户、相同提权级别运行。产品不自动提权，也不跨 Windows 会话桥接。

`PipeOptions.CurrentUserOnly` 在 Windows 上同时验证用户账户和提权级别；`LOCAL\` 的登录会话作用域见 Microsoft 的 [Windows IPC 指南](https://learn.microsoft.com/windows/apps/develop/communication/interprocess-communication)。

## 备选方案

- **用户 SID 写入稳定 Pipe 名**：拒绝。ACL 已承担用户隔离，名称暴露 SID 没有额外安全收益。
- **随机 UI Pipe 加发现文件**：拒绝。增加发现文件的权限、原子更新和陈旧状态问题。
- **所有 Renderer 共享一个 Pipe**：拒绝。会扩大故障域，并削弱会话与 Generation 的对应关系。
- **仅依赖随机名称、不做 ACL**：拒绝。名称不是访问控制。
- **gRPC/本地 TCP**：拒绝。增加依赖和攻击面，不符合首版轻量边界。

## 影响

- 一个用户可在不同登录会话分别运行 Host；`stable` 与 `preview` 可并存。
- 同一通道的就地升级必须先停止旧 Host；破坏性协议升级可使用新的 `p<major>` 端点。
- 同一用户、同一提权级别的恶意进程仍位于既定信任边界内；Renderer 的随机名称和一次性密钥用于防止误连接与抢连，不宣称抵御已控制该用户账户的恶意软件。
- UI RPC 必须依赖方法白名单和 DTO 校验，不能把 Pipe ACL 当作业务授权。

## 迁移和回退

当前没有已发布端点，无数据迁移。若 MSIX 或受支持 Windows 版本对 `LOCAL\` 行为出现不兼容，可在保持 ACL、协议主版本和本地客户端限制的前提下新增 installation scope；不得静默回退到远程可连接或跨用户 Pipe。
