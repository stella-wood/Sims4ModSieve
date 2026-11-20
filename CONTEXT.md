# 词汇表

Sieve 里反复出现、又容易被理解歪的词。写代码、写文档、写 issue 都用这里的叫法；需要新词时先加到这里，不要自己另起名字。

「代码里」一栏是对应的类型或字段，方便搜索。

## 扫描范围

| 词 | 意思 | 代码里 |
|---|---|---|
| 来源 | 玩家让 Sieve 扫的一个文件夹。一次扫描可以有多个来源 | `ScanSource`、`--source` |
| 默认 Mods | 设置里保存的 Mods 文件夹，没指定来源时就扫它 | `ModsRoot` |
| Tray | 游戏存放家庭、房屋的文件夹，跟 Mods 分开保存 | `TrayRoot` |
| 文件发现 | 把来源规范化、去掉重叠目录、跳过程序自身文件，列出要扫的文件。三种扫描共用 | `SourceFileDiscovery` |
| 程序目录保护 | 程序放进 Mods 里时，扫描避开程序自身的文件 | `ApplicationDirectoryGuard` |

## 扫描性能

| 词 | 意思 | 代码里 |
|---|---|---|
| 扫描选项快照 | 开始扫描时固定的并发档位和本次存储画像，供索引、内容阶段共用 | `ConflictScanOptions` |
| 并发决定 | 某阶段的请求档位、策略上限、采用上限、存储分类和回退原因；采用上限不是活跃任务峰值 | `ScanConcurrencyDecision` |

## 文件身份

| 词 | 意思 | 代码里 |
|---|---|---|
| 文件戳 | 扫描时记下的大小、修改时间、卷序列号和文件 ID。动文件前重新读一遍比对 | `FileStamp` |
| 整文件哈希 | 整个文件内容的 SHA-256，内容一样哈希就一样 | `sha256` |

## 结果

| 词 | 意思 | 代码里 |
|---|---|---|
| 重复 | 内容完全一样的几个文件（整文件哈希相同） | `duplicate` |
| 建议保留 | 一组重复里默认留下的那份，其余默认勾选 | `DuplicateSelectionService` |
| TGI / 资源编号 | package 里每个资源的编号：类型、组、实例三段。两个 package 有相同 TGI，游戏只会用其中一个 | `ResourceKey` |
| 候选冲突 | 被不止一个 package 共有的 TGI，还没比内容 | `Candidates` |
| 资源覆盖冲突 | 两个 package 有相同 TGI 且内容不同，后加载的那个会盖掉前一个 | `resource_conflict`、`OverrideConflict` |
| 疑似同一 mod | 两个文件互相覆盖的资源多到超过门槛，多半是同一个 mod 的新旧版本 | `suspected_same_mod`、`SuspectedSameMod` |
| 合集包含 | 一个大 package（合集）里完整包含了另一个文件的资源 | `bundle_containment`、`BundleContainment` |
| 脚本撞名 | 两个 `.ts4script` 里有同名的 Python 模块 | `script_collision`、`script_duplicate`（内容相同时） |
| 未分析 | 文件读不了、结构坏了或压缩方式不支持，这次没法判断。不等于没问题 | `incomplete`、`read_incomplete` |
| 评级 | 每条结果的轻重：异常、有隐患、不影响、未分析。另有把握：确定、可能 | `severity`、`certainty` |
| v2 文件关系分组 | 把冲突按「两个文件之间是什么关系」分组的规则，见[分组方案 v2](docs/design/冲突检测分组方案-v2.md) | `PackageConflictGroupingBuilder` |
| 结果会话 | 一次扫描的结果，只留在这次运行里，切页面保留，重启就没了 | `DuplicateResultSession`、`ConflictResultSession` |

## 文件操作

| 词 | 意思 | 代码里 |
|---|---|---|
| 禁用 | 文件改名加 `.off`，游戏不再读它；可以恢复 | `ModDisableService` |
| 删除 | 移进 Windows 回收站，不直接删；可以撤回 | `FileActionService` |
| 忽略 | 不改文件，只记下来，以后结果里标成已忽略 | `IgnoredFileRecord` |
| 整组复核 | 冲突页所有卡片与重复页在删除或禁用前，检查所选文件所在完整卡片或组的成员是否存在；缺失时提示刷新结果和选择，不读成员内容。见[性能与缓存架构](docs/architecture/性能优化与缓存架构.md#7-删除禁用前的结果成员存在检查) | — |
