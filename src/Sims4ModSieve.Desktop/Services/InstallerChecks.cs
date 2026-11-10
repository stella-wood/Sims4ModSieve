using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Sims4ModSieve.Desktop.Services;

public static class InstallerChecks
{
    public const int UnsafePath = 10;
    public const int CannotCheck = 11;
    public const int DisabledMods = 20;

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = CannotCheck;
        if (args.Length == 0 || !args[0].StartsWith("--installer-", StringComparison.Ordinal)) return false;
        if (args is ["--installer-check-path", var target, var data, var documents])
            exitCode = CheckPath(target, data, documents);
        else if (args is ["--installer-check-uninstall", var directory])
            exitCode = CheckUninstall(directory);
        else if (args is ["--installer-check-uninstall", var dataDirectory, var reportPath])
            exitCode = CheckUninstall(dataDirectory, reportPath);
        return true;
    }

    public static int CheckPath(string target, string dataDirectory, string documentsDirectory)
    {
        try
        {
            var normalized = ResolvePath(target);
            var roots = new List<string>
            {
                dataDirectory,
                Path.Combine(documentsDirectory, "Electronic Arts", "The Sims 4"),
            };
            var parent = Path.GetDirectoryName(Path.GetFullPath(dataDirectory))!;
            foreach (var settingsPath in new[] { Path.Combine(dataDirectory, "duplicate-settings.json"),
                Path.Combine(parent, "Sim4ModSieve", "duplicate-settings.json"), Path.Combine(parent, "Sims4ModDoctor", "duplicate-settings.json") })
            {
                string json;
                try { json = File.ReadAllText(settingsPath); }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
                var settings = JsonSerializer.Deserialize<DuplicateSettings>(json)
                    ?? throw new InvalidDataException();
                if (settings.Sources is null) throw new InvalidDataException();
                if (settings.ModsRoot is not null) roots.Add(settings.ModsRoot);
                if (settings.TrayRoot is not null) roots.Add(settings.TrayRoot);
                break;
            }
            var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
                if (parts[i - 1].Equals("Electronic Arts", StringComparison.OrdinalIgnoreCase)
                    && parts[i].Equals("The Sims 4", StringComparison.OrdinalIgnoreCase)) return UnsafePath;
            return roots.Any(root => IsWithin(normalized, ResolvePath(root))) ? UnsafePath : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException) { return CannotCheck; }
    }

    public static int CheckUninstall(string dataDirectory, string? reportPath = null)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "disabled-mods.json");
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { json = "[]"; }
            var records = JsonSerializer.Deserialize<DisabledModRecord[]>(json) ?? throw new InvalidDataException();
            if (records.Any(record => record is null || !ModDisableService.IsValidRecord(record))
                || records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != records.Length)
                return CannotCheck;
            // 不调用 Load：它会对中断的操作重新记账，卸载检查必须只读。
            var pending = records.Where(record => !record.Recycled || record.RecyclePending || record.RestorePending).ToArray();
            if (reportPath is not null)
            {
                var folders = pending.Select(record => Path.GetDirectoryName(record.DisabledPath)!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (folders.Any(folder => folder.IndexOfAny(['\r', '\n', '\0']) >= 0)) throw new InvalidDataException();
                var report = new StringBuilder("[Uninstall]\r\n")
                    .AppendLine($"DisabledCount={pending.Length}").AppendLine($"FolderCount={folders.Length}");
                for (var i = 0; i < folders.Length; i++) report.AppendLine($"Folder{i}={folders[i]}");
                File.WriteAllText(reportPath, report.ToString(), Encoding.Unicode);
            }
            return pending.Length > 0 ? DisabledMods : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException) { return CannotCheck; }
    }

    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ResolvePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("路径必须为绝对路径");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full[8..];
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("不支持设备路径");
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("目标路径包含文件");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new IOException("无法解析目录链接");
            var longPath = new StringBuilder(32768);
            var count = GetLongPathName(current, longPath, (uint)longPath.Capacity);
            if (count == 0 || count >= longPath.Capacity) throw new IOException("无法解析目录路径");
            current = longPath.ToString();
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string path, StringBuilder result, uint length);
}
