# Third-party notices

## 安装器

- Inno Setup 7.1.1 — Inno Setup License — https://github.com/jrsoftware/issrc/tree/is-7_1_1
  构建工具及生成的安装、卸载程序保留上游版权与网站信息，完整许可见 `licenses/InnoSetup-7.1.1-LICENSE.txt`。
- 中文向导使用 Inno Setup 7.1.1 包内 `Languages/ChineseSimplified.isl`，维护者 Zhenghan Yang（Kira），不另行下载或修改翻译。
  来源：https://github.com/jrsoftware/issrc/blob/is-7_1_1/Files/Languages/ChineseSimplified.isl ，随 Inno Setup 软件包按上述许可使用，原维护者署名保留于语言文件与安装器的翻译说明中。

## Runtime dependencies

`Sims4ModSieve.Packages` 引用 `LlamaLogic.Packages` 作为只读 DBPF 索引底座。
该引用被限制在这一个项目内：`Sims4ModSieve.Core`、`Sims4ModSieve.Cli` 与
`Sims4ModSieve.Desktop` 都不引用它，也不暴露它的任何类型。

### 直接依赖

- `LlamaLogic.Packages` 4.9.0 — MIT — https://github.com/Llama-Logic/LlamaLogic
- `SharpZipLib` 1.4.2 — MIT — https://github.com/icsharpcode/SharpZipLib
  （Packages 用于有界流式 zlib 解压；Core 用于脚本 ZIP 的有界 DEFLATE 解压、结束状态及 CRC 校验。
  原本也由 LlamaLogic 传递引入，版本未变，第三方类型不暴露在公开扫描接口中。）

### 传递依赖

`LlamaLogic.Packages` 自身带入以下包（版本取自
`src/Sims4ModSieve.Packages/packages.lock.json`）：

| 包 | 版本 | 许可证 |
|---|---|---|
| `BCnEncoder.Net` | 2.2.0 | MIT OR Unlicense |
| `CommunityToolkit.HighPerformance` | 8.4.0 | MIT |
| `Nito.AsyncEx` | 5.1.2 | MIT |
| `Nito.AsyncEx.Context` | 5.1.2 | MIT |
| `Nito.AsyncEx.Coordination` | 5.1.2 | MIT |
| `Nito.AsyncEx.Interop.WaitHandles` | 5.1.2 | MIT |
| `Nito.AsyncEx.Oop` | 5.1.2 | MIT |
| `Nito.AsyncEx.Tasks` | 5.1.2 | MIT |
| `Nito.Cancellation` | 1.1.2 | MIT |
| `Nito.Collections.Deque` | 1.1.1 | MIT |
| `Nito.Disposables` | 2.2.1 | MIT |
| `StbImageSharp` | 2.30.16 | Unlicense OR MIT；上游声明 Public Domain or MIT |
| `StbImageWriteSharp` | 1.16.7 | 上游声明 Public Domain |
| `YamlDotNet` | 16.3.0 | MIT |

各包按实际分发版本随附完整授权文本于 `licenses/`；`Nito.AsyncEx` 的六个拆分包共用该仓库同版本许可证。
`BCnEncoder.Net` 的两个许可文本均取自 nuspec 指定提交。StbImageSharp 的 Unlicense 与上游 README 授权声明、StbImageWriteSharp 的上游 Public Domain 声明一并提供。
`LlamaLogic.Packages` 4.9.0 不再引入 ImageSharp；其索引读取在本项目的 53 项 Packages 测试中通过。

自包含 win-x64 包同时携带 .NET 10.0.14 Runtime 的 `LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT` 与 WindowsDesktop Runtime `LICENSE`。这些文件取自发布时解析的官方运行时 NuGet 包。

升级目标框架后，`System.Collections.Immutable` 由 .NET 10 运行时提供，不再分发单独的 8.0.0 NuGet 程序集。

## 独立缓存存储依赖

`Sims4ModSieve.Caching` 独立引用以下包，版本取自该项目的 `packages.lock.json`。
桌面程序与 CLI 尚未引用该项目，当前产品未携带 SQLite 二进制。

| 包 | 版本 | 许可证 |
|---|---|---|
| `Microsoft.Data.Sqlite` | 10.0.15 | MIT |
| `Microsoft.Data.Sqlite.Core` | 10.0.15 | MIT |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.13 | Apache-2.0 |
| `SQLitePCLRaw.core` | 2.1.13 | Apache-2.0 |
| `SQLitePCLRaw.provider.e_sqlite3` | 2.1.13 | Apache-2.0 |
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.13 | Apache-2.0（包与构建）；原生 SQLite 3.53.3 为 Public Domain |

- Microsoft 两包 nuspec 指向 dotnet/dotnet 提交 `8e0f8ed2312954727f8919d7d1c2f78b483b4ee7`，
  完整 MIT 文本取自该提交的 [efcore/LICENSE.txt](https://github.com/dotnet/dotnet/blob/8e0f8ed2312954727f8919d7d1c2f78b483b4ee7/src/efcore/LICENSE.txt)，
  保存为 `licenses/Microsoft.Data.Sqlite-10.0.15-MIT.txt`；保留 .NET Foundation and Contributors 版权。
- SQLitePCLRaw 四包 nuspec 均声明 Apache-2.0、Copyright 2014-2024 SourceGear, LLC。
  上游没有 2.1.13 标签，授权与完整 NOTICE 取自 [v2.1.12](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12)，
  保存为 `licenses/SQLitePCLRaw-Apache-2.0.txt`、`licenses/SQLitePCLRaw-NOTICE.txt`，未作修改。
  NOTICE 中保留上游其他可选构建的说明，不代表本项目引入了 SQLCipher 或 OpenSSL。
- `SQLitePCLRaw.lib.e_sqlite3` 的 Linux x64 原生库实际报告 SQLite 3.53.3；
  SQLite 核心的完整授权声明取自 [version-3.53.3/LICENSE.md](https://github.com/sqlite/sqlite/blob/version-3.53.3/LICENSE.md)，
  保存为 `licenses/SQLite-3.53.3-Public-Domain.txt`。包装包的 Apache 许可与 SQLite 核心的 Public Domain 声明分别保留。

根目录 `licenses/` 已由现有发布规则随安装包复制；缓存产品接入时仍须验证 win-x64 原生库的实际分发。
缓存还通过 Core 引用既有 SharpZipLib 1.4.2，授权见上文，没有新增其他运行时传递依赖。

## Source Han Sans CN Light — SIL Open Font License 1.1

`Assets/Fonts/SourceHanSansCN-Light.otf` is bundled only for related filenames in the conflict file relationship dialog.
Copyright © 2014–2025 Adobe, with Reserved Font Name “Source”. The font is distributed unmodified;
its license is included at `Assets/Fonts/source-han-sans-LICENSE.txt` and copied to build/publish output.
Source: https://github.com/adobe-fonts/source-han-sans (the prototype's existing CN Light font asset).

## Noto Sans SC — SIL Open Font License 1.1

`Assets/Fonts/Sims4ModSieveSettingsTitle-Semibold.ttf` 取自 [Google Fonts 的 Noto Sans SC](https://github.com/google/fonts/tree/cc5661d43cd4f9aad5567033a8e15ca28dc35c86/ofl/notosanssc)。
版权归 Adobe（2014–2021），保留字体名为 “Source”。应用字体将字重固定为 600，只保留设置页右侧五个分类标题用到的字形，并以 “Sims4ModSieve Settings Title” 命名。
衍生字体继续采用 SIL Open Font License 1.1，完整许可证见 `Assets/Fonts/noto-sans-sc-LICENSE.txt`，随构建和发布产物分发。

## 感谢名单彩蛋图标

四份作者提供的 SVG 已转换为 `Assets/CreditsIcons.xaml` 内的 WPF 矢量几何，不另行分发 SVG 文件。

| 图标 | 来源 | 协议 |
|---|---|---|
| ChatGPT / OpenAI | [Simple Icons 13.21.0](https://github.com/simple-icons/simple-icons/tree/13.21.0)，`icons/openai.svg` | CC0-1.0，完整文本见 `licenses/simple-icons-CC0.txt` |
| DeepSeek | [Lobe Icons v1.45.0](https://github.com/lobehub/lobe-icons/tree/v1.45.0)，`@lobehub/icons-static-svg` 的 `icons/deepseek-color.svg` | MIT，Copyright (c) 2023 LobeHub，完整文本见 `licenses/lobe-icons-MIT.txt` |
| Clawd、星星 | 本项目绘制，作者提供 | 随项目 MIT 许可 |

图标许可仅覆盖图标文件；OpenAI、DeepSeek 标志仍为各自公司的商标。

## Test-only dependencies

- `MSTest` 4.3.3 — MIT — https://github.com/microsoft/testfx
- `Microsoft.NET.Test.Sdk` 18.9.0 — MIT — https://github.com/microsoft/vstest

These packages are used only to build and run the automated test projects; they are not runtime dependencies.
