# 整文件哈希缓存存储

独立的 .NET 10 存储项目；桌面程序、CLI 和扫描器均未引用它。只存整文件 SHA-256，不负责读取源文件、判断文件是否稳定或授权删除。

```csharp
var cache = new SqliteSha256Cache(databasePath, applicationVersion, "SHA-256/v1");
using var scan = cache.BeginScan();
string? hit = scan.GetSha256(filePath, currentStamp, cancellationToken);
scan.Stage(filePath, successfulHash.Stamp, successfulHash.Sha256);
// 整次扫描成功后才调用；异常或取消时直接 Dispose。
bool published = scan.Commit(cancellationToken);
```

`databasePath` 必须显式指定到本地工具缓存目录，不能放在 Mods 或网络来源里；测试只用临时目录。源文件路径仅作为键，不由本项目打开或写入。调用者负责采集真实文件戳，只暂存成功、稳定的哈希；命中后的文件戳复核由后续扫描接入负责。

- 文件版本由完整路径、大小、UTC 修改时间 ticks、身份是否存在及卷序列号 / FileIdLow / FileIdHigh 组成。身份为 null 与各字段为零的真实身份不同，UInt64 用定长十六进制保存。
- 路径使用完整路径并去尾分隔符，最长 32768 个 UTF-16 字符。无文件身份时按 `Ordinal` 精确比较路径，有身份时按 `OrdinalIgnoreCase` 比较。SQLite 排序规则和命中后的路径核对都使用对应的 .NET 比较规则。硬链接仍按路径分别存储，缓存不跟随重解析点。
- 仅接受 64 位十六进制 SHA-256，数据库保存 32 字节摘要及覆盖文件版本与摘要的记录校验值。读取前限制 BLOB 长度，异常记录视为故障。
- 程序版本、算法规则版本、存储结构版本或路径规则版本变化时，在事务内换代并清空旧记录；降回旧版本也不会重新启用历史记录。旧存储对象不能查询或提交新一代的数据。
- 每个 `Scan` 复用一条关闭连接池的独立连接，查询只读已提交记录，写入先存入 SQLite TEMP 表。同一个 `Scan` 的并发调用串行使用连接，无需跨源文件 IO 持有写事务。TEMP 可由 SQLite 写到临时文件。扫描外的单次查询可用 `cache.GetSha256`。
- `Commit` 在一个短事务内整体发布，结束后关闭连接；重复提交返回 false。`Dispose`、提交前取消或写入失败不改变已提交数据。进程被杀后临时表消失，主数据库依靠 WAL 恢复，暂存数据不会被读取。
- 提交前最后检查取消；取消与已完成的原子提交同时发生时，允许保留整批有效数据。取消抛出 `OperationCanceledException`，不伪装成存储故障。
- 打开时执行 `quick_check`；数据库损坏、记录校验失败、目录不可写或 SQLite 操作失败后，本存储对象停用，读取返回 null、暂存和提交返回 false，`IsAvailable` 为 false。锁等待上限为 1 秒；不捕获内存不足异常。

没有自动隔离 / 重建坏库、容量淘汰、维护锁、范围清理、索引或资源缓存。坏库保留原样，由未来的维护层处理；现阶段读取故障后也没有扫描重算，因为产品尚未接入。完整规划见[性能与缓存架构第 6 节](../../docs/architecture/性能优化与缓存架构.md#6-缓存设计)，此次暂存选择见[决策 0010](../../docs/decisions/0010-缓存存储先用连接临时表暂存.md)。

依赖固定为 `Microsoft.Data.Sqlite 10.0.15`，实际解析的 SQLitePCLRaw 为 2.1.13、原生 SQLite 为 3.53.3。版本与许可证见[第三方说明](../../THIRD-PARTY-NOTICES.md)。
