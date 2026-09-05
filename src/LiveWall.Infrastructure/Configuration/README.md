# Configuration

读写带 `schemaVersion` 的设置、播放策略与迁移。迁移失败保留原文件并回退安全默认，不覆盖唯一可恢复副本。

`JsonHostConfigurationStore` 分别管理 `playback-policy.json` 与 `assignments.json`。缺失文件返回默认值；畸形 JSON、未知 Schema 和无效值返回明确错误码且不改写原文件。
