# Persistence

实现壁纸仓库、Assignment、Preset 和最后会话状态。首版使用 JSON/目录；实现必须位于接口后，以便后续可替换 SQLite。

`JsonWallpaperRepository`（C2.0）实现 `IWallpaperRepository`，布局见 [`docs/package-format.md`](../../../docs/package-format.md)。索引是唯一提交点；读取失败关闭，损坏索引不会被覆盖。单写者依赖 Host 单实例 Mutex。Assignment 和播放策略仍由 `Configuration/JsonHostConfigurationStore` 保存，Preset 和最后会话状态尚未实现。
