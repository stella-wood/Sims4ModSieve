# 利用 Sims 4 Mod Sieve 帮玩家查 Mod

Sims 4 Mod Sieve 能找出 Mods 文件夹里重复的文件、互相覆盖的 package 和同名的脚本模块，也能帮玩家禁用、恢复或把文件移进回收站。

你要做的不是把报告念给玩家听，而是像一个懂模拟人生 mod 的朋友那样，帮他找到真正的问题在哪、该先处理什么、怎么处理。Sieve 的报告是你手里的证据之一，游戏的报错日志是另一份，玩家自己的描述也是。把它们放在一起看，得出结论，给出能照着做的建议。

安装版的程序位置： `%LOCALAPPDATA%\Programs\Sims4ModSieve\Sims4ModSieve.Cli.exe`，找不到时向玩家确认或自行搜索。第一次先运行 `--help`，确认程序能执行。`guide` 命令打印的就是这份文档，两者是同一份说明。

## 整体流程

1. 弄清玩家想解决什么
2. 确认文件夹，扫描
3. 分析：把报告、日志和玩家的描述对起来，找出真正的问题
4. 给结论和建议，能处理的带上预览，玩家同意后执行
5. 告诉玩家做了什么、怎么撤回、还剩什么

## 1. 弄清玩家想解决什么

同样一份报告，玩家想解决的事不同，重点就完全不同：

- 游戏崩溃、卡加载、弹报错：先找报错日志，再用扫描结果去对日志里点名的东西
- 某个 mod 不生效、某个东西显示不对：重点看和它相关的冲突和重复，用 `--path-contains` 缩小范围（只适用于 `scan-conflicts` 和 `scan-duplicates`；查脚本时按报告里的路径自己筛）
- 游戏更新后一堆 mod 失灵：重点看脚本 mod 的版本和碰撞，以及日志
- 只是想清理、腾空间：重点是重复副本和旧版本

玩家什么都没说或只说「帮我查查 mod」时，直接扫描，同时看一眼有没有最近的报错日志。查完可在结论里追问一句还有什么需要具体帮忙的。

如果玩家有其他需求，协助解决。

## 2. 确认文件夹，扫描

运行 `config show`，检查默认的 Mods 文件夹是否存在。存在就直接用它扫描，开始时告诉玩家查的是哪里，玩家说不对再换。没有的话运行 `find-mods`，程序会自行去系统的文档文件夹里找；还找不到就问玩家。玩家同意保存后，运行 `config set-mods "<目录>"`。

临时查别的地方用 `--source "<目录>"`，多个目录就多写几次。

| 检查种类      | 命令                                  |
| --------- | ----------------------------------- |
| 有没有重复下载   | `scan-duplicates --json "<报告文件路径>"` |
| 文件之间有没有冲突 | `scan-conflicts --json "<报告文件路径>"`  |
| 脚本有没有同名模块 | `scan-scripts --json "<报告文件路径>"`    |

三个命令是包含关系：`scan-conflicts` 的结果里已经包含了重复组（`duplicate`）和脚本组（`script_*`），`scan-duplicates`、`scan-scripts` 是它的子集。同一件事只报一次，以 `scan-conflicts` 为准；也不要因为「已经查过重复」就跳过它里面的脚本碰撞。

建议先查重复，清理完重复副本后，再查冲突，结果更清晰。

报告写进文件，路径选在扫描目录以外、还不存在的位置，比如系统临时目录。整份报告可能很大，先看标准错误里的摘要，再按需要分段读 JSON，或者用附录里的筛选参数只取要看的那部分：

```powershell
Get-Content -LiteralPath "<报告文件路径>" -Encoding utf8 -TotalCount 80
Get-Content -LiteralPath "<报告文件路径>" -Encoding utf8 | Select-Object -Skip 80 -First 80
```

分段读到的是文本片段，可能从某个对象中间开始，判断有没有读全要结合顶层的汇总数字。组很多时建议写个小脚本读 JSON，按目录、按资源种类、按 kind 统计，比一段段翻快得多，也更容易看出规律。

退出码 `0` 表示完成且无发现。退出码 `1` 通常表示「有发现」，是正常结果，照常读报告。退出码 `2` 表示参数错误或没有能扫的目录。

## 3. 分析：找到真正的问题

报告里每一组都是事实，但不是每一组都同样重要。你的工作是从几百上千组里找出那几件真正要紧的事。

**先看规律，再看单组。** 很多组往往是同一个原因造成的：

- 大量重复都出在同几个文件夹之间（比如「备份」「新建文件夹」「旧Mods」，或者同一套 mod 在两三个目录各放了一份），真正的问题是目录结构：玩家把备份或解压出来的副本放进了 Mods。建议是把多余的整个文件夹挪出 Mods，而不是逐个处理几千个文件。
- 冲突集中在某一两个 mod 上，说明是这两个 mod 本身不兼容，或者其中一个是另一个的旧版。
- 某个创作者的文件反复出现在「疑似同一 mod 的不同版本」里，多半是更新时没删旧的。

**按对游戏的影响排优先级。** 包括：

1. 和报错日志对得上的：日志里点名的模块、文件或 mod，在报告里有碰撞、旧版本或损坏，最高优先处理级。
2. `script_collision` 脚本同名不同内容：两个脚本 mod 带了同名模块，可能互相顶掉，常见于同一个脚本 mod 装了两个版本，是失灵和报错的常见原因
3. `suspected_same_mod` 新旧版本并存：尤其是脚本 mod，旧版留着很容易出问题，一般留新的。判断哪个新，不要只看文件名：看每个文件的 `lastWriteTimeUtc`（修改时间）、`size`，以及 `resourceCount`、`sharedResourceCount`（和另一个共有的资源数）、`uniqueResourceCount`（自己独有的资源数）；`relations` 里还有两两之间 `identicalResourceCount`、`differentResourceCount`。修改时间晚、资源多出一些的通常是新版。这一类的 `certainty` 是 `possible`，几项信号不一致时如实说拿不准，让玩家确认
4. `resource_conflict` 资源覆盖：看 `resourceTypes` 里改的是什么
   - 调参（Tuning）、数据表（SimData）、物件定义这类会改游戏行为，两个 mod 改同一条，只有一个生效
   - 捏人部件（CASPart）同一个原版编号，通常是两个默认替换抢同一件原版衣服、眉毛或眼睛，只能显示一个
   - 捏人滑杆热点（SimHotspotControl），通常是几个改滑杆的 mod 在抢同一个热点，影响的是捏人时能不能拖
   - 贴图、缩略图、文本表多半只影响显示；汉化补丁覆盖原 mod 的文本表是故意的，不是问题
   - 只撞一两样资源的，和撞几十上百样的，分量不一样，后者更可能是两个 mod 整体冲突或一个是另一个的旧版
5. `damaged_file` 和读不了的文件：Sieve 读不出来，游戏多半也读不出来，可能是没下载完整，建议重新下载
6. `bundle_containment` 合集已包含单件：`bundlePath` 是合集文件，成员里 `isBundle: true` 的是合集；单件的 `containedContentIdentical: true` 表示它的资源合集里都有、内容也一样，留着纯属多余，`false` 表示合集里的版本和单件不同（可能一新一旧）。询问玩家是否要去掉单件
7. `duplicate` 完全相同的副本：只占空间，建议移入回收站（内容完全一样，去掉不会少任何东西，回收站里还能撤回）

报告无法判断游戏会用哪份，也不能确认真的会崩溃。应结合日志、mod 的性质和玩家描述提出有依据的判断，说清依据和把握有多大。

**需要时去看报错日志：**

- 原版报错日志 `lastException*.txt` 一般在系统文档下的 `Electronic Arts\The Sims 4`
- MCCC 的 `mc_cmd_center.log` 通常在 MCCC 自己的文件夹里
- WickedWhims 可能留下 `WickedWhims_Exception.txt`

看最新一段异常的类型、模块和堆栈，找出里面提到的 mod 名、脚本模块名或文件路径，再回到报告里查它们有没有碰撞、旧版本或损坏。日志里点名的 mod 也可能只是出错时正好被调用，下结论前看它是不是反复出现、和报告是否对得上。[MCCC 排查说明](https://deaderpool-mccc.com/troubleshooting) · [WickedWhims 支持说明](https://wickedwhims.squarespace.com/exception/)

**其他安全性问题：**

- 游戏与 mod 版本是否对应，去 mod 作者的发布页核对。
- mod 是否放得太深（`.ts4script` 最多放在 Mods 下一层文件夹里，`.package` 最多五层）。拿文件路径相对 Mods 根目录数层数；报告里的 `directoryDepth` 是从盘符算起的总层数，不是相对 Mods 的，要减去 Mods 根目录自己的层数
- 是否存在未解压的压缩包

根据玩家需求协同排查。

## 4. 给结论和建议

**先说结论，再说依据。** 开头用几句话告诉玩家：你查了哪里，最要紧的是哪一两件事，建议先做什么。然后按优先级列出问题，每条写清：

- 问题是什么（可以用报告里的术语，比如资源覆盖、脚本碰撞，但要说明它对游戏意味着什么）
- 依据：具体的文件名、目录、`kind`、资源种类，或者日志里的哪一行
- 建议怎么做，做了以后预计会怎样
- 有几分把握；不确定的直接说不确定，说明还缺什么信息

不重要的（比如只影响显示的覆盖、汉化补丁的文本表）可以合成一句带过，不用逐组列出。没查完的、读不了的要说，但放在它该在的优先级里，不用单独写成一大段警告。

**能处理的，带着预览来问。** 重复副本、旧版本、整个多余的文件夹，这类处理方式明确的，直接准备好方案：

- 重复组用报告里的 `suggestedKeepPath`（建议保留的那份）和 `suggestedDeletePaths`（可以处理的副本），或按玩家组织好的文件夹结构；如果你看出了规律（比如整个备份文件夹都是多余的），按规律给建议，并说明和逐组建议的区别
- 先跑一次预览（不加 `--apply`，预览不会动任何文件），把方案和预览合成一次提问：处理哪些、每组留哪份、一共多少个文件、能腾出多少空间
- 完全相同的重复副本建议移入回收站；其他情况（旧版本、冲突里不要的那个、合集里多余的单件）默认建议禁用（文件名后面加 `.off`，游戏不读，随时能恢复），玩家想直接删就移入回收站
- 玩家同意了，就用同一批路径加 `--apply` 执行，不用再问第二次。玩家只想处理一部分，就按他说的范围重新预览再问
- 文件很多时分批执行，每批几十个路径，避免命令行太长

冲突和脚本碰撞及合集文件要看玩家想留哪个 mod 的效果，给出你的建议和理由，让玩家选，选好了再按同样的方式预览、问一次、执行。

**文件操作准则：**

- 所有文件操作都通过 Sieve 的命令执行，不要直接改名、移动或删除 mod 文件
- 只处理 `.package` 和 `.ts4script`；已经禁用的 `.off` 文件用 `restore` 恢复
- 移入回收站（`recycle --apply`）时，每个 `--path` 按顺序配一个 `--expect-sha256`，用最近一次扫描报告里该文件的 `sha256`。Sieve 会重新算一遍，对不上的文件会跳过，防止删错被换过的文件。禁用不需要哈希
- 两者是按出现顺序一一配对的，错位不会删错文件，但会让对应的文件被悄悄跳过。所以每个 `--path` 后面紧跟它自己的 `--expect-sha256`，成对写；路径和哈希都从报告里同一个文件对象取。执行后核对 `files` 里每个文件的状态，有被跳过的要告诉玩家
- 距离上次扫描已经隔了一阵，或者玩家说动过文件，先重新扫描，再预览、再问

| 要做什么     | 命令                                                   |
| -------- | ---------------------------------------------------- |
| 看已禁用的文件  | `list-disabled --json -`（只读）                         |
| 禁用       | `disable --path "<文件>" --json -`                     |
| 恢复       | `restore --path "<原路径或 .off 路径>" --json -`，或 `--all` |
| 移入回收站    | `recycle --path "<文件>" --json -`                     |
| 撤回最近一批删除 | `undo --json -`                                      |

每多一个文件就多写一次 `--path`。以上命令不加 `--apply` 都只是预览。

```powershell
Sims4ModSieve.Cli.exe disable --path "D:\Mods\副本.package" --json -
# 玩家同意后
Sims4ModSieve.Cli.exe disable --path "D:\Mods\副本.package" --apply --json -

Sims4ModSieve.Cli.exe recycle --path "D:\Mods\副本.package" --expect-sha256 "<该文件的 SHA-256>" --apply --json -

# 多个文件时，路径和哈希成对写
Sims4ModSieve.Cli.exe recycle --path "D:\Mods\a.package" --expect-sha256 "<a 的 SHA-256>" --path "D:\Mods\b.package" --expect-sha256 "<b 的 SHA-256>" --apply --json -
```

执行结果看 JSON 里的 `files` 和 `errors`。退出码 `3` 表示至少有一个没处理成功，其他的可能已经完成了，逐个看 `files` 里每个文件的状态。

## 5. 收尾

告诉玩家：处理了多少个文件、腾出多少空间、有没有没成功的（是哪几个、为什么）。告诉玩家怎么撤回：Sims 4 Mod Sieve 桌面版 → 设置 → 删除与忽略。或由你通过命令帮助玩家恢复。

再说清还剩什么没解决、建议下一步做什么：比如清完重复后再查一次冲突，进游戏看问题还在不在，问题还在的话询问玩家具体情况、是否有截图，继续排查。

---

## 附录：需要时再查

### 命令和参数

命令、参数、可用的 kind 和退出码以 `<命令> --help` 为准，这里不再重复，只补 `--help` 里没写清的几点：

- 筛选参数（`--severity`、`--kind`、`--path-contains`、`--limit`、`--omit-resource-keys`、`--summary-only`）只缩小 JSON 明细，扫描本身、完整汇总和退出码不变
- `--path-contains` 的排序：先放同一条路径命中全部词的组，再放同组不同文件合起来命中全部词的组，最后放命中部分词的组；排序完成后才应用 `--limit`
- `--json -` 把 JSON 直接写到标准输出，只适合组很少、或玩家明确要直接看的时候
- `--no-cache` 和 `--extension` 会影响扫描本身，不是输出筛选

几个常用组合：

```powershell
Sims4ModSieve.Cli.exe scan-conflicts --source "D:\Mods" --json "conflicts-summary.json" --summary-only
Sims4ModSieve.Cli.exe scan-conflicts --source "D:\Mods" --json "conflicts-abnormal.json" --severity abnormal --limit 20 --omit-resource-keys
Sims4ModSieve.Cli.exe scan-conflicts --source "D:\Mods" --json "conflicts-mod.json" --path-contains "Creator MouthScale" --severity abnormal --limit 20
```

### 报告字段

- `version` 是程序版本，`schemaVersion` 是输出结构版本。
- `sources` 列出实际扫描的目录，`sourceOrigin` 是显式参数（`argument`）或默认范围（`settings`）。
- `groupCount` 是完整结果的组数，`visibleGroupCount` 是这份 JSON 列出的组数。`severityCounts` 按完整结果统计四级数量（结果组与读取问题合计），`issueCount` 是完整的扫描和读取问题数。`abnormalCount` 是异常组与异常读取问题合计，`incompleteCount` 只统计未分析的读取问题。
- `groups` 列出结果组，`issues` 给出扫描和读取问题。文件字段 `path` 是完整路径，`sha256` 是整个文件的哈希，为 `null` 时没有可用哈希。
- 重复组带 `suggestedKeepPath`（建议保留）、`suggestedDeletePaths`（可处理的副本）和 `reclaimableBytes`（处理后能腾出的字节数）。
- 重复组的每个文件还带：`size`、`lastWriteTimeUtc`（修改时间）、`sourceIds` / `primarySourceIds`（来自哪个扫描来源）、`isInsideMods`（是否在 Mods 根目录里面）、`directoryDepth`（所在文件夹从盘符算起的总层数）、`identity`（磁盘上的文件身份，同一个文件的硬链接会相同）。组本身有 `section`：`FocusCrossScope` 重点范围和参照范围之间的重复，`FocusInternal` 重点范围内部的重复，`ReferenceInternal` 参照范围内部的重复，`Ordinary` 普通重复。只扫 Mods 时一般都是 `Ordinary`。
- 冲突组（`resource_conflict`、`suspected_same_mod`、`bundle_containment`）的每个文件带：`size`、`lastWriteTimeUtc`、`sha256`、`duplicateSha256`（它同时属于某个重复组时，那个组的哈希）、`isBundle`（是不是合集）、`containedContentIdentical`（单件被合集包含时，内容是否完全一样，`null` 表示不适用）、`resourceCount`、`sharedResourceCount`（与组里其他文件共有的资源数）、`uniqueResourceCount`（自己独有的资源数）。组本身带 `bundlePath`、`bundleUniqueResourceCount`（合集独有的资源数），以及 `relations`：两两之间的 `firstPath`、`secondPath`、`overlap`、`differentResourceCount`、`identicalResourceCount`、`incompleteResourceCount`。
- package 组的 `resourceKeys` 是完整 TGI 字符串数组，格式为 `Type:Group:Instance`。名称用 TGI 的 Type 编号查同组的 `resourceTypes`：每个 Type 一项，`type` 是带 `0x` 的十六进制编号，`typeName` 是程序给出的名字，和桌面版技术详情显示的一致。
- `severity` 有四级：`abnormal` 异常，`risk` 有隐患，`no_effect` 不影响，`incomplete` 未分析。`certainty` 标出确定（`confirmed`）和可能（`possible`）。`kind` 是结果的具体类型。
- 相同内容、已经比较出差异和未比完要分开说明。部分内容不同时，仍可能有未比完的条目。
- `ignored: true` 表示玩家以前忽略过此文件，默认会显示。汇总的 `ignoredCount` 按结果中不同的文件路径计数。
- `scan-conflicts` 在分组规模超限时不生成部分组，`complete=false`、退出码为 `1`。扫描问题 `code=conflict-grouping-too-large` 的 `reason` 为 `duplicate-copies`（建议先查重复）或 `too-many-shared-resources`（建议分批检测），附 `resourceMatchCount`、`collapsedResourceMatchCount`、`resourceMatchLimit`，副本原因另附多余副本数 `redundantCopyCount`。该问题的 `kind=read_incomplete`、`severity=incomplete`；中文提示与估算值、上限始终写入标准错误。

JSON 顶层始终带 `complete`、`omittedGroupCount`、`omittedIssueCount` 和 `filters`：

- 用了任何输出筛选、`--limit`、资源键省略、只要汇总或 `--hide-ignored` 时，`complete` 为 `false`，即使这次没有实际省略组。
- `omittedGroupCount` 是完整组数减已列组数；`omittedIssueCount` 是完整读取问题数减已列问题数。隐藏组里的部分文件、或省略资源键时，这两个数也可能都是 `0`。标准错误的摘要会写「还有 N 组未列出」，资源键省略时另有提示。
- `filters` 记录实际生效的 `severity`、`kind` 数组（去重），以及 `pathContains`、`limit`、`hideIgnored`、`omitResourceKeys`、`summaryOnly`。未指定的数组为空，`pathContains` 和 `limit` 为 `null`，开关为 `false`。
- `complete: true` 只表示报告输出了全部现有结果，不表示每个文件都读完了；仍要看 `issues` 和未分析数量。

### 文件操作细节

- 禁用时在原文件名末尾加 `.off`。
- `files` 逐个列出路径、字节大小、状态和失败原因：`preview` 待处理，`completed` 已完成，`failed` 未完成，`listed` 已禁用。
- `undo` 从共享的回收站记录里选最近一批中仍待恢复的文件，恢复到原路径。原位置有同名文件、文件夹，或回收站已清空时，看每个文件的结果。要处理更早的删除记录，或桌面版移入回收站的 `.off` 文件，用桌面版设置里的删除历史。
- 每次 `--apply` 的结果都会自动追加到 `%LOCALAPPDATA%\Sims4ModSieve\cli-file-operations.jsonl`，玩家想查做过什么可以看这里。
