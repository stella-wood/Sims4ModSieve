# 脚本 ZIP 边界回归

`ScriptArchiveRegressionTests` 在测试中生成 ZIP，再修改指定字节；不分发或执行真实 Mod。字段布局依据 [PKWARE APPNOTE 6.3.10](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) 的 4.3.6～4.3.9、4.3.12、4.3.16。数据描述符有无签名均允许，见 4.3.9.3；内容中的头部标记本身不构成另一条记录。

从仓库根目录运行：

```powershell
.\eng\dotnet.cmd test .\tests\Sims4ModSieve.Core.Tests -c Release --no-restore --nologo -m:1 --filter FullyQualifiedName~ScriptArchiveRegressionTests
```

## 损坏样本

| 测试 | 构造方式 | 预期 Code / Stage |
|---|---|---|
| `EntryDataCannotCrossTheDirectoryOrNextLocalRecord` | 同步修改本地与中央目录的长度，使数据覆盖中央目录、越过下一条记录进入其数据区，或超过文件末尾 | `EntryCorrupt` / `Entry`，读取数据前拒绝 |
| `ALocalRecordEmbeddedInAnotherEntryCannotShareItsData` | 将第二条目录记录指向第一条数据内部的完整本地记录，使两条记录声明的数据区重叠 | `EntryCorrupt` / `Entry`，拒绝覆盖另一条记录的 `m.py`；`z.py` 继续比较 |
| `LocalHeaderOffsetsOutsideTheDataAreaAreEntryCorrupt` | 本地头偏移分别指向中央目录、文件末尾之后、`uint.MaxValue - 1` | `EntryCorrupt` / `Entry`，不按巨大偏移分配内存 |
| `LocalAndCentralHeaderDisagreementsAreEntryCorrupt` | 分别修改本地头偏移 0、6、8、10、14、18、22、26、28、30 的字节，对应签名、标志、方法、时间、CRC、压缩长度、原长度、文件名长度、扩展长度、文件名 | `EntryCorrupt` / `Entry` |
| `InvalidCentralDirectoryBoundsAreCorrupt` | 中央目录偏移超过文件末尾，或偏移／长度设为 `uint.MaxValue - 1` | `Corrupt` / `Structure` |
| `TruncatedZipRecordsReturnStructuredIssues`：`end` | 从归档末尾截掉一个字节 | `NotZip` / `Structure` |
| 同上：`central-header`、`central-name` | 分别截断中央目录固定头、文件名，保留尾记录并同步目录长度 | `Corrupt` / `Structure` |
| 同上：`local-header`、`local-name`、`data` | 分别截断本地固定头、文件名、数据，保留中央目录与尾记录并同步目录位置 | `EntryCorrupt` / `Entry` |
| `InvalidOrTruncatedDataDescriptorsAreEntryCorrupt` | 分别改坏描述符的签名、CRC、压缩长度、原长度，或截断最后一个字节并保留中央目录 | `EntryCorrupt` / `Entry`，不发布已读内容的哈希 |
| `InvalidCrcOrDeclaredLengthProducesAnUncomparedOccurrence` | 本地头和中央目录一致地声明错误 CRC 或原长度 | `EntryCorrupt` / `Entry`，碰撞为未完成 |

新增损坏样本都与一个正常归档同扫，检查具体文件、Code、Stage、处理字节数与流释放。条目错误保留正常模块；结构错误只将坏归档加入 `IncompleteArchivePaths`，正常归档继续分析。测试限制单归档 1024 字节、8 条目录记录、单模块 128 字节、总输出 256 字节、并发 1；10 秒取消令牌和 128 次读取上限用于发现失去进展的回归，不作为性能基准。

## 兼容性样本

| 测试 | 保留的兼容行为 |
|---|---|
| `CentralDirectoryOrderCanDifferFromLocalRecordOrder` | 两条中央目录记录逆序，本地记录和偏移不变；两个模块仍判定相同 |
| `LocalHeaderBytesInsideModuleDataAreNotARecordBoundary` | 模块内容包含完整的本地 ZIP 记录，但中央目录没有引用它；按普通内容比较 |
| `AValidZipWrittenWithoutSeekingSupportsDataDescriptors` | 流式 ZIP 的数据描述符有签名、无签名两种情况均可比较 |
| `DuplicateArchiveEntryUsesTheLastDirectoryRecord` | 同名路径的最后一条目录记录生效，不因存在重复名字而整体拒绝 |
| `Utf8AndCp437EntryNamesResolveToTheSameUnicodeModule` | 两种编码的同一模块名正常比较 |

`RawDeflateWithoutAFinalBlockIsCorruptEvenWhenOutputAndCrcMatch` 等既有测试继续覆盖压缩流截断、输出预算边界、损坏条目不影响正常模块，以及取消和流释放。
