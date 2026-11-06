# 便携包解压位置与扫描修复验收

验收日期：2026-11-06。候选包验收时源码基于 `e98570b` 的未提交工作树，包内 `BUILD-INFO.txt` 记录 `Dirty: True`。未创建 Tag 或 GitHub Release。

## 原因与修复

原保护为 Issue #29 加入，目的是避免程序放进扫描范围后，其自身文件被误删、禁用或覆盖。`ApplicationDirectoryGuard.Contains` 原来把程序所在目录的所有内容判为受保护；三种扫描共用的 `SourceFileDiscovery` 在遍历前直接跳过整个目录，再产生 `application-directory-skipped` 记录。EXE 散落在桌面时，选择桌面就把真实 Mod 一起排除了，范围问题又被展示成读取失败。

现在只识别程序文件和资源目录，具体规则统一见[程序文件保护](application-directory-safety.md)。同目录及普通子目录的 Mod 正常扫描。混放提示随已勾选来源和设置更新，不影响扫描命令。空目录和只有程序文件的目录显示「这个文件夹里没有找到 mod 文件」；实际权限错误保留原始原因，损坏文件保留结构化问题。冲突页同时修复了只读 Detail、丢失 Message 中权限原因的问题。

ZIP 生成改为压入完整包文件夹，校验清单同步带顶层前缀；CI 已调用同一个打包及回归脚本，没有另写压缩逻辑，因此无需修改 workflow。

## 改动文件

| 文件 | 改动 |
|---|---|
| [ApplicationDirectoryGuard.cs](../../src/Sims4ModSieve.Core/Duplicates/ApplicationDirectoryGuard.cs) | 保护已知程序文件、资源目录及程序文件的 .off 路径；提供规范化目录相等判断 |
| [SourceFileDiscovery.cs](../../src/Sims4ModSieve.Core/Duplicates/SourceFileDiscovery.cs) | 保留程序目录遍历，只排除程序内容，不产生伪读取失败 |
| [DuplicateScanner.cs](../../src/Sims4ModSieve.Core/Duplicates/DuplicateScanner.cs) | 完成进度区分没有 Mod 与存在失败 |
| [SettingsSupportService.cs](../../src/Sims4ModSieve.Desktop/Services/SettingsSupportService.cs) | 同目录位置建议，纳入用户勾选的来源 |
| [MainWindowViewModel.cs](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.cs) | 重复检测空结果、失败原因和来源变化通知 |
| [MainWindowViewModel.Settings.cs](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.Settings.cs) | 位置提示使用现有设置与来源状态 |
| [MainWindowViewModel.Conflicts.cs](../../src/Sims4ModSieve.Desktop/ViewModels/MainWindowViewModel.Conflicts.cs) | 冲突检测空结果、失败状态及异常原因 |
| [ConflictResultSession.cs](../../src/Sims4ModSieve.Desktop/ViewModels/ConflictResultSession.cs) | 没有 Detail 时保留 Message 中的失败原因 |
| [package-portable.ps1](../../eng/package-portable.ps1) | ZIP 保留包名文件夹，更新文件清单比较 |
| [test-portable-bundle.ps1](../../eng/test-portable-bundle.ps1) | 增加 ZIP 顶层结构回归检查 |
| [ApplicationDirectoryGuardTests.cs](../../tests/Sims4ModSieve.Core.Tests/ApplicationDirectoryGuardTests.cs) | 文件保护边界与程序内容分类 |
| [DuplicateScannerTests.cs](../../tests/Sims4ModSieve.Core.Tests/DuplicateScannerTests.cs) | 混放、嵌套、显式来源和程序内容排除 |
| [PackageConflictScannerTests.cs](../../tests/Sims4ModSieve.Core.Tests/Conflicts/PackageConflictScannerTests.cs) | 同目录 package 送达读取器，资源目录不参与 |
| [ScriptModuleScannerTests.cs](../../tests/Sims4ModSieve.Core.Tests/Scripts/ScriptModuleScannerTests.cs) | 同目录脚本正常比较，程序资源不送入归档读取器 |
| [ApplicationDirectorySafetyTests.cs](../../tests/Sims4ModSieve.Desktop.Tests/ApplicationDirectorySafetyTests.cs) | 文件操作继续保护程序内容，同目录 Mod 不受误拦截 |
| [PortableScanLocationTests.cs](../../tests/Sims4ModSieve.Desktop.Tests/PortableScanLocationTests.cs) | 位置提示、扫描、空目录、失败原因及禁用恢复 |
| [ConflictScanFlowTests.cs](../../tests/Sims4ModSieve.Desktop.Tests/ConflictScanFlowTests.cs) | 读取失败的状态不再写成未发现冲突 |
| [README.md](../../README.md) | 同步扫描保护规则和解压入口 |
| [程序文件保护](application-directory-safety.md) | 更新保护范围、原因和提示规则 |
| [便携版使用说明](便携版使用说明.md) | 进入包名文件夹运行，说明混放仍可扫描 |
| [便携版发布流程](便携版发布流程.md) | 记录单层包文件夹与 CI 共用流程 |
| 本验收记录 | 保存测试、产物和未验证事项 |

开始前已存在的 `AGENTS.md`、`RELEASE_FIX_VERIFICATION.md` 修改没有变动。

## 自动测试

迭代时只运行受影响的 Core 和 Desktop 测试类。最后整套测试运行一次：Release 构建 0 警告 0 错误；Core 278 通过／1 跳过，Desktop 315 通过，Packages 53 通过／3 跳过，合计 **646 通过、4 跳过、0 失败**。回收站往返使用自动创建的临时文件，未操作真实 Mods。

新增测试：

| 名称 | 行为 |
|---|---|
| `OnlyProgramPayloadIsProtected` | 13 个参数场景验证程序文件、资源、语言目录、.off 保护，以及同目录 Mod／普通配置不被保护区误吞 |
| `SharedFolderNoticeTracksSourcesAndDoesNotBlockEitherScan` | 实际 WPF 窗口中来源增删及启停更新提示，两种扫描都可执行并产生预期结果 |
| `EmptyFolderShowsNoModsOnBothPages` | 纯空目录、只有程序文件两种情况，在两页都显示没有找到 Mod，且没有失败记录 |
| `ReadFailuresKeepTheirReasonsInsteadOfReportingAnEmptyFolder` | 确定性的权限失败保留原因，不冒充没有 Mod |
| `CorruptModsAreReportedAsFailuresRatherThanNoMods` | 真实临时坏 DBPF／ZIP 在冲突页保留失败与原因 |
| `ModBesideProgramCanBeDisabledAndRestored` | 程序旁的用户 Mod 可按现有身份校验规则禁用并恢复 |
| `ZipRetainsPackageFolder` | 实际 ZIP 只有与包名一致的顶级目录，EXE 位于该目录内 |

同步调整的回归：

- `MixedProgramDirectoryScansModsAndSkipsOnlyPayload`：4 个场景验证共用目录／独立子目录、隐式／显式来源，真正的 Mod 都扫描到；包括不限扩展名的扫描，程序文件不会被算作候选。
- `PackageModsBesideProgramReachReaderButProgramResourcesDoNot`：同目录及普通子目录 3 个 package 被读取，Assets／licenses 不参与。
- `ScriptModsBesideProgramReachReaderButProgramResourcesDoNot`：同目录及普通子目录 3 个脚本归档被读取，资源目录中的伪脚本不被打开。
- `DeleteRejectsProgramPayloadButAllowsNeighbouringMods`：即使绕过旧预检，程序内容仍被拒绝，同目录 Mod 仍送入文件操作适配器。
- 既有规范化边界、恢复记录保留、禁用保护、位置提示导航测试同步使用新的保护范围；失败 package 的状态断言改为明确的部分失败。

既有 bundle 检查加上 ZIP 结构，共 12 个脚本场景通过。

## 实际发布包验收

将新 ZIP 解压到全新的中文及空格路径，在实际 EXE 旁创建 3 个合成 package、2 个脚本 ZIP，再把该程序目录作为 Mods 扫描范围。通过 Windows 文件夹选择框确认路径后：

- 位置建议可见，重复扫描找到 1 组副本，并显示共查看 5 个文件；程序文件没有被算进去。
- 冲突扫描正常显示 package 与脚本结果，没有把目录保护冒充读取失败。
- 设置更改、保存和重启读取正常，外置许可证正常显示。
- 关闭进程并仅移走 5 个测试 Mod 后重新打开，两种扫描均显示没有找到 Mod。
- 测试进程设置无效 DOTNET_ROOT，使用包内运行时；没有创建指定的 bundle 解压目录。
- 原 duplicate-settings.json 已逐字节恢复，另外核对 SHA-256 一致。成功场景截图已查看。

原始日志、TRX、截图和验收工具在忽略目录 `artifacts/portable-location-fix/`，主要证据为 `final-build.log`、`final-tests.log`、`package.log`、`bundle-tests.log`、`published-ui.log`、`zip-entries.txt` 及 `published-ui/` 截图。

## 新包

路径：`artifacts/release/Sims4ModSieve-0.1.0-alpha.1-win-x64.zip`。

大小：66,260,845 字节。SHA-256：`ddc732272c3252a333c88e1a49c82faebc2fc543045f861d80d7bed454590fff`。

ZIP 内的 29 个文件与发布目录、SHA256SUMS.txt 逐个核对一致；ZIP 外部 .sha256 也一致。根目录没有散落的程序文件，结构如下：

```text
Sims4ModSieve-0.1.0-alpha.1-win-x64/
├── Sims4ModSieve.exe
├── D3DCompiler_47_cor3.dll
├── PenImc_cor3.dll
├── PresentationNative_cor3.dll
├── vcruntime140_cor3.dll
├── wpfgfx_cor3.dll
├── Assets/
├── licenses/
├── README.md
├── LICENSE
├── THIRD-PARTY-NOTICES.md
├── BUILD-INFO.txt
└── SHA256SUMS.txt
```

## 未验证

- 没有再次进入用户的干净 Windows 虚拟机；实际发布包验证在本机完成。
- 未提供真实 Mods 语料，4 项真实语料测试按既有规则跳过。
- 本次验收时远端 GitHub Actions 尚未运行；本机已验证它共用的打包和检查脚本。
