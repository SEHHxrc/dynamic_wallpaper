# `.lwpkg` 原生包格式 v1

`.lwpkg` 是可检查的普通 ZIP 容器。包是不可变输入；用户属性、Assignment 和运行状态保存在 AppData，不回写包。

## 目录

```text
wallpaper.lwpkg
├─ wallpaper.json
├─ properties.schema.json       # 可选
├─ preview.webp
├─ license.txt                  # 可选
├─ content/                     # 视频/图片
├─ web/                         # Web 工程
└─ compatibility/
   └─ import-report.json        # 外部导入时生成
```

`wallpaper.json` 必须通过 `packages/schemas/wallpaper.schema.json`。入口按 kind 限制：Video/Image 指向 `content/`，Web 指向 `web/`。v1 不允许 Scene 和 External 作为可直接运行内容。

## 导入事务

1. 在随机 staging 目录展开；
2. 验证 ZIP 路径、数量、大小和压缩比；
3. 校验 manifest、入口、权限和内容哈希；
4. 生成/验证预览及兼容性报告；
5. 按 SHA-256 去重；
6. 原子移动到 `library/<id>/<version>`；
7. 最后更新仓库索引。

任何一步失败都不得留下可见的半导入条目。

