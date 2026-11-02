# Sims 4 Mod Sieve · v0.1.0-alpha.1 发布前审计

后续修复及主 agent 验收结果见 [修复与验收记录](RELEASE_FIX_VERIFICATION.md)。

审计日期：2026-11-02（Asia/Shanghai）。审计对象：`main`，提交 `8d1f9dd9fd31881d6ec819f054351e4f157d9626`。开始时工作树干净，本地提交与 `git ls-remote origin refs/heads/main` 一致。下列代码行号均以该提交为准；正文中的文件和 artifacts 路径均相对于仓库根目录，文档链接已按本报告位置调整。

**结论：No-Go，当前不建议进入面向用户分发的 alpha.1 发布阶段。** 构建、基础检测和多项文件操作已有实测证据，但发布包、许可证随包交付、文件操作安全和依赖安全仍未通过。可以继续内部开发与验证，不能把 CI 成功等同于发布就绪。

本次只新增本报告；没有修改产品代码、测试、工作流或依赖，没有提交、打 Tag 或创建 GitHub Release。审计日志、TRX、临时探针和本地 publish 输出位于被忽略的 `artifacts/release-audit/`，不属于可发布版本。文件操作只使用自动生成的临时文件；没有读取或操作真实 Mods 语料。

## 证据口径与严重级别

- **已验证事实**：实际执行命令、测试、临时探针或读取当前 GitHub 状态得到的结果。
- **代码推断**：根据当前调用路径和实现得到的结论；没有复现的影响不写成已发生事故。
- **待人工验证**：本机不能代替的干净系统、真实语料或特定系统策略验证。
- **Blocker**：缺少必需交付物或分发条件，当前不得发布。
- **High**：可能操作错误文件、丢失恢复记录或存在未解决的依赖安全问题；本审计要求发布前处理。
- **Medium**：兼容性、容量、交互稳定性或异常路径的缺口；分别说明是否单独阻断 alpha。
- **Low**：不阻断主要流程的文档或支持体验问题。

## 构建、测试与发布验证

环境为 Windows `10.0.26200` / `win-x64`，仓库本地 SDK `8.0.424`，运行时 `Microsoft.NETCore.App` 和 `Microsoft.WindowsDesktop.App` 均为 `8.0.30`。这是一台已有 SDK、缓存和开发工具的机器，**不是干净 Windows 环境**。

### 已执行的检查

| 检查 | 结果与证据 |
|---|---|
| 锁定依赖还原 | `eng/dotnet.cmd restore Sims4ModSieve.sln --locked-mode --nologo` 正常权限下退出 0，7 个项目完成还原或已是最新；沙箱内早期尝试无输出退出 1，未将其记为还原通过 |
| Release 构建 | `eng/dotnet.cmd build Sims4ModSieve.sln -c Release --no-restore --nologo -m:1` 退出 0，**0 警告、0 错误** |
| 当前 main 的 CI | [CI run 41449427108](https://github.com/stella-wood/Sims4ModSieve/actions/runs/41449427108)，同一 SHA，push 触发，2026-11-02 21:04（北京时间）完成；锁定还原、Release 构建和测试均成功 |
| CI 测试数量 | Core：258 通过 / 1 跳过；Packages：53 通过 / 3 跳过；Desktop：287 通过 / 3 跳过；合计 **598 通过、0 失败、7 跳过，共 605 项**，数量取自该 job 日志 |
| 本地完整测试，正常权限第一次 | Core：258 通过 / 1 跳过；Packages：53 通过 / 3 跳过；Desktop：285 通过 / **2 失败** / 3 跳过；合计 **596 通过、2 失败、7 跳过**；见 `artifacts/release-audit/interactive-tests.log` 与 `interactive-test-results/` |
| 失败测试类单独复核 | 原样执行 `--filter FullyQualifiedName~ConflictRelationshipsDialogTests`，**10 通过、0 失败**；见 `focused-tests.log` 与 `focused-test-results/`。不能用此结果替代前一次完整测试的失败 |
| 本地完整测试，正常权限第二次 | 原样全量复跑退出 0：Core 258 通过 / 1 跳过、Desktop 287 通过 / 3 跳过、Packages 53 通过 / 3 跳过；合计 **598 通过、0 失败、7 跳过**；见 `verification-tests.log` 与 `verification-test-results/`。保留第一次失败记录，不据复跑成功宣称不存在不稳定性 |
| 真实 Windows 回收站 | 设置 `SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST=1` 后执行 `--filter TestCategory=WindowsIntegration`，**3 通过、0 失败、0 跳过**；见 `recycle-tests.log` 与 `recycle-test-results/` |
| CLI 冒烟 | 对 `tests/fixtures/duplicates/Mods` 与 `Downloads` 执行 README 的扩展名受限扫描，退出 0；发现 3 文件、哈希 3 文件、重复组 1、未完成 0，JSON 与 HTML 均生成 |
| 本地 publish | `eng/dotnet.cmd publish src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj -c Release --no-restore --nologo -m:1 -o artifacts/release-audit/publish` 退出 0；见 `publish.log`。这是默认框架依赖输出，没有构造或批准正式分发包 |
| 依赖安全查询 | `eng/dotnet.cmd list Sims4ModSieve.sln package --vulnerable --include-transitive --format json --output-version 1` 退出 0，**报告 ImageSharp 3.1.11 的 6 条公告**，见 H04；该命令退出 0 不表示依赖没有漏洞 |
| 文件安全临时探针 | `artifacts/release-audit/probe/` 使用真实临时文件、物理元数据访问和模拟回收站；复现 H01、H02、H03，输出保存在 `probe.log` |

首次沙箱完整测试为 Core 258/0/1、Desktop 201/86/3、Packages 52/1/3（通过/失败/跳过）。其中可见 `SetCursorPos` 失败及临时文件 `File.Move` 被拒绝；恢复正常权限后这些大批权限失败消失。正常权限下仍发生的两项失败另见 M05，没有归入沙箱失败而忽略。

### 已通过的功能和安全检查

这些“通过”只适用于对应证据覆盖的行为，文件操作整体仍受 H01～H03 限制。

| 已通过项 | 实现与测试证据 |
|---|---|
| 重复检测按完整文件内容分组 | [DuplicateScanner.cs](../../src/Sims4ModSieve.Core/Duplicates/DuplicateScanner.cs#L67) 按大小缩小候选后计算 SHA-256，以长度与哈希分组；`FindsExactDuplicatesAcrossSourcesAndPrefersModsCopy`、`ProducesDeterministicGroupAndFileOrdering`，以及本次 CLI 冒烟 |
| 建议保留规则只有一个权威 | [DuplicateSelectionService.cs](../../src/Sims4ModSieve.Core/Duplicates/DuplicateSelectionService.cs)；`PrefersModsThenDepthThenStablePath`，覆盖 Mods 优先、深目录优先与稳定路径顺序；组选择、整组手选及删除后重建另有 Desktop 会话测试 |
| 父子来源去重、读取失败隔离、reparse point 跳过 | [SourceFileDiscovery.cs](../../src/Sims4ModSieve.Core/Duplicates/SourceFileDiscovery.cs#L116)；`OverlappingParentAndChildReadEachFileOnceAndCreateFocusSection`、`DirectoryPermissionFailureDoesNotDiscardOtherSources`、`ReparsePointDirectoryIsReportedAndNotFollowed` |
| 扫描时常见文件变化被拒绝 | [StableFileHasher.cs](../../src/Sims4ModSieve.Core/Duplicates/StableFileHasher.cs#L25) 在哈希前后复核文件戳，最多重试一次；`RetriesOnceWhenFileChangesAndReturnsSecondStableSnapshot`、`RejectsAFileThatChangesDuringBothAttempts`。这不能证明同戳变化被识别 |
| Package 预检与内容比较 | [LlamaLogicPackageIndexReader.cs](../../src/Sims4ModSieve.Packages/LlamaLogicPackageIndexReader.cs#L55)、[DbpfResourceContentHasher.cs](../../src/Sims4ModSieve.Packages/DbpfResourceContentHasher.cs)；DBPF 版本/索引布局、重复键拒绝、未压缩与 zlib 同内容、截断、Adler32、字节预算和流释放均有测试；`PartialFailureIsNeverReportedAsIdentical` 防止失败被当成一致 |
| 包含证明保留完整性要求 | [ConflictPairSummaryBuilder.cs](../../src/Sims4ModSieve.Core/Conflicts/ConflictPairSummaryBuilder.cs)、[PackageConflictGroupingBuilder.cs](../../src/Sims4ModSieve.Core/Conflicts/PackageConflictGroupingBuilder.cs)；相关测试覆盖完整资源清单、稳定版本、工具元数据排除、缺证据不证明包含，以及第三文件失败不污染其他文件对 |
| Script 不执行脚本、逐模块隔离损坏 | [ScriptModuleScanner.cs](../../src/Sims4ModSieve.Core/Scripts/ScriptModuleScanner.cs#L255)、[ScriptModuleContentHasher.cs](../../src/Sims4ModSieve.Core/Scripts/ScriptModuleContentHasher.cs#L19)；`ACorruptEntryDoesNotInvalidateAHealthySiblingModule`、`InvalidBytecodeFallsBackToDifferentSourceEntries`、`SourceBytesEqualToTheBytecodeBodyAreDifferentFormats` 等通过 |
| ZIP 有实际边界与完整性保护 | [ScriptZipDirectory.cs](../../src/Sims4ModSieve.Core/Scripts/ScriptZipDirectory.cs#L27) 检查实际目录数量、局部头、数据边界、CRC、输出长度、DEFLATE 结束状态与 descriptor；`UnderstatedEndRecordCountCannotBypassTheActualEntryLimit`、`InvalidCrcOrDeclaredLengthProducesAnUncomparedOccurrence`、`AValidZipWrittenWithoutSeekingSupportsDataDescriptors` 等通过；未覆盖项见 M03 |
| 文件删除统一走回收站服务 | [FileActionService.cs](../../src/Sims4ModSieve.Desktop/Services/FileActionService.cs#L125) 与 [WindowsRecycleBinAdapter.cs](../../src/Sims4ModSieve.Desktop/Services/WindowsRecycleBinAdapter.cs#L17)；拒绝 UNC/非固定盘，预检与执行前重复核对元数据，COM 使用 `FOFX_RECYCLEONDELETE`，没有主动永久删除降级分支。该 flag 的用途与 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags) 一致；回收站关闭/满额等策略仍待实测 |
| 撤回、历史恢复与同名保护 | [WindowsRecycleBinAdapter.cs](../../src/Sims4ModSieve.Desktop/Services/WindowsRecycleBinAdapter.cs#L170) 恢复前和 `PreMoveItem` 均检查同名，Shell 碰撞时改名而非覆盖，并核对实际恢复路径；本次 `UnicodeFileCanRoundTripThroughWindowsRecycleBin`、`PersistentHistoryValidatesExactRecycleItemAndNeverOverwrites` 通过 |
| 禁用、恢复、持久记录和部分失败 | [ModDisableService.cs](../../src/Sims4ModSieve.Desktop/Services/ModDisableService.cs#L124) 以 `.off` 原地改名，禁用前先记账，恢复不覆盖；`FailedLedgerSaveDoesNotRenameOrLoseExistingRecords`、`InterruptedDisableAfterRenameRetainsRecoverableRecord`、`RestoreCollisionReportsPartialSuccessAndRetainsBlockedRecord` 等通过；真实 `DisabledModCanRoundTripThroughWindowsRecycleBin` 证明跨重启恢复先回到 `.off` 且保护原同名文件 |
| 单进程扫描并发、取消与会话隔离 | [PackageConflictScanner.cs](../../src/Sims4ModSieve.Core/Conflicts/PackageConflictScanner.cs#L178)、[MainWindowViewModel.Conflicts.cs](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.Conflicts.cs#L336)；并发上限、取消传播、不发布取消结果、迟到进度不能覆盖新会话、部分文件失败均有测试；`CancellationUnlocksSourcesAndNavigationWithoutPublishingSuccess`、`CancelledDuplicatePhaseCannotPublishIntoClearedConflictSession` 等通过 |
| HTML 转义、诊断读取边界和配置校验 | `JsonIsCamelCaseAndHtmlEscapesPathsAndMessages`、`DiagnosticsHandleMissingUnreadableAndLargeLogs`、`DiagnosticDisplayAndCopyRedactUserPaths`、`MalformedLedgerCannotRenameFilesOrBeOverwritten`、`MalformedPidlFailsManagedValidationBeforeShellBinding` 等通过。诊断脱敏针对用户名路径，不等于移除全部本地路径 |
| 仓库许可与字体许可有基础交付配置 | 根目录 [LICENSE](../../LICENSE) 是本项目 MIT 许可；[Desktop.csproj](../../src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj#L13) 复制项目许可证、第三方清单和字体 OFL 文本，publish 输出中实际存在。其他依赖许可证文本缺失见 B02 |

未采集行覆盖率或分支覆盖率，不能用通过测试数量宣称“全面覆盖”。本次没有新增仓库测试；临时探针只为复核审计发现，未改变现有断言。

## 核心功能交付判断与明确限制

| 功能 | alpha 交付判断 | 当前边界 |
|---|---|---|
| 重复文件扫描、选择、CLI 报告 | 基础行为可用，删除路径待修复 | 仅证明扫描快照下完整文件字节相同；不识别语义重复、硬链接身份或 package 内包含关系；删除前没有内容复核 |
| Package 基础冲突排查 | 可作为有限范围的只读 alpha 功能，不能宣称完整兼容 | DBPF 2.1、索引版本 3、三种受支持常量位；重复 TGI 索引整包拒绝；内容只支持未压缩与 zlib，RefPack/QFS、未知/删除记录返回未完成；不推断实际游戏加载优先级，也不证明必然引起游戏报错。详见 [Packages README](../../src/Sims4ModSieve.Packages/README.md#现在不支持什么) |
| Script 模块碰撞 | 构造样本验证较充分，真实兼容范围待补 | 比较 Python 模块名和可比较内容；字节码识别限定 CPython 3.7 magic 与其加载规则，有源代码回退；不执行、不反序列化 Python 代码，也不验证 Python 运行语义；Zip64、多卷、加密与非 0/8 压缩不支持 |
| 删除、撤回、禁用、恢复 | 常规往返通过，整体尚不可放行 | 同戳替换、同戳改写、多实例记录覆盖见 H01～H03；关闭窗口中断操作见 M06 |
| 设置、忽略、诊断、关于 | 主要行为已接入 | 有持久配置与读取失败测试；诊断版本及项目链接仍有缺口，见 B01、L01 |
| 报错日志分析、健康检查、二分排查、一键体检、Tray 检测 | 不属于已交付功能 | 代码与 README 明确未开放；不因这些计划功能未实现而单独阻断范围明确的 alpha，发布说明必须保持这一边界 |

### 并发、取消、异常与资源预算

- Package 索引读取默认并发 `clamp(CPU, 1, 4)`；资源内容与 Script 默认并发均为 2。桌面冲突流程依次执行索引、资源内容、脚本、整文件重复核对、会话投影；没有将四种扫描同时全量启动。
- Package 预检单文件上限 8 GiB、资源数上限 1,000,000（[PackageSafetyLimits.cs:7–20](../../src/Sims4ModSieve.Core/Packages/PackageSafetyLimits.cs#L7)）；资源内容单条存储 64 MiB、解压输出 256 MiB。
- **资源内容总字节预算默认是 `long.MaxValue`，不是 4 GiB**（[ResourceContentLimits.cs:14–43](../../src/Sims4ModSieve.Core/Conflicts/ResourceContentLimits.cs#L14)）。共享预算计数、预留/结算有测试，但默认配置不能视为整次运行有固定 IO/CPU 上限。
- Script 单归档 256 MiB、条目限制配置值 65,535、单模块输出 16 MiB、共享输出预算 1 GiB（[ScriptModuleLimits.cs:13–42](../../src/Sims4ModSieve.Core/Scripts/ScriptModuleLimits.cs#L13)）；当前 ZIP 解析将 EOCD 的 `0xffff` 视为 Zip64 标记而拒绝，不能据配置值保证恰好 65,535 条的普通 ZIP 兼容性。
- 流式哈希/解压限制了单条内容缓冲，不限制整个扫描的路径、索引、资源关系和 UI 模型总内存；不同内容组之间的文件配对仍可能接近平方增长，见 M02。
- 单文件预期读取失败转为结构化未完成；取消向上传播。索引与 Script 的外围隔离明确不吞 `OutOfMemoryException`，不能据此承诺内存耗尽后仍可正常继续。命令层有异常提示/日志，启动层有错误提示；没有发现全局 Dispatcher 未处理异常兜底，不能把这些局部处理当作全局无崩溃保证。
- 普通读取/解压循环可取消，但第三方打开 package 的阶段没有 token；Shell `PerformOperations` 中途也没有连接取消回调。文件动作不宜在部分成功后简单抛取消而遗失成功清单，见 M04、M06。

## 未通过项与建议修复

### Blocker

**B01 · 没有完成可复现、可识别版本且经干净 Windows 验证的分发包。**

- **已验证事实**：唯一工作流 [.github/workflows/ci.yml:31–56](../../.github/workflows/ci.yml#L31) 只有还原、构建、测试及失败诊断上传；该成功 CI run 的 artifacts 数为 0。GitHub releases API 返回空列表。没有发布脚本或 publish profile。本次默认 publish 成功，但输出要求 `Microsoft.WindowsDesktop.App 8.0`，不是自包含程序。
- **已验证事实**：[Directory.Build.props:2–11](../../Directory.Build.props#L2) 与 [Desktop.csproj:2–10](../../src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj#L2) 没有设置 alpha 版本；[SettingsSupportService.cs:36](../../src/Sims4ModSieve.Desktop/Services/SettingsSupportService.cs#L36) 读取 AssemblyVersion，探针实际显示 `1.0.0.0`。[README.md:27–32](../../README.md#L27) 面向开发要求 SDK，没有说明普通用户对应的运行时与安装方式。
- **待人工验证**：没有无 SDK、无开发缓存的 Windows 10/11 目标环境记录。不能用本机 publish 或 `windows-latest` 构建测试替代最终 ZIP/安装包验证。
- **建议**：明确 x64 等支持架构及便携 ZIP/安装包方案；选择框架依赖（明确 Desktop Runtime 前提）或自包含模式；建立固定命令、版本和提交标识、文件清单、校验和及发布说明，包含已知限制和恢复操作。对**同一个最终包**完成干净系统启动、扫描、临时文件往返和升级验证。可以采用人工发布规程，自动创建 Release、安装器和签名都不是本审计强加的前提；本次不执行这些动作。

**B02 · 分发目录没有附齐第三方许可证和版权文本。**

- **已验证事实**：[THIRD-PARTY-NOTICES.md:11–37](../../THIRD-PARTY-NOTICES.md#L11) 列出包名、版本、许可证名称和链接；[Desktop.csproj:13–16](../../src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj#L13) 只复制项目 MIT、该清单与字体 OFL。实际 publish 包含 SharpZipLib、LlamaLogic、ImageSharp、Nito、YamlDotNet 等 DLL，却没有对应完整许可证集合。本项目自身 MIT 文本不能代替依赖作者版权和授权文本。
- **依据**：已核对 [SharpZipLib v1.4.2 许可证](https://github.com/icsharpcode/SharpZipLib/blob/v1.4.2/LICENSE.txt) 的版权与许可保留要求。ImageSharp 的 [v3.1.11 分离许可证](https://github.com/SixLabors/ImageSharp/blob/v3.1.11/LICENSE) 支持本项目目前采用 Apache-2.0 的条件，但仍应履行 [Apache-2.0 第 4 节](https://www.apache.org/licenses/LICENSE-2.0) 的再分发要求；许可证兼容不等于随包义务已完成。
- **建议**：按实际分发 DLL/运行时收集对应版本的完整 LICENSE、版权及适用 NOTICE，放入随包 `licenses/` 或合并的许可文件，并验证 publish/压缩包确实携带；保留字体 OFL。自包含方案还需核对随包 .NET 运行时通知。修正声明第 41 行：本地 ImageSharp 3.1.11 nuspec 实际含 `<license type="file">LICENSE</license>`；许可证原文应固定版本链接。

### High

**H01 · 删除重复文件前不复核当前内容，扫描后的同戳改写会被放行（Issue #24）。**

- **位置**：[FileActionService.cs:6–11、150–165、180–211](../../src/Sims4ModSieve.Desktop/Services/FileActionService.cs#L150)；[MainWindowViewModel.cs:545–559](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.cs#L545)。候选没有扫描时 SHA-256，预检与执行前都只比较长度/修改时间。
- **已验证事实**：探针先记录 `AAAA`，随后写入同长度 `BBBB` 并恢复原修改时间，得到 `CanExecute=True; SentToAdapter=1; Content=BBBB`。模拟回收站收到删除请求；没有对真实文件执行删除。
- **影响/推断**：用户确认依据的是旧重复分组，当前文件可能已含独有内容，仍会被移入回收站。回收站可恢复不能替代操作正确性保证。
- **建议**：将扫描 SHA-256 带入重复删除候选，在实际动作前流式复核当前内容；不匹配或无法读取则阻止该文件并报告，多文件结果准确区分成功/失败。补同戳改写、读取失败、部分成功和额外 IO 成本测试；结合 H02 控制校验至操作的竞态。
- **发布判断**：alpha.1 前必须处理，属于发布阻塞问题。

**H02 · 缺少稳定文件身份，路径上的同戳替换不能识别（Issue #23）。**

- **位置**：[FileActionService.cs:51–59、161–165](../../src/Sims4ModSieve.Desktop/Services/FileActionService.cs#L51)；[ModDisableService.cs:144–152、192–193、297–315](../../src/Sims4ModSieve.Desktop/Services/ModDisableService.cs#L297)；[FileSystemAccess.cs:25–35](../../src/Sims4ModSieve.Core/Duplicates/FileSystemAccess.cs#L25)。没有卷标识/文件 ID，也没有将验证句柄与后续按路径执行的 Shell/改名动作关联。
- **已验证事实**：探针将原文件移到另一名称，再在原路径创建不同文件并设置同长度、同修改时间，预检仍 `CanExecute=True`。
- **代码推断**：禁用/恢复也只有文件戳校验。冲突行禁用的 [ConflictActions.cs:109–115](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.ConflictActions.cs#L109) 仅用内存会话核对原行；[FileManagement.cs:231–239](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.FileManagement.cs#L231) 只传路径，禁用服务读取当下元数据建新记录，未用原扫描戳验证磁盘对象。文件身份变化和内容变化是两类风险，文件 ID 不能代替 H01 的哈希。
- **建议**：采集受支持文件系统上的卷标识与文件 ID，定义不可获取身份时的保守策略；文件操作前比较原快照及当前身份/文件戳，尽量缩短并约束验证至操作的窗口。覆盖改名、替换、reparse point、不可访问、非 NTFS 等情况；冲突行操作携带原扫描快照。
- **发布判断**：alpha.1 前必须处理，属于发布阻塞问题。

**H03 · 多实例可以覆盖删除历史，使工具内恢复记录丢失。**

- **位置**：[FileActionService.cs:269–281](../../src/Sims4ModSieve.Desktop/Services/FileActionService.cs#L269) 缓存 `history`，保存时用缓存整体替换 `Deleted`；[FileManagementStore.cs:14、41–54](../../src/Sims4ModSieve.Desktop/Services/FileManagementStore.cs#L41) 的锁仅属于单个实例；[App.xaml.cs:12–19](../../src/Sims4ModSieve.Desktop/App.xaml.cs#L12) 没有单实例入口约束。[ModDisableService.cs:21、279–287](../../src/Sims4ModSieve.Desktop/Services/ModDisableService.cs#L279) 的读改写锁同样不跨实例/进程。
- **已验证事实**：两份服务实例共享同一临时历史目录，分别先载入，再模拟删除不同文件；第二次保存后历史仅剩 1 条，第一条消失：`FirstDeleteCount=1; AfterSecondDeleteCount=1; FirstRecordRetained=False`。
- **代码推断**：实际启动两个应用进程可走同一竞态；禁用清单也存在并发覆盖风险。探针证明历史丢失，未证明回收站中的文件本身丢失。
- **建议**：alpha 最小方案可对同用户数据目录实行单实例；或以跨进程锁、重新读取和合并事务保证数据更新。补两实例交错删除/恢复、禁用与重新加载测试。
- **发布判断**：涉及恢复能力，alpha.1 前必须处理。

**H04 · 随包 ImageSharp 3.1.11 命中当前已知安全公告，CI 没有相应门禁。**

- **位置**：[Packages/packages.lock.json:123](../../src/Sims4ModSieve.Packages/packages.lock.json#L123)、[Packages.csproj:8](../../src/Sims4ModSieve.Packages/Sims4ModSieve.Packages.csproj#L8)、[ci.yml:40–47](../../.github/workflows/ci.yml#L40)。本次 publish 实际带有 `SixLabors.ImageSharp.dll`。
- **已验证事实**：NuGet 查询报告 4 条 High、2 条 Moderate：[v76p-62qx-wwq2](https://github.com/SixLabors/ImageSharp/security/advisories/GHSA-v76p-62qx-wwq2)、[j3p4-wp97-rph4](https://github.com/SixLabors/ImageSharp/security/advisories/GHSA-j3p4-wp97-rph4)、[j9gm-c75j-xc9q](https://github.com/SixLabors/ImageSharp/security/advisories/GHSA-j9gm-c75j-xc9q)、[jjfr-hcj7-qf5w](https://github.com/SixLabors/ImageSharp/security/advisories/GHSA-jjfr-hcj7-qf5w)、[gwg2-r3hj-4w44](https://github.com/SixLabors/ImageSharp/security/advisories/GHSA-gwg2-r3hj-4w44)、[wmxv-xphr-5c9g](https://github.com/SixLabors/ImageSharp/security/advisories/GHSA-wmxv-xphr-5c9g)。上游公告列出的 v3 修复版本为 3.2.0。
- **代码推断与边界**：当前业务只调用 LlamaLogic 的只读索引与元数据，资源内容自行哈希，没有发现 ImageSharp 图像解码、TIFF 编码或图像处理入口。**依赖受影响已确认；本应用可利用性未确认**，不能据此声称扫描任意 package 就能利用漏洞，也不能把未调用的推断当成已完成安全豁免。
- **建议**：优先通过兼容的上游依赖升级或受控依赖解析使用修复版本，重跑 DBPF 与发布验证；重新核对许可证条件和 lock 文件。在 CI 显式查询并判定传递漏洞，不能只检查该命令退出码。若以不可达性处置，需要留存针对实际第三方调用链的证据。
- **发布判断**：当前依赖安全检查未通过，alpha.1 前应解决。

### Medium

**M01 · 真实 Package/Script 兼容性回归不可重复确认（Issue #25）。**

- **位置**：[RealCorpusTests.cs:20](../../tests/Sims4ModSieve.Packages.Tests/RealCorpusTests.cs#L20)、[ConflictGroupingRealCorpusTests.cs:16–27](../../tests/Sims4ModSieve.Packages.Tests/ConflictGroupingRealCorpusTests.cs#L16)、[ScriptRealCorpusTests.cs:25–30](../../tests/Sims4ModSieve.Core.Tests/Scripts/ScriptRealCorpusTests.cs#L25)、[Packages README:103–122](../../src/Sims4ModSieve.Packages/README.md#L103)；本次与 CI 均跳过 4 项真实语料测试。
- **已验证事实**：存在可选本地语料入口，并非完全没有真实样本测试；但没有受控样本清单、每样本预期成功/失败类别及可用的固定对照资料。分组回归还依赖未跟踪的 `artifacts/conflict-grouping-v2-statistics.json`。文档中“95 个 package 通过”是历史记录，本次未复验。
- **建议/判断**：建立可合法获得的版本固定语料清单及阶段预期，覆盖受支持/不受支持压缩和索引，并做可信工具逐项对照。本项单独不阻断范围明确的 alpha；不能在关闭它之前宣称广泛兼容。

**M02 · 缺少规模与峰值内存基线，总体资源增长没有硬上限（Issue #26）。**

- **位置**：[ScanPerformance.cs:37–60](../../src/Sims4ModSieve.Core/Diagnostics/ScanPerformance.cs#L37)、[SourceFileDiscovery.cs:123–160](../../src/Sims4ModSieve.Core/Duplicates/SourceFileDiscovery.cs#L123)、[PackageConflictScanner.cs:184、263–292](../../src/Sims4ModSieve.Core/Conflicts/PackageConflictScanner.cs#L263)、[ConflictPairSummaryBuilder.cs:185–240、389–425](../../src/Sims4ModSieve.Core/Conflicts/ConflictPairSummaryBuilder.cs#L389)、[ResourceContentLimits.cs:33–37](../../src/Sims4ModSieve.Core/Conflicts/ResourceContentLimits.cs#L33)。
- **已验证事实**：已有分阶段耗时、机器/CPU/存储、文件数、输入字节及处理字节诊断，不能说“完全没有性能观测”；没有固定的小/中/大基准集，也未测峰值内存、冷/热缓存差异与吞吐基线。
- **代码推断**：扫描保留整体路径/索引集合，不同内容组的多文件关系可能平方展开；单条流式上限不能保护整体内存。默认内容总预算近似不限。尚未实测 OOM 或给出容量承诺。
- **建议/判断**：保留现有计时作为基准基础，补固定样本规模、运行时版本、峰值内存、重复运行与取消延迟，评估候选/文件对数量上限及可解释的未完成结果。单独不阻断小范围 alpha，但发布说明需限定已验证规模。

**M03 · ZIP 重叠与矛盾布局缺少专门回归样本（Issue #27）。**

- **位置**：[ScriptZipDirectory.cs:71–80、101–104、231–266、273–295](../../src/Sims4ModSieve.Core/Scripts/ScriptZipDirectory.cs#L71)；[ScriptArchiveRegressionTests.cs:42、154、295、338](../../tests/Sims4ModSieve.Core.Tests/Scripts/ScriptArchiveRegressionTests.cs#L42)。
- **已验证事实**：已有 CRC/长度、伪造目录数量、非法 Huffman、descriptor、压缩流截断及取消测试；代码已经计算下一局部头/中央目录的边界并拒绝越界。没有找到专门覆盖中央目录与数据重叠、多条目共享/重叠数据区、局部头偏移和字段互相矛盾的完整样本矩阵。
- **建议/判断**：按 ZIP 规范构造小型异常与合法非典型对照样本，断言结构化错误、健康同归档模块可用、资源消耗有界。当前是测试缺口，未证实绕过或无界消耗，**不单凭 Issue 文字升级为 Blocker**；若补测发现解析接受歧义数据或无界循环，应升 High/Blocker。

**M04 · 第三方 Package 打开阶段不能即时取消。**

- **位置**：[LlamaLogicPackageIndexReader.cs:70–78](../../src/Sims4ModSieve.Packages/LlamaLogicPackageIndexReader.cs#L70)；[MainWindowViewModel.Conflicts.cs:413–425](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.Conflicts.cs#L413)。
- **代码事实/推断**：`FromPathAsync(fullPath, forReadOnly: true)` 没有 CancellationToken，前后检查 token 只能阻止后续工作；用户取消时可能要等当前打开/解析结束。其他循环的取消测试不能覆盖此等待上界。
- **建议/判断**：针对最大允许索引和慢盘实测取消延迟；优先使用可取消的上游入口或调整读取方案。不要只用 `WaitAsync(token)` 留下无人管理的后台 IO。当前不单独阻断 alpha，但应记录可接受的等待范围。

**M05 · 本地完整 UI 测试出现顺序或焦点相关不稳定。**

- **位置**：[ConflictRelationshipsDialogTests.cs:97–103](../../tests/Sims4ModSieve.Desktop.Tests/ConflictRelationshipsDialogTests.cs#L97) 与 [同文件:379–386](../../tests/Sims4ModSieve.Desktop.Tests/ConflictRelationshipsDialogTests.cs#L379)，关联 [WpfTestHost.cs:74–77](../../tests/Sims4ModSieve.Desktop.Tests/WpfTestHost.cs#L74)。
- **已验证事实**：正常权限第一次完整运行的 `GroupHeadingAndFileTextAreasRouteToTheirActions`、`FileActionsHaveRectangularHitAreasAndTechnicalTableStaysInline` 在空格 KeyDown 后断言 `IsPressed` 失败；同一测试类单跑 10 项全通过，第二次原样完整测试也全部通过。CI 同 SHA 全通过。
- **待确认原因**：可能为桌面焦点/激活或测试间输入状态，尚未证明是产品键盘交互缺陷；最终完整复核已通过，但不能据此删除首次失败记录、放宽断言或认定原因已经解决。
- **建议/判断**：检查键盘/鼠标状态、激活和 Dispatcher 等待，保存失败截图与输入诊断，并通过固定条件复现。本轮有完整通过记录，本项作为不稳定性跟踪，不单独阻断；最终发布候选仍需原样全量通过。

**M06 · 关闭窗口与部分文件动作的收尾缺少保护证据。**

- **位置**：[MainWindow.xaml.cs:16–22](../../src/Sims4ModSieve.Desktop/MainWindow.xaml.cs#L16)、[WindowsRecycleBinAdapter.cs:111–124、177–179、237–261](../../src/Sims4ModSieve.Desktop/Services/WindowsRecycleBinAdapter.cs#L237)、[FileActionService.cs:208–235、247–267](../../src/Sims4ModSieve.Desktop/Services/FileActionService.cs#L208)。
- **代码推断**：没有 Closing 时等待/阻止关闭的逻辑，Shell STA 是后台线程；普通删除历史在整批完成后保存。窗口关闭导致进程退出，或服务调用在部分恢复后取消，可能使已经完成的动作没有对应完整记录。禁用路径已有预记账，不能据此推断普通删除也有相同保障。
- **建议/判断**：对正在执行的文件动作提供可完成收尾的退出生命周期，并持久化操作意图/已完成项；部分取消须返回或持久保存成功项。仅用临时文件验证关闭/进程中断恢复。当前没有复现丢失记录，列为 Medium；发布前需完成这一恢复验证。

**M07 · .NET 8 即将结束支持，发行运行时策略尚未确定。**

- **位置**：[Directory.Build.props:3](../../Directory.Build.props#L3)、[global.json:3–4](../../global.json#L3)、[Desktop.csproj:4](../../src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj#L4)。
- **外部已核实事实**：微软列明 .NET 8 于 **2026-11-10** 结束支持，距审计日仅 8 天，见 [.NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。本机运行时 8.0.30 不构成发布应使用的最新补丁证据。
- **建议/判断**：决定 alpha 的支持时间窗、运行时更新与后续受支持目标框架计划。自包含包需负责运行时补丁更新，框架依赖包需说明 Desktop Runtime 前提。当前并非已经 EOL，但不能把 .NET 8 作为长期维护承诺。

### Low

**L01 · 程序内 GitHub 与反馈链接仍使用旧仓库名。**

- **位置**：[SettingsSupportService.cs:17、110](../../src/Sims4ModSieve.Desktop/Services/SettingsSupportService.cs#L17)，[SettingsPage.xaml:464、478–479](../../src/Sims4ModSieve.Desktop/SettingsPage.xaml#L464)。
- **已验证事实**：常量与探针输出为 `https://github.com/stella-wood/Sims4ModDoctor`，当前 remote 与 README 是 `Sims4ModSieve`；未依赖旧地址重定向是否继续有效作判断。
- **建议/判断**：统一指向当前项目和 issues，更新目标断言测试；不单独阻断主要扫描流程。

## Issues #23～#27 的发布影响

以下均通过 GitHub 连接读取了当前 Issue 正文与状态，五项均为 **open、0 comments**；没有只按标题判断，也没有修改 Issue。

| Issue | 当前核对结果 | 本次级别 | 是否阻断 alpha.1 |
|---|---|---|---|
| [#23 文件身份校验](https://github.com/stella-wood/Sims4ModSieve/issues/23) | 同戳物理替换通过预检，文件操作无身份关联；H02 | High | **是**，文件操作对象正确性要求 |
| [#24 删除前内容复核](https://github.com/stella-wood/Sims4ModSieve/issues/24) | 同戳内容改写仍传入删除适配器；H01 | High | **是**，不能按旧重复证据删除新内容 |
| [#25 DBPF 真实回归集](https://github.com/stella-wood/Sims4ModSieve/issues/25) | 有可选真实测试，但未形成固定样本与预期矩阵，本轮/CI 未运行；M01 | Medium | 不单独阻断有限范围 alpha；阻止广泛兼容声明 |
| [#26 性能与占用基准](https://github.com/stella-wood/Sims4ModSieve/issues/26) | 分阶段计时已存在；可重复规模、峰值内存和冷热缓存基线缺失；M02 | Medium | 不单独阻断限定规模 alpha；不能作大库容量承诺 |
| [#27 ZIP 重叠边界测试](https://github.com/stella-wood/Sims4ModSieve/issues/27) | 已有实际边界/CRC 保护及部分回归，重叠矩阵未覆盖；M03 | Medium | 当前未证明独立阻断；发现实际越界/无界消耗后升级 |

## Windows 干净环境与待人工验证

以下未执行，属于发布证据缺口，不能勾为通过：

1. 在声明支持的 Windows 10/11 版本和架构上，用普通用户、无 SDK/无仓库缓存运行最终分发包；分别确认缺少 Desktop Runtime 时的指引，以及所选部署模式下离线运行条件。
2. 最终包的解压/安装、首次启动、再次启动、配置持久化、升级与卸载/移除；仅在用户可写目录写入数据。MSI/MSIX 安装器不是便携 ZIP 方案的必需品。
3. 在专用测试卷上验证回收站关闭、空间/策略受限、不支持回收站、文件锁定、权限不足与路径变动时保守失败；没有更改当前用户的回收站策略。已通过的 3 项常规真实往返不能覆盖这些条件。
4. 在 H01～H03 修复后，用临时文件验证同戳改写、物理替换、跨实例、同名碰撞和部分成功；再验证文件动作进行中关闭应用、取消服务调用及中断后的恢复。
5. 受控真实 Package/Script 清单及可信工具对照；长 Unicode/长路径的最终包验证；规模、峰值内存、慢盘和取消延迟。
6. 声明支持的 DPI/显示器配置下，真实键盘、鼠标、展开/筛选/关系弹窗与最低窗口尺寸交互。现有 WPF 行为测试已有价值，但本轮出现的完整测试不稳定仍需解释。

## 发布建议

本轮判定 **No-Go**。先完成 B01/B02，修复 H01～H03，处置 H04，再在同一候选提交与最终分发包上保留完整构建、测试及干净环境证据。M05 的完整测试稳定性与 M06 的中断恢复验证也不能以单项测试成功代替。

有限 alpha 可以明确保留 RefPack、Zip64、游戏实际加载顺序、真实语料与大规模性能等已知限制，但不能以“alpha”名义省略文件操作对象正确性、恢复记录可靠性、分发许可证或运行条件验证。本报告没有授权或执行后续修复与发布。
