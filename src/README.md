# Product source modules

产品代码按依赖方向分为内核、端口实现、组合根和独立进程。允许引用关系以 `docs/module-boundaries.md` 为准；每个模块根 `README.md` 定义其公开表面，每个子目录 `README.md` 定义局部职责。

禁止在 `src` 下建立绕过既有分层的 `Common`、`Shared`、`Utils` 总包。确有跨模块需求时，先确定语义属于 Domain、Contracts 还是 Application 端口。

