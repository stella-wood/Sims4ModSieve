# Third-party notices

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
| `System.Collections.Immutable` | 8.0.0 | MIT |
| `YamlDotNet` | 16.3.0 | MIT |

各包按实际分发版本随附完整授权文本于 `licenses/`；`Nito.AsyncEx` 的六个拆分包共用该仓库同版本许可证。
`BCnEncoder.Net` 的两个许可文本均取自 nuspec 指定提交。StbImageSharp 的 Unlicense 与上游 README 授权声明、StbImageWriteSharp 的上游 Public Domain 声明一并提供。
`LlamaLogic.Packages` 4.9.0 不再引入 ImageSharp；其索引读取在本项目的 53 项 Packages 测试中通过。

自包含 win-x64 包同时携带 .NET 8.0.33 Runtime 的 `LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT` 与 WindowsDesktop Runtime `LICENSE`。这些文件取自发布时解析的官方运行时 NuGet 包。

## Source Han Sans CN Light — SIL Open Font License 1.1

`Assets/Fonts/SourceHanSansCN-Light.otf` is bundled only for related filenames in the conflict file relationship dialog.
Copyright © 2014–2025 Adobe, with Reserved Font Name “Source”. The font is distributed unmodified;
its license is included at `Assets/Fonts/source-han-sans-LICENSE.txt` and copied to build/publish output.
Source: https://github.com/adobe-fonts/source-han-sans (the prototype's existing CN Light font asset).

## Test-only dependencies

- `MSTest` 4.3.3 — MIT — https://github.com/microsoft/testfx
- `Microsoft.NET.Test.Sdk` 18.9.0 — MIT — https://github.com/microsoft/vstest

These packages are used only to build and run the automated test projects; they are not runtime dependencies.
