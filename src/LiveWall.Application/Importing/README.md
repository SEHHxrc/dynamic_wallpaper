# Importing

定义 Probe、Import、包索引、安全提取和内容转换端口。Probe 必须轻量；Import 必须可取消、报告进度并显式返回兼容性损失。

导入器返回 `StagedWallpaper`：已写入 staging 的规范化内容，尚未固定哈希。Host 计算内容哈希后组成 `ImportedWallpaper` 交给仓库，仓库在私有副本上复算并校验。
