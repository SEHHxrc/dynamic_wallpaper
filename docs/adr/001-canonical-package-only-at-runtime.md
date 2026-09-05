# ADR-001：运行时仅消费规范化内容

- 状态：Accepted
- 日期：2026-09-05

## 决定

Renderer 只接收已验证的 `WallpaperDefinition + CanonicalContentRoot`，不直接打开 `.lwpkg/.pkg/.mpkg`。

## 影响

导入必须先完成安全展开、转换、兼容性报告和原子提交。代价是占用额外磁盘，但运行时更小、更稳定，外部格式变化不会污染 Renderer。

