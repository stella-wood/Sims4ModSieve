using Sims4ModSieve.Core.Settings;
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
        else if (args is ["--installer-check-path", var installTarget, var installData, var installDocuments, var pathReport])
            exitCode = CheckPath(installTarget, installData, installDocuments, pathReport);
        else if (args is ["--installer-check-uninstall", var directory])
            exitCode = CheckUninstall(directory);
        else if (args is ["--installer-check-uninstall", var dataDirectory, var reportPath])
            exitCode = CheckUninstall(dataDirectory, reportPath);
        return true;
    }

    public static int CheckPath(string target, string dataDirectory, string documentsDirectory, string? reportPath = null)
    {
        var entries = new List<string>();
        var (targetFull, targetResolved, reason) = CheckDirectory("安装位置", target, entries);
        var gamePath = Path.Combine(documentsDirectory, "Electronic Arts", "The Sims 4");
        var match = IsGamePath(targetFull) || IsGamePath(targetResolved) ? "游戏用户目录" : null;
        CheckRoot("工具数据目录", dataDirectory);
        CheckRoot("文档中的《模拟人生 4》文件夹", gamePath);

        var settings = ReadSettings(dataDirectory, entries);
        if (settings?.ModsRoot is { } mods) CheckRoot("Mods", mods);
        else entries.Add("Mods：没有可用的自定义位置");
        if (settings?.TrayRoot is { } tray) CheckRoot("Tray", tray);
        else entries.Add("Tray：没有可用的自定义位置");

        if (match is not null) entries.Add($"命中受保护目录：{match}");
        if (reportPath is not null) WritePathReport(reportPath, reason, entries);
        return match is not null ? UnsafePath : targetResolved is null ? CannotCheck : 0;

        void CheckRoot(string name, string path)
        {
            var (full, resolved, _) = CheckDirectory(name, path, entries);
            if (targetResolved is not null && resolved is not null)
            {
                if (IsWithin(targetResolved, resolved)) match ??= name;
            }
            else if (full is not null && ((targetFull is not null && IsWithin(targetFull, full))
                || (targetResolved is not null && IsWithin(targetResolved, full))))
            {
                match ??= name;
                entries.Add($"{name}：按完整路径匹配");
            }
        }
    }

    private static DuplicateSettings? ReadSettings(string dataDirectory, List<string> entries)
    {
        var files = new List<string>();
        try
        {
            var data = NormalizePath(dataDirectory);
            files.Add(Path.Combine(data, "duplicate-settings.json"));
            var parent = Path.GetDirectoryName(data);
            if (parent is null) entries.Add("无法定位旧设置：工具数据目录没有上一级目录");
            else
            {
                files.Add(Path.Combine(parent, "Sim4ModSieve", "duplicate-settings.json"));
                files.Add(Path.Combine(parent, "Sims4ModDoctor", "duplicate-settings.json"));
            }
        }
        catch (Exception exception) when (IsPathCheckError(exception))
        {
            entries.Add($"无法定位设置：{exception.Message}");
        }

        foreach (var file in files)
        {
            try
            {
                var settings = JsonSerializer.Deserialize<DuplicateSettings>(File.ReadAllText(file));
                if (settings?.Sources is null) throw new InvalidDataException("设置为空或缺少 Sources 字段");
                entries.Add($"读取设置：{file}");
                return settings;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                entries.Add($"设置不存在：{file}");
            }
            catch (Exception exception) when (IsPathCheckError(exception) || exception is JsonException or InvalidDataException)
            {
                entries.Add($"跳过设置：{file}。{exception.Message}");
            }
        }
        return null;
    }

    private static (string? Full, string? Resolved, string Reason) CheckDirectory(string name, string path, List<string> entries)
    {
        string? full = null;
        try
        {
            full = NormalizePath(path);
            var resolved = ResolvePath(full);
            entries.Add($"{name}：{path} → {resolved}");
            return (full, resolved, string.Empty);
        }
        catch (Exception exception) when (IsPathCheckError(exception))
        {
            entries.Add($"{name}：{path}。解析失败，{exception.Message}。" +
                (full is null ? "无可用的完整路径" : $"使用完整路径 {full}"));
            var detail = exception is UnauthorizedAccessException ? "Windows 拒绝了访问" : exception.Message.TrimEnd('。', '.');
            return (full, null, $"无法解析所选安装位置。{detail}。");
        }
    }

    private static bool IsPathCheckError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException;

    private static bool IsGamePath(string? path)
    {
        if (path is null) return false;
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
            if (parts[i - 1].Equals("Electronic Arts", StringComparison.OrdinalIgnoreCase)
                && parts[i].Equals("The Sims 4", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void WritePathReport(string reportPath, string reason, List<string> entries)
    {
        static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        var report = new StringBuilder("[PathCheck]\r\n")
            .AppendLine($"Reason={SingleLine(reason)}").AppendLine($"EntryCount={entries.Count}");
        for (var i = 0; i < entries.Count; i++) report.AppendLine($"Entry{i}={SingleLine(entries[i])}");
        try { File.WriteAllText(reportPath, report.ToString(), Encoding.Unicode); }
        catch (Exception exception) when (IsPathCheckError(exception)) { /* 退出码仍用于安装判断。 */ }
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
            var pending = records.Where(record => !record.IsExternal && (!record.Recycled || record.RecyclePending || record.RestorePending)).ToArray();
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

    private static string NormalizePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("路径必须为绝对路径");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full[8..];
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("不支持设备路径");
        if (!Path.IsPathFullyQualified(full)) throw new ArgumentException("不支持此类路径");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static string ResolvePath(string full)
    {
        var current = Path.GetPathRoot(full)!;
        if (current.Length == 3 && current[1] == ':' && !new DriveInfo(current).IsReady)
            throw new IOException("所在的盘当前无法访问");
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("目标路径包含文件");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("目录链接无法解析，无法确认它指向哪里");
                if (!Directory.Exists(current)) throw new IOException($"目录链接指向的位置当前无法访问：{current}");
            }
            var longPath = new StringBuilder(32768);
            var count = GetLongPathName(current, longPath, (uint)longPath.Capacity);
            if (count == 0) throw new IOException($"无法读取完整目录名（Windows 错误 {Marshal.GetLastPInvokeError()}）");
            if (count >= longPath.Capacity) throw new IOException("完整目录名过长");
            current = longPath.ToString();
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string path, StringBuilder result, uint length);
}
