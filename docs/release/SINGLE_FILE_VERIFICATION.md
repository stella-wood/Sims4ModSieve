# win-x64 便携包单文件布局验收

验收日期：2026-11-05。候选包验收时源码基于 `09e80e4`，修改尚未提交，包内 `BUILD-INFO.txt` 如实记录 `Dirty: True`。未创建 Tag 或 GitHub Release。

## 改动文件

- [PortableWinX64.pubxml](../../src/Sims4ModSieve.Desktop/Properties/PublishProfiles/PortableWinX64.pubxml)：发布专用配置，保留简体中文和英文，将托管程序集合入 EXE，关闭原生库／内容解压、裁剪和单文件压缩，PDB 留在构建输出。
- [package-portable.ps1](../../eng/package-portable.ps1)：还原与发布使用同一 profile，更新原生文件检查、EXE 内部检查和 BUILD-INFO 布局说明。
- [verify-portable-bundle.ps1](../../eng/verify-portable-bundle.ps1)：读取实际 bundle 格式 6 的清单和运行时配置，检查关键程序集、语言、无解压／无压缩、外置文件类型。
- [test-portable-bundle.ps1](../../eng/test-portable-bundle.ps1)：在包副本上验证 11 个打包检查场景。
- [packages.win-x64.lock.json](../../src/Sims4ModSieve.Desktop/packages.win-x64.lock.json)：增加单文件兼容性分析所需的 `Microsoft.NET.ILLink.Tasks` 10.0.14；该包不进入用户包。
- [ci.yml](../../.github/workflows/ci.yml)：打包后运行检查回归脚本，调试符号另存独立工件。
- [README](../../README.md)、[便携版使用说明](便携版使用说明.md)、[发布流程](便携版发布流程.md)：同步运行方式、布局与维护规则。
- 本验收记录：保存新旧完整清单与验证结果。

原有 `AGENTS.md` 和 `RELEASE_FIX_VERIFICATION.md` 的未提交修改保持原样。没有改业务 C# 或 XAML。

## 包对比

旧包使用 PR #33 的 .NET 10.0.14 多文件包，源码提交同为 `09e80e4`；没有用更早的 .NET 8 包混算布局差异。旧包位于 `artifacts/pr33-source/artifacts/release/`，新包位于 `artifacts/release/`。

| 项目 | 旧包 | 新包 |
|---|---:|---:|
| 顶层文件 | 268 | 11 |
| 顶层文件夹 | 15 | 2 |
| 顶层 DLL | 256 | 5 |
| 包内总文件 | 442 | 29 |
| 解压后字节 | 157,731,438 | 142,492,566 |
| ZIP 字节 | 73,611,704 | 66,257,357 |

新 ZIP：`artifacts/release/Sims4ModSieve-0.1.0-alpha.1-win-x64.zip`。

SHA-256：`05e9d232ce2fdb0aeed83a5a5c31ef4563d3eef277f7ef725505853eb586baa5`。

### 旧包完整顶层清单

```text
Accessibility.dll
Assets/
BCnEncoder.dll
BUILD-INFO.txt
clretwrc.dll
clrgc.dll
clrgcexp.dll
clrjit.dll
CommunityToolkit.HighPerformance.dll
coreclr.dll
createdump.exe
cs/
D3DCompiler_47_cor3.dll
de/
DirectWriteForwarder.dll
es/
fr/
hostfxr.dll
hostpolicy.dll
ICSharpCode.SharpZipLib.dll
it/
ja/
ko/
LICENSE
licenses/
LlamaLogic.Packages.dll
Microsoft.CSharp.dll
Microsoft.DiaSymReader.Native.amd64.dll
Microsoft.VisualBasic.Core.dll
Microsoft.VisualBasic.dll
Microsoft.Win32.Primitives.dll
Microsoft.Win32.Registry.AccessControl.dll
Microsoft.Win32.Registry.dll
Microsoft.Win32.SystemEvents.dll
mscordaccore_amd64_amd64_10.0.1426.52326.dll
mscordaccore.dll
mscordbi.dll
mscorlib.dll
mscorrc.dll
msquic.dll
netstandard.dll
Nito.AsyncEx.Context.dll
Nito.AsyncEx.Coordination.dll
Nito.AsyncEx.Interop.WaitHandles.dll
Nito.AsyncEx.Oop.dll
Nito.AsyncEx.Tasks.dll
Nito.Cancellation.dll
Nito.Collections.Deque.dll
Nito.Disposables.dll
PenImc_cor3.dll
pl/
PresentationCore.dll
PresentationFramework-SystemCore.dll
PresentationFramework-SystemData.dll
PresentationFramework-SystemDrawing.dll
PresentationFramework-SystemXml.dll
PresentationFramework-SystemXmlLinq.dll
PresentationFramework.Aero.dll
PresentationFramework.Aero2.dll
PresentationFramework.AeroLite.dll
PresentationFramework.Classic.dll
PresentationFramework.dll
PresentationFramework.Fluent.dll
PresentationFramework.Luna.dll
PresentationFramework.Royale.dll
PresentationNative_cor3.dll
PresentationUI.dll
pt-BR/
ReachFramework.dll
README.md
ru/
SHA256SUMS.txt
Sims4ModSieve.Core.dll
Sims4ModSieve.Core.pdb
Sims4ModSieve.deps.json
Sims4ModSieve.dll
Sims4ModSieve.exe
Sims4ModSieve.Packages.dll
Sims4ModSieve.Packages.pdb
Sims4ModSieve.pdb
Sims4ModSieve.runtimeconfig.json
StbImageSharp.dll
StbImageWriteSharp.dll
System.AppContext.dll
System.Buffers.dll
System.CodeDom.dll
System.Collections.Concurrent.dll
System.Collections.dll
System.Collections.Immutable.dll
System.Collections.NonGeneric.dll
System.Collections.Specialized.dll
System.ComponentModel.Annotations.dll
System.ComponentModel.DataAnnotations.dll
System.ComponentModel.dll
System.ComponentModel.EventBasedAsync.dll
System.ComponentModel.Primitives.dll
System.ComponentModel.TypeConverter.dll
System.Configuration.ConfigurationManager.dll
System.Configuration.dll
System.Console.dll
System.Core.dll
System.Data.Common.dll
System.Data.DataSetExtensions.dll
System.Data.dll
System.Diagnostics.Contracts.dll
System.Diagnostics.Debug.dll
System.Diagnostics.DiagnosticSource.dll
System.Diagnostics.EventLog.dll
System.Diagnostics.EventLog.Messages.dll
System.Diagnostics.FileVersionInfo.dll
System.Diagnostics.PerformanceCounter.dll
System.Diagnostics.Process.dll
System.Diagnostics.StackTrace.dll
System.Diagnostics.TextWriterTraceListener.dll
System.Diagnostics.Tools.dll
System.Diagnostics.TraceSource.dll
System.Diagnostics.Tracing.dll
System.DirectoryServices.dll
System.dll
System.Drawing.Common.dll
System.Drawing.dll
System.Drawing.Primitives.dll
System.Dynamic.Runtime.dll
System.Formats.Asn1.dll
System.Formats.Nrbf.dll
System.Formats.Tar.dll
System.Globalization.Calendars.dll
System.Globalization.dll
System.Globalization.Extensions.dll
System.IO.Compression.Brotli.dll
System.IO.Compression.dll
System.IO.Compression.FileSystem.dll
System.IO.Compression.Native.dll
System.IO.Compression.ZipFile.dll
System.IO.dll
System.IO.FileSystem.AccessControl.dll
System.IO.FileSystem.dll
System.IO.FileSystem.DriveInfo.dll
System.IO.FileSystem.Primitives.dll
System.IO.FileSystem.Watcher.dll
System.IO.IsolatedStorage.dll
System.IO.MemoryMappedFiles.dll
System.IO.Packaging.dll
System.IO.Pipelines.dll
System.IO.Pipes.AccessControl.dll
System.IO.Pipes.dll
System.IO.UnmanagedMemoryStream.dll
System.Linq.AsyncEnumerable.dll
System.Linq.dll
System.Linq.Expressions.dll
System.Linq.Parallel.dll
System.Linq.Queryable.dll
System.Memory.dll
System.Net.dll
System.Net.Http.dll
System.Net.Http.Json.dll
System.Net.HttpListener.dll
System.Net.Mail.dll
System.Net.NameResolution.dll
System.Net.NetworkInformation.dll
System.Net.Ping.dll
System.Net.Primitives.dll
System.Net.Quic.dll
System.Net.Requests.dll
System.Net.Security.dll
System.Net.ServerSentEvents.dll
System.Net.ServicePoint.dll
System.Net.Sockets.dll
System.Net.WebClient.dll
System.Net.WebHeaderCollection.dll
System.Net.WebProxy.dll
System.Net.WebSockets.Client.dll
System.Net.WebSockets.dll
System.Numerics.dll
System.Numerics.Vectors.dll
System.ObjectModel.dll
System.Printing.dll
System.Private.CoreLib.dll
System.Private.DataContractSerialization.dll
System.Private.Uri.dll
System.Private.Windows.Core.dll
System.Private.Windows.GdiPlus.dll
System.Private.Xml.dll
System.Private.Xml.Linq.dll
System.Reflection.DispatchProxy.dll
System.Reflection.dll
System.Reflection.Emit.dll
System.Reflection.Emit.ILGeneration.dll
System.Reflection.Emit.Lightweight.dll
System.Reflection.Extensions.dll
System.Reflection.Metadata.dll
System.Reflection.Primitives.dll
System.Reflection.TypeExtensions.dll
System.Resources.Extensions.dll
System.Resources.Reader.dll
System.Resources.ResourceManager.dll
System.Resources.Writer.dll
System.Runtime.CompilerServices.Unsafe.dll
System.Runtime.CompilerServices.VisualC.dll
System.Runtime.dll
System.Runtime.Extensions.dll
System.Runtime.Handles.dll
System.Runtime.InteropServices.dll
System.Runtime.InteropServices.JavaScript.dll
System.Runtime.InteropServices.RuntimeInformation.dll
System.Runtime.Intrinsics.dll
System.Runtime.Loader.dll
System.Runtime.Numerics.dll
System.Runtime.Serialization.dll
System.Runtime.Serialization.Formatters.dll
System.Runtime.Serialization.Json.dll
System.Runtime.Serialization.Primitives.dll
System.Runtime.Serialization.Xml.dll
System.Security.AccessControl.dll
System.Security.Claims.dll
System.Security.Cryptography.Algorithms.dll
System.Security.Cryptography.Cng.dll
System.Security.Cryptography.Csp.dll
System.Security.Cryptography.dll
System.Security.Cryptography.Encoding.dll
System.Security.Cryptography.OpenSsl.dll
System.Security.Cryptography.Pkcs.dll
System.Security.Cryptography.Primitives.dll
System.Security.Cryptography.ProtectedData.dll
System.Security.Cryptography.X509Certificates.dll
System.Security.Cryptography.Xml.dll
System.Security.dll
System.Security.Permissions.dll
System.Security.Principal.dll
System.Security.Principal.Windows.dll
System.Security.SecureString.dll
System.ServiceModel.Web.dll
System.ServiceProcess.dll
System.Text.Encoding.CodePages.dll
System.Text.Encoding.dll
System.Text.Encoding.Extensions.dll
System.Text.Encodings.Web.dll
System.Text.Json.dll
System.Text.RegularExpressions.dll
System.Threading.AccessControl.dll
System.Threading.Channels.dll
System.Threading.dll
System.Threading.Overlapped.dll
System.Threading.Tasks.Dataflow.dll
System.Threading.Tasks.dll
System.Threading.Tasks.Extensions.dll
System.Threading.Tasks.Parallel.dll
System.Threading.Thread.dll
System.Threading.ThreadPool.dll
System.Threading.Timer.dll
System.Transactions.dll
System.Transactions.Local.dll
System.ValueTuple.dll
System.Web.dll
System.Web.HttpUtility.dll
System.Windows.Controls.Ribbon.dll
System.Windows.dll
System.Windows.Extensions.dll
System.Windows.Input.Manipulations.dll
System.Windows.Presentation.dll
System.Windows.Primitives.dll
System.Xaml.dll
System.Xml.dll
System.Xml.Linq.dll
System.Xml.ReaderWriter.dll
System.Xml.Serialization.dll
System.Xml.XDocument.dll
System.Xml.XmlDocument.dll
System.Xml.XmlSerializer.dll
System.Xml.XPath.dll
System.Xml.XPath.XDocument.dll
THIRD-PARTY-NOTICES.md
tr/
UIAutomationClient.dll
UIAutomationClientSideProviders.dll
UIAutomationProvider.dll
UIAutomationTypes.dll
vcruntime140_cor3.dll
WindowsBase.dll
wpfgfx_cor3.dll
YamlDotNet.dll
zh-Hans/
zh-Hant/
```

### 新包完整顶层清单

```text
Assets/
BUILD-INFO.txt
D3DCompiler_47_cor3.dll
LICENSE
licenses/
PenImc_cor3.dll
PresentationNative_cor3.dll
README.md
SHA256SUMS.txt
Sims4ModSieve.exe
THIRD-PARTY-NOTICES.md
vcruntime140_cor3.dll
wpfgfx_cor3.dll
```

5 个 DLL 的用途及必须外置的原因见[发布流程 · 文件布局](便携版发布流程.md#文件布局)。这些都是 SDK 发布出的 WPF 原生依赖；本配置禁止原生库自解压，因此由 Windows 从 EXE 旁加载。没有手工删除运行时 DLL。固定运行时的 `RuntimeList.xml` 将 `coreclr.dll`、`clrjit.dll`、`hostpolicy.dll` 标为 `DropFromSingleFile`，对应代码由 Windows 单文件启动器静态包含。

## 已验证

- 自有资源：`src/` 中没有独立的 `.resx`、`.resw` 或卫星程序集文件，也没有发现额外翻译目录。界面文字直接写在 XAML/C#；`zh-Hans;en` 过滤作用于发布的依赖卫星资源。字体本体仍为 WPF Resource，字体许可仍在 Assets 中。
- 路径：没有发现业务代码用 `Assembly.Location`、`CodeBase` 或模块文件路径读取随包资源；许可证使用 `AppContext.BaseDirectory`，设置使用 LocalApplicationData。CLI 的当前目录仅用于默认报告输出，不参与本次 Desktop 发布。
- 配置隔离：读取普通项目的 MSBuild 属性时，单文件、语言过滤、解压和裁剪属性均未启用；显式选择 PortableWinX64 时，单文件／自包含为 true，语言为 `zh-Hans;en`，两项解压、裁剪、压缩均为 false。普通及发布的锁定还原分别成功。
- 包内容：实际 EXE 含 251 个条目，关键业务、第三方和框架托管程序集、deps 与 runtimeconfig 在 EXE 内；两个框架版本均为 10.0.14。未发现原生、需解压或压缩条目，也没有多余顶层语言目录、托管 DLL、JSON 或 PDB。
- 校验：逐个核对 29 个 ZIP 文件与发布目录、SHA256SUMS 清单以及 ZIP 外部哈希，全部一致。所有许可证保留。调试 PDB 在三个项目的构建目录中。
- 漏洞门禁：锁定发布还原后查询 NuGet 直接和传递依赖，无已知漏洞报告。
- 实际启动：将新 ZIP 解压到全新中文及空格路径，从不同工作目录启动实际 EXE，DOTNET_ROOT 指向不存在的目录。正常显示主窗口、关闭并再次启动。
- 界面操作：通过 Windows 文件夹选择框设置临时 Mods；重复扫描找到预期的 1 组副本；冲突扫描读取合成 DBPF 和 ts4script，显示脚本碰撞及 package 整理卡片；更改默认 Mods 后保存，退出并重启读取同一设置；关于页正常显示 LICENSE 与 THIRD-PARTY-NOTICES。截图已经查看。
- 无 bundle 解压：为实际进程指定全新的 DOTNET_BUNDLE_EXTRACT_BASE_DIR，完成上述操作后该目录未创建；进程模块确认 WPF 原生库来自新解压目录，未加载外置 CoreCLR/JIT/hostfxr。
- 设置恢复：验收前备份原 duplicate-settings.json，结束后逐字节恢复，另行核对 SHA-256 一致。所有扫描／文件操作测试仅使用临时样本，没有扫描或更改真实 Mods。

## 测试

相关测试先跑 `FullyQualifiedName~Settings`：28 项通过。最后完整构建一次、整套测试一次：构建 0 警告 0 错误；Core 263 通过／1 跳过，Desktop 309 通过，Packages 53 通过／3 跳过，合计 625 通过／4 跳过／0 失败。此次显式启用临时文件回收站往返测试。

没有新增 MSTest 测试方法。新增的 `eng/test-portable-bundle.ps1` 场景全部通过：

| 场景名 | 验证行为 |
|---|---|
| ValidBundle | 正确的新包通过检查 |
| LooseManagedDllRejected | 拒绝外置托管程序集 |
| LoosejsonRejected | 拒绝外置 JSON |
| LoosepdbRejected | 拒绝用户包中的 PDB |
| UnwantedLanguageDirectoryRejected | 拒绝多余顶层语言目录 |
| ExtractionModeRejected | 拒绝启用内容提取兼容模式 |
| CompressedPayloadRejected | 拒绝压缩条目 |
| NativePayloadRejected | 拒绝需提取的内嵌原生条目 |
| WrongRuntimeRejected | 拒绝错误运行时版本 |
| NonBundleRejected | 拒绝不含 bundle 的伪造 EXE |
| RestoredBundle | 恢复测试副本后再次通过检查 |

本地 UI 自动验收另有 DuplicateScan、ConflictScan、ExternalResources、SettingsRoundTrip、NoBundleExtraction 五个场景，验证内容对应上面的实际进程操作。

原始日志、TRX、清单和截图保存在仓库忽略目录 `artifacts/single-file-audit/`：`package.log`、`bundle-gate-tests.log`、`targeted-tests.log`、`final-build.log`、`final-tests.log`、`ui-smoke.log`，以及 `ui-evidence/` 下五张成功场景截图；这些本机产物不随用户包分发。

## 未验证

- 修改后的 GitHub Actions 尚未在远端执行；打包、检查脚本及符号路径已在本机验证。
- 未在没有 SDK／NuGet 缓存的干净 Windows 10、Windows 11 机器上验证。当前证据来自开发机，不替代干净系统验收。
- 未提供真实 Mods 语料，4 项真实语料测试按原条件跳过。
- 未单独比较冷启动耗时、峰值内存或安装杀毒软件下的表现。本次未开启单文件压缩，目标是精简顶层文件。
