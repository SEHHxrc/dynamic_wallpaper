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

## 库内布局（C2.0）

```text
library/
├─ .index.json              # 唯一提交点：schemaVersion、id、version、contentHash、origin、grade、issues
├─ .staging/<随机>/          # 校验中的私有副本
├─ .trash/<随机>/            # 待删除内容
└─ <id>/<version>/          # 规范化内容，含 wallpaper.json
```

每个 id 只有一个活动版本。索引未列出的目录不可见。已提交版本不可变，仅大小写不同的版本号视为同一版本。C2.0 只接受 Video，`.lwpkg` ZIP 读取尚未实现。

## 内容哈希

按正斜杠相对路径的序数顺序，对每个文件依次写入：UTF-8 路径、一个零字节、8 字节小端长度、文件的 SHA-256 原始字节；再对整体取 SHA-256，输出大写十六进制。重解析点直接拒绝，文件数上限 10000。
