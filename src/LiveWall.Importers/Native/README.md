# Native

探测并导入原生目录或 `.lwpkg`，验证 manifest、入口、权限与哈希。输出 Native 等级的规范化导入结果。

`LocalVideoImporter`（C2.1）把单个本地视频包装成原生包，规则见 [`docs/package-format.md`](../../../docs/package-format.md) 的“本地视频导入”。它只写入调用方提供的空 staging 目录，不接触正式 library；原因码见 `LocalVideoImportDiagnostics`。原生目录和 `.lwpkg` 导入尚未实现。
