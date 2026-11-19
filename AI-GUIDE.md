# 用 Sims 4 Mod Sieve 帮玩家查 Mod

Sims 4 Mod Sieve 可以查找重复文件、package 资源覆盖差异和脚本模块碰撞。安装版默认把 `Sims4ModSieve.Cli.exe` 放在 `%LOCALAPPDATA%\Programs\Sims4ModSieve`。玩家可以更改安装位置。便携版在玩家解压的目录里。找不到程序时请向玩家确认路径。先运行 `Sims4ModSieve.Cli.exe --help` 确认能执行。

## 找到本次要查的文件夹

1. 运行 `config show`，查看已保存的默认 Mods 文件夹和它是否存在。
2. 默认 Mods 没设好时运行 `find-mods`，它会查看系统的文档文件夹。
3. 仍找不到就问玩家。玩家同意保存默认目录后，运行 `config set-mods "<目录>"`。
4. 临时扫别处用 `--source "<目录>"`。多扫一个目录就再写一次 `--source`。

报告中的 `sources` 列出实际可扫描的目录，`sourceOrigin` 表示显式参数（`argument`）或默认范围（`settings`）。从默认范围扫描时，向玩家确认文件夹。同一次对话里同一批目录确认一次就够。

## 选择扫描命令

| 玩家想查什么 | 命令 |
| --- | --- |
| 是不是重复下载了 | `scan-duplicates --json "<报告文件路径>"` |
| 文件之间有没有冲突 | `scan-conflicts --json "<报告文件路径>"` |
| 脚本是不是带了同名模块 | `scan-scripts --json "<报告文件路径>"` |

先查重复较容易看清结果，玩家处理完重复副本可以再查冲突。显式指定目录时，查重沿用按完整文件内容匹配的方式，可用 `--extension .package --extension .ts4script` 限定文件类型。

默认用 `--json "<报告文件路径>"` 写入文件，选择扫描目录之外、尚不存在的报告路径。先读标准错误里的摘要：扫描目录、文件数、完整组数、各级数量和读取问题数；再按需要分段读取 JSON 文件。不要把整份大报告一次打印到工具输出，也不要把工具截断后的一段当成完整结果。例如用 PowerShell 读取前 80 行：

```powershell
Get-Content -LiteralPath "<报告文件路径>" -Encoding utf8 -TotalCount 80
```

继续读取时使用 `Get-Content -LiteralPath "<报告文件路径>" -Encoding utf8 | Select-Object -Skip 80 -First 80`，按需要递增 `-Skip`。这是文本片段，可能从某个对象中间开始；判断完整性要结合顶层汇总和后面的明细，不能只看第一段。

`--json -` 只留给已知组少、明细较短，或用户明确要求直接查看的场合。这时标准输出只有 JSON，摘要和提示仍在标准错误。不加 `--json` 时，CLI 照旧在当前目录写报告并在标准输出显示摘要。

退出码 `0` 表示完成且无发现，`1` 表示有发现、未分析项或读取失败，`2` 表示参数错误或没有可扫描目录。即使退出码为 `1`，也要查看已有报告；筛选掉全部明细后，退出码也不会变成 `0`。

- `version` 是程序版本，`schemaVersion` 是输出结构版本。
- `groupCount` 是完整结果的组数，`visibleGroupCount` 是这份 JSON 列出的组数。`severityCounts` 按完整结果统计四级数量（结果组与读取问题合计），`issueCount` 是完整的扫描和读取问题数。已有的 `abnormalCount` 是异常组与异常读取问题合计，`incompleteCount` 只统计未分析的读取问题。
- `groups` 列出结果组，`issues` 给出扫描和读取问题。文件字段 `path` 是完整路径，`sha256` 是整个文件的哈希。为 `null` 时没有可用哈希。
- `scan-conflicts` 在分组规模超限时不生成部分组，`complete=false`、退出码为 `1`。扫描问题 `code=conflict-grouping-too-large` 的 `reason` 为 `duplicate-copies`（建议先重复检测）或 `too-many-shared-resources`（建议分批检测），附 `resourceMatchCount`、`collapsedResourceMatchCount`、`resourceMatchLimit`，副本原因另附多余副本数 `redundantCopyCount`。该问题的 `kind=read_incomplete`、`severity=incomplete`，沿用下文筛选及省略计数规则；中文提示与两个估算值、上限始终写入标准错误。估算规则见[分组规模限制](docs/design/冲突检测分组方案-v2.md#44-性能)。
- `severity` 有四级：`abnormal` 异常，`risk` 有隐患，`no_effect` 不影响，`incomplete` 未分析。`certainty` 单独标出确定（`confirmed`）和可能（`possible`）。`kind` 标识结果的具体类型。
- 相同内容、已经比较出差异和未比完要分开说明。部分内容不同时仍可能有未比完的条目。覆盖时只能说游戏会使用其中一份，报告没有判定哪份会生效。
- `ignored: true` 表示玩家以前忽略过此文件。默认会显示，`--hide-ignored` 可隐藏。汇总的 `ignoredCount` 按结果中不同的文件路径计数。

## 按需缩小报告明细

三个扫描命令支持以下参数。只缩小输出，扫描、完整汇总和退出码不变。

| 参数 | 规则 |
| --- | --- |
| `--severity <级别>` | 可重复，值为 `abnormal`、`risk`、`no_effect`、`incomplete`；同一参数多个值取并集 |
| `--kind <种类>` | 可重复，取值见下表；与 severity 同用时取交集 |
| `--limit <N>` | 筛选后按原结果顺序最多列 N 组，N 是大于或等于 0 的整数；不限制 `issues` |
| `--omit-resource-keys` | 仅用于 `scan-conflicts`；省略 package 组的 `resourceKeys` 数组，保留 `resourceKeyCount` |
| `--summary-only` | 只要汇总，`groups` 和 `issues` 均为空 |

`--severity` 和 `--kind` 同时筛选 `groups` 与 `issues`。可用 kind：

| 命令 | kind |
| --- | --- |
| `scan-duplicates` | `duplicate`、`read_incomplete` |
| `scan-scripts` | `script_collision`、`script_duplicate`、`damaged_file`、`read_incomplete` |
| `scan-conflicts` | 上述全部，加 `resource_conflict`、`bundle_containment`、`suspected_same_mod` |

无效值、其他命令不适用的 kind，以及在查重或脚本命令上使用 `--omit-resource-keys` 都是参数错误（退出码 `2`）。例如：

```powershell
Sims4ModSieve.Cli.exe scan-conflicts --source "D:\Mods" --json "conflicts-summary.json" --summary-only
Sims4ModSieve.Cli.exe scan-conflicts --source "D:\Mods" --json "conflicts-abnormal.json" --severity abnormal --limit 20 --omit-resource-keys
```

JSON 顶层始终带 `complete`、`omittedGroupCount`、`omittedIssueCount` 和 `filters`：

- 使用任何输出筛选、`--limit`、资源键省略、只要汇总或已有的 `--hide-ignored` 时，`complete` 为 `false`，即使这次没有实际省略组。
- `omittedGroupCount` 是完整组数减已列组数；`omittedIssueCount` 是完整读取问题数减已列问题数。隐藏组里的部分文件、或省略资源键时，这两个数也可能都是 `0`。标准错误的摘要会写「还有 N 组未列出」，资源键省略时另有提示。
- `filters` 记录实际生效的 `severity`、`kind` 数组（去重），以及 `limit`、`hideIgnored`、`omitResourceKeys`、`summaryOnly`。未指定的数组为空，`limit` 为 `null`，开关为 `false`。
- `complete: true` 只表示报告输出了全部现有结果，不表示每个文件都成功读完；仍要看 `issues` 和未分析数量。

查重的 HTML 不支持这些新筛选参数：显式 `--html` 与它们同用时报参数错误，也不会自动生成 HTML。需要 HTML 时另行运行不带新筛选参数的查重命令；已有的 `--hide-ignored` 对 HTML 的行为保持不变。

## 向玩家说明结果

用人话告诉玩家查了哪些目录、确定了什么、还有什么没查完。读不了或没比完的项目要明确指出。资源内容不同意味着有覆盖差异，报告无法推断游戏会崩溃。

扫描只读。玩家明确要求整理文件时，先把涉及的路径和计划给玩家看，由玩家使用 Sieve 的桌面界面确认处理。有疑问时优先禁用，明确要求删除时才移入回收站。不要自行删除、移动或改名。

## 其他排查

目前的扫描覆盖重复文件、资源覆盖和脚本模块碰撞。游戏报错日志、Mod 是否过期、游戏版本以及完整健康检查都要另行排查。

- 原版 `lastException*.txt` 一般在系统文档下 `Electronic Arts\The Sims 4`。MCCC 的 `mc_cmd_center.log` 通常在 MCCC 的 Mod 文件夹里。WickedWhims 可能在游戏用户目录留下 `WickedWhims_Exception.txt`。优先看最新一段异常的类型、模块和堆栈。[MCCC 排查说明](https://deaderpool-mccc.com/troubleshooting) · [WickedWhims 支持说明](https://wickedwhims.squarespace.com/exception/)
- 游戏更新后，可以从 Mod 作者的发布页核对脚本版本。也可以查看脚本是否放得太深，是否仍在未解压的外层压缩包里，以及是否支持玩家的游戏版本。
- 日志点名的 Mod 可能只是在异常发生时被调用。提出怀疑时说明依据和把握，文件操作仍由玩家确认。
