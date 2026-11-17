using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Benchmarks;

internal sealed record ManifestEntry(string Path, FileStamp Stamp);
internal sealed record ManifestIssue(string Source, string ErrorType);
internal sealed record InputManifest(IReadOnlyList<ManifestEntry> Files, IReadOnlyList<ManifestIssue> Issues)
{
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(this, ResultSnapshot.JsonOptions)));

    internal static InputManifest Capture(IEnumerable<string> sources)
    {
        var files = new SortedDictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ManifestIssue>();
        var access = new PhysicalFileSystemAccess();
        foreach (var source in sources)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false,
                }))
                {
                    if (!new[] { ".package", ".ts4script" }.Contains(Path.GetExtension(path),
                            StringComparer.OrdinalIgnoreCase)) continue;
                    files[path] = new ManifestEntry(path, access.GetFileStamp(path));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ManifestIssue(source, exception.GetType().Name));
            }
        }
        return new(files.Values.ToArray(), issues);
    }
}

internal static class BenchmarkInputs
{
    internal static void Generate(string directory)
    {
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("Synthetic directory must not already exist.");
        Directory.CreateDirectory(directory);
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("synthetic-resource-", 2048)));
        var a = new DbpfFixtureBuilder().AddContent(payload, instanceLo: 1).Build();
        File.WriteAllBytes(Path.Combine(directory, "a.package"), a);
        File.WriteAllBytes(Path.Combine(directory, "a-copy.package"), a);
        File.WriteAllBytes(Path.Combine(directory, "same-content-zlib.package"), new DbpfFixtureBuilder()
            .AddContent(DbpfFixtureBuilder.Zlib(payload), DbpfFixtureBuilder.CompressionZlib,
                (uint)payload.Length, instanceLo: 1).Build());
        File.WriteAllBytes(Path.Combine(directory, "different.package"), new DbpfFixtureBuilder()
            .AddContent("different"u8.ToArray(), instanceLo: 1).Build());
        File.WriteAllBytes(Path.Combine(directory, "bad-zlib.package"), new DbpfFixtureBuilder()
            .AddContent([0x78, 0x9c, 0], DbpfFixtureBuilder.CompressionZlib, 100, instanceLo: 1).Build());
        File.WriteAllBytes(Path.Combine(directory, "unsupported.package"), new DbpfFixtureBuilder()
            .AddContent([1, 2, 3], DbpfFixtureBuilder.CompressionInternal, 10, instanceLo: 1).Build());
        File.WriteAllText(Path.Combine(directory, "bad-index.package"), "not a DBPF");
        for (var index = 0; index < 2; index++)
        {
            using var archive = ZipFile.Open(Path.Combine(directory, $"script-{index}.ts4script"), ZipArchiveMode.Create);
            var entry = archive.CreateEntry("library/shared.py", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write($"value = {index}\n");
        }
        File.WriteAllText(Path.Combine(directory, "bad.ts4script"), "not a ZIP");
        foreach (var file in Directory.EnumerateFiles(directory))
            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    internal static string ResolveDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var resolved = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, component);
            if (Directory.Exists(resolved))
                resolved = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    internal static void ValidateOutput(string output, IEnumerable<string> sources)
    {
        var resolvedOutput = ResolveDirectory(output);
        foreach (var source in sources)
        {
            var relative = Path.GetRelativePath(ResolveDirectory(source), resolvedOutput);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new ArgumentException("Output must be outside every scanned source (including links).");
        }
        if (Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("Output directory must not already exist.");
    }
}
