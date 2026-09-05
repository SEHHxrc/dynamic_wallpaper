# Tests

测试项目按被验证的架构边界拆分。Domain、Application、Package 与 Renderer Contract 测试工程在 Foundation 阶段建立；Windows Integration 工程已随 Desktop Host 阶段启用。其余测试工程随对应产品阶段加入，测试依赖不得进入产品项目。

| 目录 | 验证对象 |
|---|---|
| `LiveWall.Domain.Tests` | 布局、策略、Generation 和领域不变量 |
| `LiveWall.Application.Tests` | 用例、Provider 选择、状态编排和 Fake 端口 |
| `LiveWall.Package.Tests` | manifest、原子提交、恶意 ZIP 与路径安全 |
| `LiveWall.WallpaperEngine.Tests` | pkg/mpkg/tex 探测、边界、兼容等级和 fuzz 回归 |
| `LiveWall.Renderer.ContractTests` | 帧、版本握手、未知字段、幂等和畸形消息 |
| `LiveWall.Windows.IntegrationTests` | Windows 纯映射/生命周期测试，以及真实桌面会话、DPI、多屏、Explorer、RDP 与电源 |
