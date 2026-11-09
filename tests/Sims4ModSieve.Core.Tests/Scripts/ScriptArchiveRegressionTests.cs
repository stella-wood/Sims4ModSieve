using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Core.Tests.Scripts;

[TestClass]
public sealed class ScriptArchiveRegressionTests
{
    private static byte[] Zip(params (string Name, byte[] Data)[] entries) =>
        Zip(CompressionLevel.NoCompression, entries);

    private static byte[] Zip(CompressionLevel compression, params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name, compression).Open();
                stream.Write(data);
            }
        return memory.ToArray();
    }

    private static int Find(byte[] bytes, uint signature, int from = 0)
    {
        for (int i = from; i <= bytes.Length - 4; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == signature) return i;
        throw new InvalidOperationException("signature missing");
    }

    private static Task<ScriptModuleScanReport> Scan(string directory, ScriptModuleLimits? limits = null,
        IFileSystemAccess? fs = null, CancellationToken cancellationToken = default) =>
        new ScriptModuleScanner(fs ?? new PhysicalFileSystemAccess()).ScanAsync(
            new([new ScanSource("mods", directory)], limits), cancellationToken: cancellationToken);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task EntryDataCannotCrossTheDirectoryOrNextLocalRecord(bool overlapsNextEntry, bool pastEnd)
    {
        var bytes = overlapsNextEntry
            ? Zip(("m.py", "x=1"u8.ToArray()), ("z.py", "y=1"u8.ToArray()))
            : Zip(("m.py", "x=1"u8.ToArray()));
        var central = Find(bytes, 0x02014b50);
        var boundary = overlapsNextEntry ? Find(bytes, 0x04034b50, 4) : central;
        var dataStart = 30 + "m.py".Length;
        var length = (uint)(pastEnd ? bytes.Length - dataStart + 1
            : boundary - dataStart + (overlapsNextEntry ? 30 + "z.py".Length + 1 : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 20), length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), length);

        var report = await AssertBrokenZipAsync(bytes, ScriptArchiveIssueCode.EntryCorrupt,
            ScriptArchiveIssueStage.Entry, overlapsNextEntry ? 9 : 6);

        if (overlapsNextEntry)
            Assert.AreEqual(ScriptModuleVerdict.Identical, report.Collisions.Single(item => item.ModuleName == "z").Verdict);
    }

    [TestMethod]
    public async Task ALocalRecordEmbeddedInAnotherEntryCannotShareItsData()
    {
        var inner = Zip(("z.py", "y=1"u8.ToArray()));
        var innerCentral = Find(inner, 0x02014b50);
        var bytes = Zip(("m.py", inner[..innerCentral]), ("z.py", "y=1"u8.ToArray()));
        var central = Find(bytes, 0x02014b50);
        var secondCentral = Find(bytes, 0x02014b50, central + 4);
        // Both directory entries now claim the bytes of the embedded z.py record.
        inner.AsSpan(innerCentral, 46 + "z.py".Length).CopyTo(bytes.AsSpan(secondCentral));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(secondCentral + 42), (uint)(30 + "m.py".Length));

        var report = await AssertBrokenZipAsync(bytes, ScriptArchiveIssueCode.EntryCorrupt,
            ScriptArchiveIssueStage.Entry, 9);

        Assert.AreEqual(ScriptModuleVerdict.Identical, report.Collisions.Single(item => item.ModuleName == "z").Verdict);
    }

    [TestMethod]
    [DataRow("directory")]
    [DataRow("past-end")]
    [DataRow("huge")]
    public async Task LocalHeaderOffsetsOutsideTheDataAreaAreEntryCorrupt(string location)
    {
        var bytes = Zip(("m.py", "x=1"u8.ToArray()));
        var central = Find(bytes, 0x02014b50);
        var offset = location switch
        {
            "directory" => (uint)central,
            "past-end" => (uint)bytes.Length + 1,
            _ => uint.MaxValue - 1,
        };
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 42), offset);

        await AssertBrokenZipAsync(bytes, ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    [DataRow(8)]
    [DataRow(10)]
    [DataRow(14)]
    [DataRow(18)]
    [DataRow(22)]
    [DataRow(26)]
    [DataRow(28)]
    [DataRow(30)]
    public async Task LocalAndCentralHeaderDisagreementsAreEntryCorrupt(int localFieldOffset)
    {
        var bytes = Zip(("m.py", "x=1"u8.ToArray()));
        bytes[localFieldOffset] ^= 1;

        await AssertBrokenZipAsync(bytes, ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry);
    }

    [TestMethod]
    [DataRow("past-end")]
    [DataRow("huge-offset")]
    [DataRow("huge-size")]
    public async Task InvalidCentralDirectoryBoundsAreCorrupt(string field)
    {
        var bytes = Zip(("m.py", "x=1"u8.ToArray()));
        var end = Find(bytes, 0x06054b50);
        if (field == "huge-size")
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), uint.MaxValue - 1);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16),
                field == "past-end" ? (uint)bytes.Length + 1 : uint.MaxValue - 1);

        await AssertBrokenZipAsync(bytes, ScriptArchiveIssueCode.Corrupt, ScriptArchiveIssueStage.Structure);
    }

    [TestMethod]
    [DataRow("end", ScriptArchiveIssueCode.NotZip, ScriptArchiveIssueStage.Structure)]
    [DataRow("central-header", ScriptArchiveIssueCode.Corrupt, ScriptArchiveIssueStage.Structure)]
    [DataRow("central-name", ScriptArchiveIssueCode.Corrupt, ScriptArchiveIssueStage.Structure)]
    [DataRow("local-header", ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry)]
    [DataRow("local-name", ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry)]
    [DataRow("data", ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry)]
    public async Task TruncatedZipRecordsReturnStructuredIssues(string record, string code, ScriptArchiveIssueStage stage)
    {
        var bytes = Zip(("m.py", "x=1"u8.ToArray()));
        var central = Find(bytes, 0x02014b50);
        var end = Find(bytes, 0x06054b50);
        var cut = record switch
        {
            "central-header" => central + 45,
            "central-name" => central + 46 + "m.py".Length - 1,
            "local-header" => 29,
            "local-name" => 30 + "m.py".Length - 1,
            "data" => central - 1,
            _ => bytes.Length - 1,
        };
        if (record == "end") bytes = bytes[..cut];
        else if (record.StartsWith("central", StringComparison.Ordinal))
        {
            bytes = [.. bytes[..cut], .. bytes[end..]];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cut + 12), (uint)(cut - central));
        }
        else
        {
            bytes = [.. bytes[..cut], .. bytes[central..]];
            var shiftedEnd = cut + end - central;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(shiftedEnd + 16), (uint)cut);
        }

        await AssertBrokenZipAsync(bytes, code, stage);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    [DataRow(8)]
    [DataRow(12)]
    [DataRow(-1)]
    public async Task InvalidOrTruncatedDataDescriptorsAreEntryCorrupt(int descriptorFieldOffset)
    {
        var bytes = ZipWithDescriptor();
        var descriptor = Find(bytes, 0x08074b50);
        if (descriptorFieldOffset < 0)
        {
            bytes = [.. bytes[..(descriptor + 15)], .. bytes[(descriptor + 16)..]];
            var end = Find(bytes, 0x06054b50);
            var central = Find(bytes, 0x02014b50);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), (uint)central);
        }
        else bytes[descriptor + descriptorFieldOffset] ^= 1;

        await AssertBrokenZipAsync(bytes, ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry, 9);
    }

    [TestMethod]
    public async Task CentralDirectoryOrderCanDifferFromLocalRecordOrder()
    {
        using var temp = new TempDirectory();
        var bytes = Zip(("m.py", "x=1"u8.ToArray()), ("z.py", "y=1"u8.ToArray()));
        var central = Find(bytes, 0x02014b50);
        var secondCentral = Find(bytes, 0x02014b50, central + 4);
        var end = Find(bytes, 0x06054b50);
        bytes = [.. bytes[..central], .. bytes[secondCentral..end], .. bytes[central..secondCentral], .. bytes[end..]];
        temp.WriteBytes("a.ts4script", bytes);
        temp.WriteBytes("b.ts4script", Zip(("m.py", "x=1"u8.ToArray()), ("z.py", "y=1"u8.ToArray())));

        var report = await Scan(temp.Path);

        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(2, report.Collisions.Count);
        Assert.IsTrue(report.Collisions.All(item => item.Verdict == ScriptModuleVerdict.Identical));
        Assert.AreEqual(12L, report.ProcessedBytes);
    }

    [TestMethod]
    public async Task LocalHeaderBytesInsideModuleDataAreNotARecordBoundary()
    {
        using var temp = new TempDirectory();
        var inner = Zip(("z.py", "y=1"u8.ToArray()));
        var data = inner[..Find(inner, 0x02014b50)];
        var bytes = Zip(("m.py", data));
        temp.WriteBytes("a.ts4script", bytes);
        temp.WriteBytes("b.ts4script", bytes);

        var report = await Scan(temp.Path);

        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(ScriptModuleVerdict.Identical, report.Collisions.Single().Verdict);
        Assert.AreEqual(2L * data.Length, report.ProcessedBytes);
    }

    private static async Task<ScriptModuleScanReport> AssertBrokenZipAsync(
        byte[] bytes, string code, ScriptArchiveIssueStage stage, long expectedProcessedBytes = 6)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("a.ts4script", bytes);
        temp.WriteBytes("b.ts4script", Zip(("m.py", "x=1"u8.ToArray()), ("z.py", "y=1"u8.ToArray())));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reads = 0;
        var fs = new TrackingFs(() =>
        {
            if (Interlocked.Increment(ref reads) > 128) throw new IOException("ZIP fixture exceeded its read limit.");
        });

        var report = await Scan(temp.Path, new ScriptModuleLimits(MaxArchiveBytes: 1024, MaxEntriesPerArchive: 8,
            MaxModuleBytes: 128, MaxTotalProcessedBytes: 256, MaxDegreeOfParallelism: 1), fs, cancellation.Token);

        var issue = report.Issues.Single();
        Assert.AreEqual(code, issue.Code, issue.Detail);
        Assert.AreEqual(stage, issue.Stage);
        Assert.AreEqual(path, issue.Path);
        Assert.AreEqual(expectedProcessedBytes, report.ProcessedBytes);
        Assert.AreEqual(2, fs.OpenedCount);
        Assert.AreEqual(fs.OpenedCount, fs.DisposedCount);
        if (stage == ScriptArchiveIssueStage.Entry)
        {
            Assert.AreEqual("m.py", issue.EntryName);
            Assert.AreEqual(2, report.AnalyzedArchiveCount);
            var collision = report.Collisions.Single(item => item.ModuleName == "m");
            Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
            Assert.IsNull(collision.Uncompared.Single().Sha256);
            Assert.AreEqual(1, collision.ContentGroups.Single().Occurrences.Count);
        }
        else
        {
            Assert.AreEqual(1, report.AnalyzedArchiveCount);
            CollectionAssert.AreEqual(new[] { path }, report.IncompleteArchivePaths.ToArray());
            Assert.AreEqual(0, report.Collisions.Count);
        }
        return report;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidCrcOrDeclaredLengthProducesAnUncomparedOccurrence(bool wrongLength)
    {
        using var temp = new TempDirectory();
        var bytes = Zip(("m.py", "x=1"u8.ToArray()));
        var central = Find(bytes, 0x02014b50);
        var local = Find(bytes, 0x04034b50);
        if (wrongLength)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22), 4);
        }
        else
        {
            bytes[central + 16] ^= 1;
            bytes[local + 14] ^= 1;
        }
        temp.WriteBytes("a.ts4script", bytes);
        temp.WriteBytes("b.ts4script", Zip(("m.py", "x=1"u8.ToArray())));
        var report = await Scan(temp.Path);
        Assert.AreEqual(ScriptArchiveIssueCode.EntryCorrupt, report.Issues.Single().Code);
        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
        Assert.IsNull(collision.Uncompared.Single().Sha256);
        Assert.AreEqual(1, collision.ContentGroups.Single().Occurrences.Count);
    }

    [TestMethod]
    public async Task DuplicateArchiveEntryUsesTheLastDirectoryRecord()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("a.ts4script", Zip(("m.py", "x=1"u8.ToArray()), ("m.py", "x=2"u8.ToArray())));
        temp.WriteBytes("b.ts4script", Zip(("m.py", "x=2"u8.ToArray())));
        var report = await Scan(temp.Path);
        Assert.AreEqual(ScriptModuleVerdict.Identical, report.Collisions.Single().Verdict);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task Utf8AndCp437EntryNamesResolveToTheSameUnicodeModule()
    {
        using var temp = new TempDirectory();
        var utf8 = Zip(("xx.py", "x=1"u8.ToArray()));
        var cp437 = Zip(("x.py", "x=1"u8.ToArray()));
        ReplaceName(utf8, [0xC3, 0xA9, (byte)'.', (byte)'p', (byte)'y'], utf8Flag: true);
        ReplaceName(cp437, [0x82, (byte)'.', (byte)'p', (byte)'y'], utf8Flag: false);
        temp.WriteBytes("a.ts4script", utf8);
        temp.WriteBytes("b.ts4script", cp437);

        var report = await Scan(temp.Path);

        Assert.AreEqual(0, report.Issues.Count);
        var collision = report.Collisions.Single();
        Assert.AreEqual("é", collision.ModuleName);
        Assert.AreEqual(ScriptModuleVerdict.Identical, collision.Verdict);
        Assert.IsTrue(collision.Occurrences.All(occurrence => occurrence.EntryName == "é.py"));
    }

    private static void ReplaceName(byte[] zip, byte[] name, bool utf8Flag)
    {
        var local = Find(zip, 0x04034b50);
        var central = Find(zip, 0x02014b50);
        name.CopyTo(zip, local + 30);
        name.CopyTo(zip, central + 46);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(local + 6), (ushort)(utf8Flag ? 0x800 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(central + 8), (ushort)(utf8Flag ? 0x800 : 0));
    }

    [TestMethod]
    public async Task InvalidBytecodeFallsBackToDifferentSourceEntries()
    {
        using var temp = new TempDirectory();
        var invalidPyc = "invalid bytecode with identical bytes"u8.ToArray();
        temp.WriteBytes("a.ts4script", Zip(("m.pyc", invalidPyc), ("m.py", "x=1"u8.ToArray())));
        temp.WriteBytes("b.ts4script", Zip(("m.pyc", invalidPyc), ("m.py", "x=2"u8.ToArray())));
        var report = await Scan(temp.Path);
        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Different, collision.Verdict);
        Assert.IsTrue(collision.Occurrences.All(occurrence => occurrence.EntryName == "m.py"));
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task UnsupportedBytecodeMagicCannotShareTheSupportedBytecodeHash()
    {
        using var temp = new TempDirectory();
        var first = new byte[20];
        first[0] = 0x42; first[1] = 0x0d; first[2] = 0x0d; first[3] = 0x0a;
        var second = (byte[])first.Clone(); second[0] = 0x61;
        temp.WriteBytes("a.ts4script", Zip(("m.pyc", first)));
        temp.WriteBytes("b.ts4script", Zip(("m.pyc", second)));
        var report = await Scan(temp.Path);
        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
        Assert.AreEqual(1, collision.Uncompared.Count);
        Assert.IsNull(collision.Uncompared.Single().Sha256);
    }

    [TestMethod]
    public async Task ACompletedModuleAtTheExactSharedOutputBudgetSucceeds()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("a.ts4script", Zip(("m.py", "x=1"u8.ToArray())));
        var report = await Scan(temp.Path, new ScriptModuleLimits(MaxTotalProcessedBytes: 3));
        Assert.AreEqual(0, report.Issues.Count, string.Join(",", report.Issues.Select(i => i.Code)));
        Assert.AreEqual(3L, report.ProcessedBytes);
        Assert.AreEqual(1, report.ModuleCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task UnderstatedEndRecordCountCannotBypassTheActualEntryLimit(int declaredCount)
    {
        using var temp = new TempDirectory();
        var bytes = Zip(("a.py", [1]), ("b.py", [2]), ("c.py", [3]));
        var eocd = Find(bytes, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 8), (ushort)declaredCount);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 10), (ushort)declaredCount);
        temp.WriteBytes("forged.ts4script", bytes);
        var report = await Scan(temp.Path, new ScriptModuleLimits(MaxEntriesPerArchive: 2));
        Assert.AreEqual(0, report.AnalyzedArchiveCount);
        Assert.AreEqual(1, report.IncompleteArchiveCount);
        Assert.AreEqual(0, report.ModuleCount);
        Assert.AreEqual(0L, report.ProcessedBytes);
        var issue = report.Issues.Single();
        Assert.AreEqual(ScriptArchiveIssueStage.Structure, issue.Stage);
        Assert.IsTrue(issue.Code is ScriptArchiveIssueCode.EntryCountExceedsLimit or ScriptArchiveIssueCode.Corrupt);
    }

    [TestMethod]
    public async Task MismatchedEndRecordCountBelowTheLimitIsCorrupt()
    {
        using var temp = new TempDirectory();
        var bytes = Zip(("a.py", [1]), ("b.py", [2]));
        var eocd = Find(bytes, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 10), 1);
        temp.WriteBytes("forged.ts4script", bytes);

        var report = await Scan(temp.Path, new ScriptModuleLimits(MaxEntriesPerArchive: 3));

        Assert.AreEqual(ScriptArchiveIssueCode.Corrupt, report.Issues.Single().Code);
        Assert.AreEqual(0, report.AnalyzedArchiveCount);
        Assert.AreEqual(0L, report.ProcessedBytes);
    }

    [TestMethod]
    public async Task ACorruptEntryDoesNotInvalidateAHealthySiblingModule()
    {
        using var temp = new TempDirectory();
        var bytes = Zip(("broken.py", "bad"u8.ToArray()), ("healthy.py", "x=1"u8.ToArray()));
        var central = Find(bytes, 0x02014b50);
        var local = Find(bytes, 0x04034b50);
        bytes[central + 16] ^= 1;
        bytes[local + 14] ^= 1;
        temp.WriteBytes("a.ts4script", bytes);
        temp.WriteBytes("b.ts4script", Zip(("broken.py", "bad"u8.ToArray()), ("healthy.py", "x=1"u8.ToArray())));

        var report = await Scan(temp.Path);

        Assert.AreEqual(2, report.AnalyzedArchiveCount);
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, report.Collisions.Single(c => c.ModuleName == "broken").Verdict);
        Assert.AreEqual(ScriptModuleVerdict.Identical, report.Collisions.Single(c => c.ModuleName == "healthy").Verdict);
        Assert.AreEqual("broken.py", report.Issues.Single().EntryName);
    }

    [TestMethod]
    public async Task MalformedDynamicHuffmanIsEntryCorrupt()
    {
        using var temp = new TempDirectory();
        var compressed = Convert.FromBase64String(
            "7cQ7DoAgEEBBaxOOsgmyK2LrRQjgFkaAnHOYT4SsMxojRYMNJPv////////PAMltBRlrlHHcTcx6lbKVIn0PZ1QNoVl6AA==");
        using var memory = new MemoryStream();
        memory.Write(RawDeflateZip(new byte[5052], finalBlock: true, compressedPayload: compressed));
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("normal.py", CompressionLevel.NoCompression).Open();
            stream.Write("y=1"u8);
        }
        var path = temp.WriteBytes("a.ts4script", memory.ToArray());
        temp.WriteBytes("b.ts4script", Zip(("m.py", "x=1"u8.ToArray()), ("normal.py", "y=1"u8.ToArray())));
        var fs = new TrackingFs();

        var report = await Scan(temp.Path, new ScriptModuleLimits(
            MaxTotalProcessedBytes: 9, MaxDegreeOfParallelism: 1), fs);

        Assert.AreEqual(2, report.AnalyzedArchiveCount,
            string.Join(",", report.Issues.Select(issue => $"{issue.Code}/{issue.Stage}")));
        var issue = report.Issues.Single();
        Assert.AreEqual(ScriptArchiveIssueCode.EntryCorrupt, issue.Code);
        Assert.AreEqual(ScriptArchiveIssueStage.Entry, issue.Stage);
        Assert.AreEqual(path, issue.Path);
        Assert.AreEqual("m.py", issue.EntryName);
        CollectionAssert.DoesNotContain(report.IncompleteArchivePaths.ToArray(), path);
        var broken = report.Collisions.Single(collision => collision.ModuleName == "m");
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, broken.Verdict);
        Assert.IsNull(broken.Uncompared.Single().Sha256);
        Assert.AreEqual(1, broken.ContentGroups.Single().Occurrences.Count);
        var normal = report.Collisions.Single(collision => collision.ModuleName == "normal");
        Assert.AreEqual(ScriptModuleVerdict.Identical, normal.Verdict);
        Assert.AreEqual(2, normal.ContentGroups.Single().Occurrences.Count);
        Assert.AreEqual(9L, report.ProcessedBytes);
        Assert.AreEqual(2, fs.OpenedCount);
        Assert.AreEqual(fs.OpenedCount, fs.DisposedCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptySourceEntriesSucceedEvenAfterAnotherModuleUsesTheBudget(bool compressed)
    {
        using var temp = new TempDirectory();
        var compression = compressed ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
        temp.WriteBytes("a.ts4script", Zip(compression, ("a.py", "x=1"u8.ToArray()), ("z.py", [])));
        temp.WriteBytes("b.ts4script", Zip(compression, ("z.py", [])));

        var report = await Scan(temp.Path, new ScriptModuleLimits(MaxTotalProcessedBytes: 3, MaxDegreeOfParallelism: 1));

        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(3L, report.ProcessedBytes);
        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Identical, collision.Verdict);
        Assert.IsTrue(collision.Occurrences.All(occurrence => occurrence.ContentLength == 0));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyRawDeflateBlocksSucceedAfterTheSharedBudgetIsConsumed(bool fixedHuffman)
    {
        using var temp = new TempDirectory();
        byte[] compressed = fixedHuffman ? [0x03, 0x00] : [0x01, 0x00, 0x00, 0xFF, 0xFF];
        var empty = RawDeflateZip([], finalBlock: true, compressed);
        Assert.AreEqual((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(empty.AsSpan(8)));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(empty.AsSpan(14)));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(empty.AsSpan(22)));
        temp.WriteBytes("a.ts4script", Zip(("a.py", "x=1"u8.ToArray())));
        temp.WriteBytes("b.ts4script", empty);
        temp.WriteBytes("c.ts4script", empty);

        var report = await Scan(temp.Path, new ScriptModuleLimits(MaxTotalProcessedBytes: 3, MaxDegreeOfParallelism: 1));

        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(3L, report.ProcessedBytes);
        Assert.AreEqual(3, report.AnalyzedArchiveCount);
        var collision = report.Collisions.Single();
        Assert.AreEqual("m", collision.ModuleName);
        Assert.AreEqual(ScriptModuleVerdict.Identical, collision.Verdict);
        Assert.IsTrue(collision.Occurrences.All(occurrence => occurrence.ContentLength == 0));
    }

    private static byte[] ZipWithDescriptor()
    {
        using var memory = new MemoryStream();
        using (var output = new NonSeekableWriteStream(memory))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("m.py", CompressionLevel.Optimal).Open();
            entry.Write("x=1"u8);
        }
        return memory.ToArray();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AValidZipWrittenWithoutSeekingSupportsDataDescriptors(bool withSignature)
    {
        using var temp = new TempDirectory();
        var bytes = ZipWithDescriptor();
        if (!withSignature)
        {
            var descriptor = Find(bytes, 0x08074b50);
            bytes = [.. bytes[..descriptor], .. bytes[(descriptor + 4)..]];
            var end = Find(bytes, 0x06054b50);
            var shiftedCentral = Find(bytes, 0x02014b50);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), (uint)shiftedCentral);
        }
        var central = Find(bytes, 0x02014b50);
        Assert.AreEqual(8, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) & 8);
        Assert.AreEqual(8, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 8)) & 8);
        temp.WriteBytes("a.ts4script", bytes);
        temp.WriteBytes("b.ts4script", Zip(("m.py", "x=1"u8.ToArray())));

        var report = await Scan(temp.Path);

        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(2, report.AnalyzedArchiveCount);
        Assert.AreEqual(ScriptModuleVerdict.Identical, report.Collisions.Single().Verdict);
        Assert.AreEqual(6L, report.ProcessedBytes);
    }

    [TestMethod]
    public async Task ValidCompressedStreamAtBothExactOutputLimitsSucceeds()
    {
        using var temp = new TempDirectory();
        var data = System.Text.Encoding.UTF8.GetBytes(new string('x', 8192));
        temp.WriteBytes("a.ts4script", Zip(CompressionLevel.Optimal, ("m.py", data)));

        var report = await Scan(temp.Path, new ScriptModuleLimits(
            MaxModuleBytes: data.Length, MaxTotalProcessedBytes: data.Length));

        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual((long)data.Length, report.ProcessedBytes);
        Assert.AreEqual(1, report.ModuleCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RawDeflateWithoutAFinalBlockIsCorruptEvenWhenOutputAndCrcMatch(bool exactBudget)
    {
        using var temp = new TempDirectory();
        var data = "x=1"u8.ToArray();
        // A non-final stored DEFLATE block produces all declared bytes and the right CRC,
        // but has no final block. Merely observing EOF from DeflateStream is insufficient.
        temp.WriteBytes("a.ts4script", RawDeflateZip(data, finalBlock: false));
        temp.WriteBytes("b.ts4script", RawDeflateZip(data, finalBlock: true));

        var report = await Scan(temp.Path, new ScriptModuleLimits(
            MaxTotalProcessedBytes: exactBudget ? 6 : 1024, MaxDegreeOfParallelism: 1));

        var collision = report.Collisions.Single();
        Assert.AreEqual(ScriptModuleVerdict.Incomplete, collision.Verdict);
        Assert.AreEqual(ScriptArchiveIssueCode.EntryCorrupt, collision.Uncompared.Single().Issue!.Code);
        Assert.AreEqual(1, collision.ContentGroups.Single().Occurrences.Count);
    }

    [TestMethod]
    public async Task ValidRawDeflateStoredBlockAtTheExactBudgetSucceeds()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("a.ts4script", RawDeflateZip("x=1"u8.ToArray(), finalBlock: true));

        var report = await Scan(temp.Path, new ScriptModuleLimits(MaxModuleBytes: 3, MaxTotalProcessedBytes: 3));

        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(3L, report.ProcessedBytes);
    }

    [TestMethod]
    public async Task EveryOpenedArchiveStreamIsDisposedAfterSuccessOrStructureFailure()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("valid.ts4script", Zip(("m.py", "x=1"u8.ToArray())));
        temp.WriteBytes("invalid.ts4script", new byte[32]);
        var fs = new TrackingFs();

        var report = await Scan(temp.Path, fs: fs);

        Assert.AreEqual(1, report.AnalyzedArchiveCount);
        Assert.AreEqual(2, fs.OpenedCount);
        Assert.AreEqual(fs.OpenedCount, fs.DisposedCount);
    }

    [TestMethod]
    public async Task CancellationDuringArchiveReadPropagatesAndDisposesTheStream()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("a.ts4script", Zip(("m.py", "x=1"u8.ToArray())));
        using var cancellation = new CancellationTokenSource();
        var fs = new TrackingFs(cancellation.Cancel);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Scan(temp.Path, fs: fs, cancellationToken: cancellation.Token));

        Assert.AreEqual(1, fs.OpenedCount);
        Assert.AreEqual(1, fs.DisposedCount);
    }

    private static byte[] RawDeflateZip(byte[] data, bool finalBlock, byte[]? compressedPayload = null)
    {
        const string name = "m.py";
        const ushort method = 8;
        var compressedLength = compressedPayload?.Length ?? data.Length + 5;
        uint crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        crc = ~crc;
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(0x04034b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write(method);
        writer.Write(0u);
        writer.Write(crc);
        writer.Write((uint)compressedLength);
        writer.Write((uint)data.Length);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0);
        writer.Write(System.Text.Encoding.UTF8.GetBytes(name));
        if (compressedPayload is not null)
        {
            writer.Write(compressedPayload);
        }
        else
        {
            writer.Write((byte)(finalBlock ? 1 : 0));
            writer.Write((ushort)data.Length);
            writer.Write((ushort)~data.Length);
            writer.Write(data);
        }
        var centralOffset = (uint)memory.Position;
        writer.Write(0x02014b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write(method);
        writer.Write(0u);
        writer.Write(crc);
        writer.Write((uint)compressedLength);
        writer.Write((uint)data.Length);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(System.Text.Encoding.UTF8.GetBytes(name));
        var centralLength = (uint)memory.Position - centralOffset;
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralLength);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
        return memory.ToArray();
    }

    private sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class TrackingFs(Action? onRead = null) : IFileSystemAccess
    {
        private readonly PhysicalFileSystemAccess _inner = new();
        private int _opened;
        private int _disposed;
        public int OpenedCount => _opened;
        public int DisposedCount => _disposed;
        public bool DirectoryExists(string p) => _inner.DirectoryExists(p);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string p) => _inner.EnumerateFileSystemEntries(p);
        public FileAttributes GetAttributes(string p) => _inner.GetAttributes(p);
        public FileStamp GetFileStamp(string p) => _inner.GetFileStamp(p);
        public Stream OpenRead(string p)
        {
            var stream = _inner.OpenRead(p);
            Interlocked.Increment(ref _opened);
            return new TrackingStream(stream, this, onRead);
        }
        public void RecordDisposal() => Interlocked.Increment(ref _disposed);
    }
    private sealed class TrackingStream(Stream inner, TrackingFs owner, Action? onRead) : Stream
    {
        private bool _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] b, int o, int c)
        {
            var read = inner.Read(b, o, c);
            onRead?.Invoke();
            return read;
        }
        public override int Read(Span<byte> b)
        {
            var read = inner.Read(b);
            onRead?.Invoke();
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken t = default)
        {
            var read = await inner.ReadAsync(b, t);
            onRead?.Invoke();
            return read;
        }
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                inner.Dispose();
                owner.RecordDisposal();
            }
            base.Dispose(disposing);
        }
    }
}
