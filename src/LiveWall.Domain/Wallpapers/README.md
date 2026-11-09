# Wallpapers

定义壁纸身份、类型、元数据、能力和来源。公开 `WallpaperDefinition` 等不可变领域值；不读取 manifest、不验证磁盘路径、不包含 Renderer 细节。

`NativeVideoFormats` 列出原生视频入口允许的扩展名；Video Renderer 自己保留一份同样的集合，由契约测试保持一致。`LocalVideoIdentity` 定义本地视频的内容寻址 id（`local.video.<SHA-256>`）和入口形状，只做字符串规则，不读文件。
