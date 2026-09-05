# ADR-005：Wallpaper Engine 仅作为导入来源

- 状态：Accepted
- 日期：2026-09-05

## 决定

Wallpaper Engine 工程、`.pkg` 和 `.mpkg` 类型只存在于 Importers；不得出现在 Video/Web Renderer 的公开接口。

## 影响

视频/Web/移动素材可高兼容导入；复杂 Scene 只能部分转换、预览降级或由用户明确选择外部委托，不承诺完整运行。

