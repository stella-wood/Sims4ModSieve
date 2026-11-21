# CLI 开发任务书

> 2026-11-17。依据 `main` 的 2cd3be2（#42 已合）和 `perf/stage0-baseline`（#43）。
> **开工前先核对下面写的「现状」**，代码可能已经变了。对不上的地方以代码为准，在 PR 里写出来。
> 性能与缓存不在本任务范围，见 `docs/architecture/性能优化与缓存架构.md` 和 `docs/architecture/性能优化与重构实施计划.md`，本文只写 CLI 跟它们的接口。

---

## 0. 背景与原则

现状：`src/Sims4ModSieve.Cli` 只有 `scan-duplicates`，csproj 只引用 Core，而且**安装版和便携版都没有打包 CLI**。

为什么要做：玩家会让终端里的 AI 助手（Claude Code、Codex 等）帮忙查 mod，也会让 AI 直接处理文件。如果 Sieve 不提供处理文件的命令，AI 会自己用 `rm`、`del` 或随手挪动：不进回收站，没有记录，界面也不知道。CLI 要提供一条更安全、也更省事的路。

原则：
- 扫描命令全程只读。
- 动文件的命令默认只预览，加 `--apply` 才真动；不弹「确定吗 y/n」，AI 回不了。
- 读不了、没比完的标「未分析」，不能当成「通过」。
- 措辞只说「内容不同」「覆盖」，不说「会导致崩溃」。
- CLI 不引用 Desktop，也不复制 Desktop 的代码；需要共用的挪到共享项目。
- 界面和 CLI 对同一批文件，结果必须一致。

## 1. 分阶段与并行

下一版发布 = 性能 + 缓存 + CLI。CLI 跟性能线**并行**，由另一个 Codex 做：

| 阶段 | 内容 | 开工条件 |
| --- | --- | --- |
| CLI-1 只读 | 第 2–6 节、第 9–11 节中跟扫描有关的部分 | 现在就能开工，跟性能阶段 1–3 并行 |
| CLI-2 动文件 | 第 7–8 节、第 9–11 节其余部分 | 整文件哈希缓存与[决策 0009](../decisions/0009-重复组动作前只检查存在并刷新.md)落地后 |

每个阶段一个 PR。

跟性能线少撞车：
- CLI-1 先用各扫描器现有的 `CreateDefault` / 默认构造，**不自己设并发、不碰缓存**。性能线会往扫描器里加并发策略（阶段 1–2）、套缓存装饰器（阶段 4–5）；等它把选项接进统一的组装入口后，CLI 改成用那个入口。
- 如果想把「组装扫描流水线」抽成 Core 里的共用工厂（界面、CLI、基准三处用），先在 PR 描述里提出来，跟性能线对齐后再做，不要两边各抽一份。
- 两边都会动 `Sims4ModSieve.sln` 和 `.github/workflows/ci.yml`，合并时注意。

## 2. 扫描命令

### 2.1 `scan-conflicts`
- 参数跟 `scan-duplicates` 一样用 `--source <目录>`（可多个）。
- Cli 的 csproj 加上对 `Sims4ModSieve.Packages` 的引用。
- 组装顺序参照 #43 的 `eng/Sims4ModSieve.Benchmarks/BenchmarkRunner.cs`：`PackageConflictScanner` → `ResourceContentComparer` → `ScriptModuleScanner` → `DuplicateScanner` → `PackageConflictGroupingBuilder`。**再跟界面 `MainWindowViewModel.Conflicts` 的实际流程逐项对照**，以界面为准。
- 分组复用 `PackageConflictGroupingBuilder`，不在 CLI 里另写一份。
- JSON 里每组列出全部文件，不折叠副本；副本关系用字段表示。

### 2.2 `scan-scripts`
- 复用 `ScriptModuleScanner`，分组跟界面用同一份逻辑。

### 2.3 `scan-duplicates`（已有）
- 补上第 4 节的 JSON、评级字段和第 5 节的退出码，行为保持不变。

### 2.4 `find-mods`
- 先看设置里的 Mods 文件夹，再找默认位置（文档\Electronic Arts\The Sims 4\Mods），并说明结果来自哪里。
- 文档目录用系统 API 取，不拼用户名；OneDrive 重定向也要能找到。
- 只查找，不写设置。找不到就明确说找不到，退出码 `1`。

## 3. 设置与忽略列表

### 3.1 跟界面共用设置
界面把 Mods 文件夹、扫描来源和 Tray 文件夹存在 `%LOCALAPPDATA%\Sims4ModSieve\duplicate-settings.json`（`DuplicateSettingsStore`，在 Desktop）。把它挪到共享项目，CLI 读写同一份。

- `config show`：显示 Mods 文件夹、扫描来源（含是否启用）、Tray 文件夹，以及每个目录现在是否存在。
- `config set-mods <路径>`：设默认 Mods 文件夹。路径不存在就拒绝，不写入。
- 扫描命令不写 `--source` 时，按界面现在的规则取目录（先查界面的实际规则，别另定一套）；写了 `--source` 就只用它，不改设置。
- 设置里没有可用目录时报错，提示运行 `find-mods` 或 `config set-mods`，退出码 `2`。
- 扫描输出（含 JSON）写明本次实际扫描了哪些目录，来自 `--source` 还是设置。
- 目录来自设置时，JSON 加一个 `notice`：「本次目录来自已保存的设置。如果这次对话还没跟玩家确认过，请先确认这是不是要查的文件夹。」CLI 不记对话，所以每次都输出；`AI-GUIDE.md` 写明同一次对话确认一次就够。
- 界面开着时可能在内存里留着旧设置，之后保存会盖掉 CLI 的修改。先查清这一点，确保不丢改动、界面能显示最新值。
- 设置文件读不出或已损坏：明确报错，不当成没有设置，更不覆盖它。

### 3.2 已忽略的文件
界面有「忽略」功能（`Desktop/Services/FileManagementStore.cs`，`IgnoredFileRecord`）。跟设置一起挪到共享项目。

- 默认**照常输出**被忽略的文件，每条带 `ignored: true`，汇总里单列「已忽略 N 项」。
- 加 `--hide-ignored` 时跟界面默认一样隐藏。
- 理由：AI 帮玩家排查时，玩家以前忽略的东西可能正是问题所在。

（这条是暂定方案，作者如有异议以作者为准。）

## 4. 机器可读输出与评级

### 4.1 `--json -`
- 所有扫描命令都支持 `--json -`：JSON 打印到标准输出，这时标准输出**只放 JSON**，进度和提示一律写标准错误。
- 不写 `--json` 时保持现在的行为（写文件 + 打印摘要）。
- JSON 带软件版本和 `schemaVersion`。
- 每个文件带完整路径和 SHA-256（CLI-2 的 `--expect-sha256` 要用）。AI 要拿路径去操作，所以 CLI 输出不替换用户名；这跟日志脱敏是两回事，日志照旧脱敏。

### 4.2 评级字段
以 `docs/product/冲突评级与拆分标准.md` 为准：
- `severity`：`abnormal`（异常）/ `risk`（有隐患）/ `no_effect`（不影响）/ `incomplete`（未分析）
- `certainty`：`confirmed` / `possible`
- `kind`：如 `resource_conflict`、`script_collision`、`not_loaded`、`duplicate`、`base_override`
- 哪项归哪级照那份文档的「各项归哪级」表，不自己定。
- 那份文档里「CLI 相关字段（待对齐）」一节，本任务做完后同步更新。

## 5. 退出码
- `0`：全部查完、没有问题，或预览成功
- `1`：查完但有冲突，或有未分析的文件
- `2`：参数错误、没有可用目录
- `3`：动文件时有文件没成功（全部或部分），包括 SHA-256 对不上被拒绝

写进 `--help`。

## 6. `--help` 与 `guide`
- `--help` 写给第一次见的人：每个命令一句话、每个参数什么意思、一个例子。支持 `<命令> --help`。
- `--help` 第一行：「AI 助手请先运行 `guide`」。
- `guide` 打印 `AI-GUIDE.md`（第 9 节）。文件作为嵌入资源编进 exe，保证说明跟程序版本一致。

## 7. 动文件命令（CLI-2）

| 命令 | 作用 |
| --- | --- |
| `list-disabled` | 列出已禁用的文件（只读） |
| `disable --path <文件>` | 禁用（改名为 `.off`，记入账本），可多个 `--path` |
| `restore --path <原路径>` / `restore --all` | 恢复禁用的文件 |
| `recycle --path <文件>` | 移入 Windows 回收站，可多个 `--path` |
| `undo` | 撤回最近一次删除 |

- 只处理 `.package` 和 `.ts4script`（`.off` 由 `restore` 处理）。其他文件、文件夹一律拒绝。
- 拒绝动 Sieve 自己的文件，复用 `ApplicationDirectoryGuard`。
- 不加 `--apply`：只预览，列出每个将被改动的文件和大小，一个文件都不动。
- `recycle --apply` 的每个 `--path` 按顺序对应一个 `--expect-sha256`。执行时直接读取文件核对。禁用沿用文件戳核对，重复组检查遵循[文件操作规则](../release/file-operation-safety.md)。
- 部分失败时继续处理其他文件，最后逐个报告成功和失败，退出码 `3`。
- 所有命令支持 `--json -`。
- 每次 `--apply` 写操作日志：时间、命令、文件、结果。日志位置写进 `guide`。

## 8. 跟界面共用同一套实现（CLI-2）
- 挪到共享项目的只挪 CLI 真用到的：`ModDisableService`、`FileActionService`、`FileManagementStore`、`WindowsRecycleBinAdapter`。回收站是 Windows 专用，放进 Windows 专用的共享项目，Core 不引用它。
- 界面和 CLI 用同一份禁用账本和删除记录：CLI 禁用的，界面能看到、能恢复；反过来也一样。
- `undo` 复用 `FileActionService` 的删除历史恢复，按共享记录选中最新批次中的 `.package` 或 `.ts4script`，桌面的 `.off` 回收记录仍由桌面删除历史恢复。
- 文件操作和账本的读改写用[跨进程文件锁](../decisions/0011-跨进程串行写入文件管理记录.md)。
- 这是重构，界面行为不能变，现有测试全部通过。

## 9. 给 AI 的说明：`AI-GUIDE.md`
仓库根目录新建。CLI-1 写扫描部分，CLI-2 补动文件部分。

**基本用法**
- 这个工具干嘛的；exe 在哪：安装版默认在 `%LOCALAPPDATA%\Programs\Sims4ModSieve`（见 `eng/installer/Sims4ModSieve.iss` 的 `DefaultDirName`，用户可改），便携版在玩家自己解压的地方，找不到就问玩家；怎么确认能跑（`--help`）。
- 玩家说什么、用哪个命令（例：「有没有冲突」→ `scan-conflicts`；「是不是重复下了」→ `scan-duplicates`）。
- 找文件夹的顺序：`config show` → `find-mods` → 都没有就问玩家，用 `config set-mods` 存下来。只想临时查别的目录用 `--source`，不改设置。
- 建议顺序：先查重、处理完再查冲突。
- `--json -` 主要字段的意思；「内容相同」「内容不同（覆盖）」「没比完」三类的区别；`severity` 和 `certainty` 怎么读。

**规矩（最重要）**
- 玩家明确要求了才动文件；动之前先把预览给玩家看。
- 优先 `disable`，能恢复；玩家明确说删除才用 `recycle`。
- 不要绕开 Sieve 自己删除或挪动 mod。
- 不说「会导致游戏崩溃」，只说「内容不同，后加载的会覆盖前面的」。
- 「没比完」不能说成没问题：「没查出冲突不代表没有冲突」。结论分「确定的」和「可能的」。
- 跟玩家说人话，别直接甩 JSON。

**Sieve 管不到的**
- 先说边界：Sieve 目前只查重复和冲突；报错日志、mod 是否过期、游戏版本等还不会查。不能把「Sieve 没查出问题」说成「mod 都没问题」。
- 玩家需要时 AI 可以帮忙排查：读报错日志（原版 `lastException`、MCCC 的日志、WickedWhims 的 `WickedWhims_Exception` 等，写明一般位置和先看哪几行）；游戏更新后对照作者页面查脚本 mod 是否有新版；常见坑（放得太深、没解压、游戏版本不对）。
- 规矩照守：只读；要动文件走 Sieve 的 `disable` / `recycle`，不自己删；先给玩家看要做什么。
- 说清把握：从日志推「是哪个 mod」可能猜错，说「可能是」并给出理由，不说成已查明。

`AI-GUIDE.md` 也作为单独文件放进安装包和便携包，跟 exe 放一起，玩家可以自己打开看、自己交给 AI。`guide` 命令和这个文件从同一份源生成。

## 10. 打包
- 安装版和便携版都带上 CLI 的 exe 和 `AI-GUIDE.md`，跟主程序同一目录。exe 名保持 `Sims4ModSieve.Cli.exe`。
- 更新 `eng/package-installer.ps1`、`eng/package-portable.ps1`，包结构验证（`verify-portable-bundle.ps1`、`test-installer.ps1`），以及「跳过 Sieve 自身文件」的规则（`ApplicationDirectoryGuard`）。

## 11. 给人看的说明：README「让 AI 帮你查 mod」
写给不懂电脑的模拟人生玩家。先写草稿，作者会改措辞：
1. 装好 Sieve（安装版或便携版都行）。
2. 打开能在电脑上执行命令的 AI 助手，把 `AI-GUIDE.md` 交给它，告诉它 Sieve 装在哪。建议自己先翻一眼，看看 AI 被要求守哪些规矩。
3. 然后直接问，比如「帮我看看 Mods 里有没有冲突」。

还要说明：
- AI 每次执行命令前可能会问你要不要允许，这是正常的。
- 扫描只读。AI 也能帮你禁用文件或移入回收站，它应该先给你看要动哪些；不确定就让它只禁用，别删。
- 有的 AI 在云端跑，摸不到你电脑上的文件，那种用不了。

## 12. 测试
- CLI-1，用 `tests/fixtures` 样本：`scan-conflicts`、`scan-scripts`、`find-mods`、`config` 能跑通；`--json -` 输出能被解析且标准输出只有 JSON；退出码对。
- **CLI 和界面结果一致**：同一批样本，CLI 的分组和评级跟界面流程算出来的一致。
- 设置（临时目录）：CLI 写的界面 store 读得到，反过来也一样；`--source` 不改设置；路径不存在拒绝写入；设置损坏时报错且不覆盖；输出写明目录和来源。
- 忽略：默认输出带 `ignored: true`；`--hide-ignored` 时隐藏；汇总计数对。
- CLI-2（临时目录，不碰真实 Mods）：
  - 不加 `--apply` 时文件和账本都不变；
  - `--apply` 且 SHA-256 正确时成功；对不上、缺 `--expect-sha256` 时拒绝，文件不变；
  - CLI 只核对明确指定的待删文件；
  - 部分失败时其他文件照常处理，报告和退出码正确；
  - 拒绝动 Sieve 自己的文件、文件夹和其他类型文件；
  - `disable` 后 `restore` 能还原；`recycle` 后 `undo` 能还原（真实回收站测试沿用现有环境变量开关）；
  - CLI 禁用的文件，界面服务读得到。
- 打包：安装包和便携包里有 CLI exe 和 `AI-GUIDE.md`，CI 的包结构验证覆盖到。
- 测试只跑受影响的项目，每个 PR 合并前整套跑一次。

## 13. 汇报
改了哪些文件；每个新命令的真实运行示例和输出；新增测试及各自验证的行为；跟界面结果对照的方法和结论；打包是否已包含 CLI；没验证的部分；跟性能线可能冲突的文件。

## 14. 以后（这次不做）
- 报错日志分析做好后加 `analyze-log`，`AI-GUIDE` 里「读日志」改成「先跑 `analyze-log`」。
- 二分排查做好后加对应命令，复用 `disable` / `restore`（issue #22）。
