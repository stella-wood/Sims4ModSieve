# alpha.1 发布问题修复与验收

本轮以 [发布前审计](RELEASE_READINESS.md) 为原始记录，先修复已讨论的六项发布问题；原始报告保留当时的事实和结论，本文件记录修复后的验证。验收后按用户要求将修复拆分为本地 Git 提交，未创建 Tag 或 Release。

## 分工与验收

| 范围 | 实施 | 验收要求 | 状态 |
|---|---|---|---|
| H01 / #24 删除前内容复核 | file_safety | 同戳改写、无法读取阻止该文件；批次成功与失败准确，额外 IO 可说明 | 完整测试及主 agent 物理临时文件探针通过 |
| H02 / #23 文件身份与扫描快照 | file_safety | 同戳替换可识别；身份不可获取保守失败；禁用与恢复不覆盖、不放行旧快照 | 主 agent 同戳、同内容换实体和禁用/恢复探针通过；保留最终核对至系统动作间的竞态限制 |
| H03 多实例覆盖、M06 正常退出 | application_lifecycle | 第二实例不能进入写数据流程；文件动作进行中正常关闭保留完整收尾 | 跨进程锁、实际删除收尾测试通过；恢复中途取消/IO 已补逐项保存 |
| H04 依赖安全 | release_package | 依赖漏洞查询实际解析结果，不以退出 0 当通过；升级后原有分析测试通过 | LlamaLogic 4.9.0 移除 ImageSharp；最终漏洞查询与主 agent 门禁反例通过 |
| B02 许可证 | release_package | 最终包携带实际分发依赖、运行时和字体的完整适用许可与版权通知 | 许可文本已补齐并出现在最终 ZIP，逐文件哈希验收通过 |
| B01 发布包 | release_package | 正确 alpha 版本、固定构建流程、Windows x64 自包含 ZIP、文件清单与校验和 | 最终包、本机启动和打包后普通锁定还原通过；干净 Windows 10/11 仍未验证 |
| M05 UI 测试、L01 项目链接 | application_lifecycle | 不放宽断言；复核完整测试中的键盘焦点；程序链接与当前仓库一致 | 完整 UI 测试通过，版本保留 SemVer，链接已修正 |

主 agent 负责代码复查、README 整合、完整构建和测试、真实临时文件回收站往返、打包与发布输出复核。UI 测试串行运行，避免多个进程争抢桌面焦点；共享文件由明确的一方编辑。

变更集中在 Core 的 `Models.cs`、`FileSystemAccess.cs`、`PhysicalFileIdentity.cs`、`DuplicateScanner.cs`，Desktop 的文件操作/禁用服务、候选快照与会话 ViewModel、`App.xaml.cs`、`MainWindow.xaml.cs`、`SingleInstanceLock.cs` 和设置支持服务；另更新相应测试、项目和锁文件、`Directory.Build.props`、CI、两个 `eng/` 脚本、`licenses/`、README、第三方声明及设置/发布文档。没有修改扫描分类算法、选择建议规则或界面外观。

## 新增测试

| 测试名称 | 验证行为 |
|---|---|
| `DeleteRejectsSameStampContentChangeAndContinuesOtherFiles` | 同戳内容改写阻止该文件，保留其他文件的成功结果 |
| `DeleteRejectsSameStampIdentityReplacement` | 同戳更换文件实体后不发出删除请求 |
| `DeleteRejectsUnreadableContentAndKeepsOtherCandidates` | 内容读取失败保守拒绝，批次其他文件继续 |
| `PreflightRejectsUnavailableIdentityWithoutBlockingReadOnlyStamp` | 缺少身份时拒绝文件动作，保留只读元数据 |
| `DeleteRechecksEarlierFileBeforeSubmittingBatch` | 后续文件核对期间替换先前文件，批次提交前仍能剔除 |
| `PhysicalPreflightRejectsSameStampReplacement` | 真实临时文件同戳替换后预检拒绝 |
| `UndoPersistsFirstRestoredItemWhenSecondIsCanceled` | 撤回第二项取消时，第一项成功已持久化且剩余仍可撤回 |
| `RestoreDeletedPersistsFirstItemWhenSecondThrowsIo` | 历史恢复第二项 IO 失败时，第一项成功不遗留旧历史 |
| `DisableRejectsSameStampPhysicalReplacementFromScan` | 禁用拒绝已经被替换的扫描快照 |
| `RestoreKeepsLegacyRecordWithoutIdentityForManualReview` | 旧禁用记录保留，不自动操作无法证明身份的文件 |
| `SecondProcessCannotAcquireDataDirectoryUntilFirstExits` | 跨进程占用同一临时数据目录，退出后锁可重新取得 |
| `UnrelatedIoFailureIsNotTreatedAsAnotherInstance` | 非共享冲突错误向上报告，不伪装成第二实例 |
| `ClosingWaitsForBusyOperationAndClosesOnceAfterCompletion` | 忙时重复关闭只等待一次，结束后关闭 |
| `ClosingDuringDeleteWaitsForHistoryAndResultBeforeExit` | 实际 VM 临时文件删除与历史保存完成后才关闭 |

既有测试只补齐所需的真实身份、哈希和统一扫描快照，保留业务断言；两项键盘测试在发送空格前激活窗口，没有放宽按压断言。没有新增外观尺寸或像素比较断言。

## 主 agent 验收证据

- 整合阶段的普通锁定还原曾报 NU1004，发布 RID 已改为独立锁文件；没有关闭锁定还原。第一次整合完整测试有 10 项失败，日志保留在 `artifacts/release-fixes/root-tests.log`：8 项旧测试快照未携带新增身份，2 项普通界面暴露版本提交哈希。已修正测试快照和版本显示，保留原断言。
- 最终 Release 构建 0 警告、0 错误，见 `artifacts/release-fixes/root-final-build.log`；最终原样完整测试共 619 项，**615 通过、0 失败、4 跳过**，见 `root-final-tests.log` 与 `root-final-test-results/`。本次显式启用真实 Windows 回收站测试，Desktop 304/304、Core 258 通过/1 跳过、Packages 53 通过/3 跳过；4 项跳过均为既有真实语料测试。
- 主 agent 独立物理临时文件探针通过 9 项检查，见 `artifacts/release-fixes/root-acceptance-probe.log`，包括同戳改写、相同内容的同戳实体替换、旧扫描禁用拒绝及正常跨重启禁用恢复。
- 新身份信息下，3 项真实 Windows 回收站往返通过，见 `artifacts/release-fixes/root-recycle.log`。这些操作仅使用测试临时文件。
- 主 agent 给漏洞门禁注入无漏洞、传递漏洞、公告源失败但退出码为 0、空项目结果四种输入，均按预期放行或拒绝，见 `artifacts/release-fixes/vulnerability-gate-cases.log`。
- 最终打包从仓库以外的当前工作目录调用脚本，成功生成候选包；随后原样普通解决方案锁定还原再次通过，见 `root-package.log`、`root-post-package-restore.log`。当前依赖漏洞查询没有报告直接或传递漏洞，原始 JSON 位于 `artifacts/release/vulnerabilities.json`。
- 主 agent 独立读取最终 ZIP 全部 507 个清单负载文件，逐项核对 ZIP 内字节、发布目录和 SHA-256 清单，且 ZIP 外部校验和一致；`SixLabors.ImageSharp.dll` 和 `BCnEncoder.NET.ImageSharp.dll` 均已移除。见 `root-package-verification.json`。
- 最终可执行文件在 `DOTNET_ROOT` 指向不存在目录时成功创建主窗口，加载的 `coreclr.dll` 来自便携目录；真实第二个桌面程序进程退出，首实例保持；正常关闭退出码为 0，现有用户配置文件哈希未变化。见 `root-portable-startup.log`。未触发扫描或文件动作，这仍是开发机冒烟，不能替代干净系统验证。

## 最终本地候选

`artifacts/release/Sims4ModSieve-0.1.0-alpha.1-win-x64.zip`，80,591,223 字节，SHA-256：

```text
52628d74925e1a8836a9d85eb202bdf92076e0b2d7324d794562e0ace3566136
```

包内含使用说明、项目及第三方许可、自带 .NETCore / WindowsDesktop 8.0.33、逐文件清单和 `BUILD-INFO.txt`。该候选包在修复提交前构建，`SourceCommit` 记录基线提交，`Dirty=True`；后续从提交重新构建时会得到新的源码标识与包校验和。原审计报告和本验收记录不随便携包分发。

本轮代码与本机行为验收通过；**发布决定仍为暂缓**，须完成最终 ZIP 在无 SDK、无缓存的干净 Windows 10/11 x64 上的运行验证。新的 GitHub Actions 工作流仅在本地执行了等效步骤，未推送触发远端运行。

## 环境边界

当前机器是 Windows 11 x64 开发环境。已检查系统中没有 Windows Sandbox 可执行文件；本轮不安装系统组件或变更虚拟化配置。最终包在无 SDK 的干净 Windows 10/11 上运行仍需真实环境证据，不能以自包含设置或开发机启动替代。

#25 真实语料矩阵、#26 性能与峰值内存基线、#27 ZIP 专项样本，以及第三方索引打开阶段取消延迟、.NET 8 后续支持计划继续保留跟踪。本轮不把这些事项记为已修复，也不扩大为新产品功能。
