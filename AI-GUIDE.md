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
| 是不是重复下载了 | `scan-duplicates --json -` |
| 文件之间有没有冲突 | `scan-conflicts --json -` |
| 脚本是不是带了同名模块 | `scan-scripts --json -` |

先查重复较容易看清结果，玩家处理完重复副本可以再查冲突。显式指定目录时，查重沿用按完整文件内容匹配的方式，可用 `--extension .package --extension .ts4script` 限定文件类型。

`--json -` 的标准输出是一份 JSON，摘要和提示在标准错误。退出码 `0` 表示完成且无发现，`1` 表示有发现、未分析项或读取失败，`2` 表示参数错误或没有可扫描目录。即使退出码为 `1`，也要读完已有报告。

- `version` 是程序版本，`schemaVersion` 是输出结构版本。
- `groups` 列出结果组，`issues` 给出扫描和读取问题。文件字段 `path` 是完整路径，`sha256` 是整个文件的哈希。为 `null` 时没有可用哈希。
- `severity` 有四级：`abnormal` 异常，`risk` 有隐患，`no_effect` 不影响，`incomplete` 未分析。`certainty` 单独标出确定（`confirmed`）和可能（`possible`）。`kind` 标识结果的具体类型。
- 相同内容、已经比较出差异和未比完要分开说明。部分内容不同时仍可能有未比完的条目。覆盖时只能说游戏会使用其中一份，报告没有判定哪份会生效。
- `ignored: true` 表示玩家以前忽略过此文件。默认会显示，`--hide-ignored` 可隐藏。汇总的 `ignoredCount` 按结果中不同的文件路径计数。

## 向玩家说明结果

用人话告诉玩家查了哪些目录、确定了什么、还有什么没查完。读不了或没比完的项目要明确指出。资源内容不同意味着有覆盖差异，报告无法推断游戏会崩溃。

扫描只读。玩家明确要求整理文件时，先把涉及的路径和计划给玩家看，由玩家使用 Sieve 的桌面界面确认处理。有疑问时优先禁用，明确要求删除时才移入回收站。不要自行删除、移动或改名。

## 其他排查

目前的扫描覆盖重复文件、资源覆盖和脚本模块碰撞。游戏报错日志、Mod 是否过期、游戏版本以及完整健康检查都要另行排查。

- 原版 `lastException*.txt` 一般在系统文档下 `Electronic Arts\The Sims 4`。MCCC 的 `mc_cmd_center.log` 通常在 MCCC 的 Mod 文件夹里。WickedWhims 可能在游戏用户目录留下 `WickedWhims_Exception.txt`。优先看最新一段异常的类型、模块和堆栈。[MCCC 排查说明](https://deaderpool-mccc.com/troubleshooting) · [WickedWhims 支持说明](https://wickedwhims.squarespace.com/exception/)
- 游戏更新后，可以从 Mod 作者的发布页核对脚本版本。也可以查看脚本是否放得太深，是否仍在未解压的外层压缩包里，以及是否支持玩家的游戏版本。
- 日志点名的 Mod 可能只是在异常发生时被调用。提出怀疑时说明依据和把握，文件操作仍由玩家确认。
