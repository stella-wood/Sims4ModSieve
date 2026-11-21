# 整文件哈希缓存存储

桌面程序和 CLI 的扫描使用本项目缓存整文件 SHA-256，数据库位于 `%LOCALAPPDATA%\Sims4ModSieve\cache\sha256.sqlite3`。`CachingStableFileHasher` 在缓存命中前后核对文件戳，未命中时用 `StableFileHasher` 实时读取文件并暂存成功结果和返回的文件戳。CLI 的 `--no-cache` 跳过缓存读写。

```csharp
using var cache = CachingStableFileHasher.Open(dataDirectory);
var report = await cache.RunAsync(hasher => ScanAsync(hasher, cancellationToken), cancellationToken);
if (report.Issues.Count == 0) cache.Commit(cancellationToken);
```

`dataDirectory` 是工具的本地数据目录，测试传临时目录。扫描完成且没有读取或分析问题才提交。桌面窗口关闭时取消进行中的扫描。存储层 `SqliteSha256Cache` 只用文件路径作为键。

- 文件版本由完整路径、大小、UTC 修改时间 ticks、身份是否存在及卷序列号 / FileIdLow / FileIdHigh 组成。身份为 null 与各字段为零的真实身份不同，UInt64 用定长十六进制保存。
- 路径使用完整路径并去尾分隔符，最长 32768 个 UTF-16 字符。无文件身份时按 `Ordinal` 精确比较路径，有身份时按 `OrdinalIgnoreCase` 比较。SQLite 排序规则和命中后的路径核对都使用对应的 .NET 比较规则。硬链接仍按路径分别存储，缓存不跟随重解析点。
- 仅接受 64 位十六进制 SHA-256，数据库保存 32 字节摘要及覆盖文件版本与摘要的记录校验值。读取前限制 BLOB 长度，异常记录视为故障。
- 缓存失效条件见[版本和键](../../docs/architecture/性能优化与缓存架构.md#62-版本和键)。
- 每个 `Scan` 复用一条关闭连接池的独立连接，查询只读已提交记录，写入先存入 SQLite TEMP 表。同一个 `Scan` 的并发调用串行使用连接，无需跨源文件 IO 持有写事务。TEMP 可由 SQLite 写到临时文件。扫描外的单次查询可用 `cache.GetSha256`。
- `Commit` 在一个短事务内整体发布，同时移除已暂存路径的旧版本。身份模式切换时只清理拼写完全相同的路径。`Dispose`、提交前取消或写入失败不改变已提交数据。进程被杀后临时表消失，主数据库依靠 WAL 恢复。
- 提交前最后检查取消；取消与已完成的原子提交同时发生时，允许保留整批有效数据。取消抛出 `OperationCanceledException`，不伪装成存储故障。
- 打开时执行 `quick_check`；数据库损坏、记录校验失败、目录不可写或 SQLite 操作失败后，本存储对象停用，读取返回 null、暂存和提交返回 false，`IsAvailable` 为 false。锁等待上限为 1 秒；不捕获内存不足异常。

缓存打开失败或发现损坏时静默回退到实时读取。扫描期间停用缓存时，已消费命中的读取步骤在发布结果前绕过缓存重跑一次。坏库保留原样。后续扩展见[性能与缓存架构第 6 节](../../docs/architecture/性能优化与缓存架构.md#6-缓存设计)，暂存选择见[决策 0010](../../docs/decisions/0010-缓存存储先用连接临时表暂存.md)。

依赖固定为 `Microsoft.Data.Sqlite 10.0.15`，实际解析的 SQLitePCLRaw 为 2.1.13、原生 SQLite 为 3.53.3。版本与许可证见[第三方说明](../../THIRD-PARTY-NOTICES.md)。
