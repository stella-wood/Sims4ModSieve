# Sims 4 Mod Sieve

[![CI](https://github.com/stella-wood/Sims4ModDoctor/actions/workflows/ci.yml/badge.svg)](https://github.com/stella-wood/Sims4ModDoctor/actions/workflows/ci.yml)

Sims 4 Mod Sieve 是一个面向 Windows 的本地离线《模拟人生 4》Mod 整理工具。

当前版本提供重复文件检测与基础冲突排查。扫描和报告生成默认只读；重复检测中只有用户明确勾选并确认后，桌面版才会把文件移入 Windows 回收站。冲突页不提供删除或禁用操作。

## 当前功能

- 扫描一个或多个目录中的 `.package` 和 `.ts4script` 文件
- 识别文件名不同但内容完全相同的副本
- 正确处理同时选择父目录和显式子目录的情况
- 跳过 reparse point，避免意外扩大扫描范围
- 先按文件大小筛选，再流式计算 SHA-256
- Hash 前后复核文件状态，避免使用扫描期间发生变化的内容
- 单个目录或文件读取失败时继续扫描其他内容
- 按稳定规则提供建议保留项和建议删除项
- 支持逐文件、按组和一键应用建议
- 将明确选择的文件移入 Windows 回收站
- 支持部分失败报告和最近一次删除撤回
- 通过 CLI 导出 JSON 和静态 HTML 报告

Mod 冲突检测首次从首页或顶部导航进入四功能总览，同次运行再次进入时恢复上次页面。「基础排查」比较 package 资源和脚本模块，按资源／模块展示默认收起的卡片，直接列参与文件、原因和按需展开的技术详情；漏斗支持状态与类型复选筛选，独立未完成项合并在结果列表中。具体状态、技术详情和虚拟化规则见 [冲突检测页面方案](docs/design/冲突检测页面方案-v1.md)。报错日志分析、健康性检查、二分排查仍显示「尚未开放」。两种检测的文件夹范围和勾选状态各自独立，同次运行期间切换页面保留；清空范围只重置当前检测的结果。完全关闭再打开，两页恢复默认范围，不恢复上次添加的目录或勾选，当前没有持久化扫描历史。

## 系统要求

- Windows 10 或 Windows 11
- .NET SDK 8.0.424 或兼容的 .NET 8 SDK

仓库的 `global.json` 固定了开发所使用的 SDK 版本。`eng\dotnet.cmd` 会优先使用项目内的本地 SDK；本地 SDK 不存在时会自动使用系统 `dotnet`。

## 构建与测试

每次推送到 `main` 或提交 Pull Request 时，GitHub Actions 都会在 Windows 上执行锁定还原、Release 构建和普通测试。

CI 失败时上传 `test-failure-diagnostics` 工件，包含 TRX 测试结果、界面场景截图和异常发生时的窗口截图，保留 7 天；原生输入失败的测试输出同时记录工作区、DPI、目标／实际鼠标坐标与命中控件。原生鼠标测试只调整测试窗口到当前工作区，并在确认实际命中后点击或拖动，不改变应用的默认窗口尺寸。

测试范围与断言边界统一遵循 [AGENTS.md 的测试规则](AGENTS.md#10-完成标准)。冲突页截图可通过 `eng/render-conflict-review.ps1` 保存到 `artifacts/conflict-review/`，供人工查看。

```powershell
.\eng\dotnet.cmd restore .\Sims4ModSieve.sln --locked-mode
.\eng\dotnet.cmd build .\Sims4ModSieve.sln -c Release --no-restore --nologo -m:1
.\eng\dotnet.cmd test .\Sims4ModSieve.sln -c Release --no-build --no-restore --nologo -m:1
```

真实 Windows 回收站往返测试默认跳过，因为它会对自动创建的临时文件执行一次回收站删除和恢复。明确需要验证时运行：

```powershell
$env:SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST='1'
.\eng\dotnet.cmd test .\tests\Sims4ModSieve.Desktop.Tests\Sims4ModSieve.Desktop.Tests.csproj `
  -c Release --no-build --no-restore --nologo `
  --filter 'FullyQualifiedName~UnicodeFileCanRoundTripThroughWindowsRecycleBin'
```

该测试只使用系统临时目录中的自动生成文件，不会访问真实 Mods 文件夹。

## 运行桌面版

```powershell
.\eng\dotnet.cmd run --project .\src\Sims4ModSieve.Desktop\Sims4ModSieve.Desktop.csproj -c Release
```

Release 构建输出位于：

```text
src\Sims4ModSieve.Desktop\bin\Release\net8.0-windows\
```

桌面程序名为 `Sims4ModSieve.exe`。顶部「设置」提供文件夹、诊断与反馈、关于三个分类。可立即保存共用默认 Mods 与独立 Tray 位置，打开目录，或查找并确认采用系统文档下的默认目录；Tray 仅保存位置，图库检测尚未开放。设置页修改默认 Mods 不改变已添加的扫描范围；检测页原有「更改」仍将新位置加入当前页。配置兼容和会话规则见 [设置页面方案](docs/design/设置页面方案-v1.md)。

配置文件 `duplicate-settings.json` 与本工具错误日志保存在 `%LOCALAPPDATA%\Sims4ModSieve\`，不会写入 Mods 文件夹。「诊断与反馈」仅查看本工具的运行及启动日志，支持有界读取、路径脱敏和复制；无日志也可复制实际版本与系统信息。关于页可读取随构建输出的 MIT 许可证和第三方声明。GitHub 仅在点击链接时通过系统浏览器打开，无自动联网。

## CLI

```powershell
.\eng\dotnet.cmd run --project .\src\Sims4ModSieve.Cli\Sims4ModSieve.Cli.csproj -c Release -- `
  scan-duplicates `
  --source "D:\Documents\Electronic Arts\The Sims 4\Mods" `
  --source "D:\Downloads\New Mods" `
  --mods-root "D:\Documents\Electronic Arts\The Sims 4\Mods" `
  --extension .package `
  --extension .ts4script `
  --json ".\artifacts\duplicate-report.json" `
  --html ".\artifacts\duplicate-report.html"
```

省略 `--extension` 时 CLI 会扫描全部文件。CLI 不执行删除或修改；存在未完成分析时仍会保存报告，并以退出码 `1` 结束。

## 项目结构

```text
src/
├─ Sims4ModSieve.Core/          文件发现、Hash、重复分组、资源冲突、脚本碰撞和报告
├─ Sims4ModSieve.Packages/      DBPF 只读目录读取（LlamaLogic.Packages 适配）
├─ Sims4ModSieve.Cli/           命令行扫描与报告入口
└─ Sims4ModSieve.Desktop/       WPF 界面和 Windows 回收站适配

tests/
├─ Sims4ModSieve.Core.Tests/    核心扫描与报告测试
├─ Sims4ModSieve.Packages.Tests/ DBPF 读取测试
└─ Sims4ModSieve.Desktop.Tests/ ViewModel、文件动作和 WPF 交互测试

eng/
├─ dotnet.cmd                    统一 .NET 命令入口
├─ generate-brand-icon.ps1       应用图标生成脚本
└─ render-conflict-review.ps1    冲突页离屏渲染
```

本地构建、渲染截图、临时样本和验收记录放在 `artifacts/`，由 `.gitignore` 排除，不作为项目文档提交。

## 安全与隐私

- 扫描、分析和报告生成全部在本机完成
- 不上传 Mod、文件路径或扫描报告
- 不执行 `.ts4script` 中的代码
- 不修改 package 内部内容
- 不提供永久删除降级路径
- HTML 报告会转义文件名、路径和错误文本
- 导出的报告可能包含本地完整路径，分享前请自行检查

## 第三方组件

依赖与许可证信息见 [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)。

## 许可证

本项目采用 [MIT License](./LICENSE)。
