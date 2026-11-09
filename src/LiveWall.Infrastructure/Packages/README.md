# Packages

实现公开 `.lwpkg` ZIP、manifest Schema 校验和规范化内容读取。仅处理原生包，不解析 Wallpaper Engine 私有格式。

已实现：`WallpaperManifestReader`（manifest v1 严格读取）和 `WallpaperContentTree`（重解析点拒绝、复制、内容哈希）。`.lwpkg` ZIP 读取尚未实现。

`WallpaperContentTree.ComputeDigestAsync` 一次遍历同时给出树哈希和每个文件的 SHA-256，供仓库校验保留 id。
