using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Sims4ModSieve.Desktop.Services;

public sealed record DiagnosticLog(string Name, string Content, bool HasContent, bool IsTruncated, bool ReadFailed)
{
    public bool ShowEntry => HasContent || ReadFailed;
}

public sealed partial class SettingsSupportService
{
    public const int MaximumLogCharacters = 65_536;
    public const string ProjectUrl = "https://github.com/stella-wood/Sims4ModDoctor";
    private readonly string documentsDirectory;
    private readonly string applicationDirectory;
    private readonly Action<string> openTarget;
    private readonly Action<string> copyText;

    public SettingsSupportService(string? localApplicationData = null, string? documentsDirectory = null,
        string? applicationDirectory = null, Action<string>? openTarget = null, Action<string>? copyText = null)
    {
        LogDirectory = Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve");
        this.documentsDirectory = documentsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        this.applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        this.openTarget = openTarget ?? (target => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }));
        this.copyText = copyText ?? Clipboard.SetText;
    }

    public string LogDirectory { get; }
    public string ProductVersion => typeof(SettingsSupportService).Assembly.GetName().Version?.ToString() ?? string.Empty;
    public string SystemVersion => Environment.OSVersion.VersionString;

    public string? FindDefaultFolder(string kind)
    {
        if (kind is not ("Mods" or "Tray")) throw new ArgumentException("未知的文件夹类型", nameof(kind));
        var candidate = Path.Combine(documentsDirectory, "Electronic Arts", "The Sims 4", kind);
        return Directory.Exists(candidate) ? candidate : null;
    }

    public async Task<IReadOnlyList<DiagnosticLog>> ReadDiagnosticsAsync()
    {
        var logs = new List<DiagnosticLog>();
        foreach (var name in new[] { "application-error.log", "startup-error.log" })
        {
            try
            {
                await using var stream = new FileStream(Path.Combine(LogDirectory, name), FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaximumLogCharacters + 1];
                var count = await reader.ReadBlockAsync(buffer.AsMemory());
                var truncated = count > MaximumLogCharacters;
                var content = RedactDiagnosticPaths(new string(buffer, 0, Math.Min(count, MaximumLogCharacters)));
                if (truncated) content += "\n（日志过长，仅显示／复制前 65536 个字符）";
                logs.Add(new DiagnosticLog(name, content, !string.IsNullOrWhiteSpace(content), truncated, false));
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                logs.Add(new DiagnosticLog(name, "日志不存在", false, false, false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logs.Add(new DiagnosticLog(name, "无法读取此日志", false, false, true));
            }
        }
        return logs;
    }

    public static string RedactDiagnosticPaths(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            var parent = Path.GetDirectoryName(profile);
            if (!string.IsNullOrEmpty(parent))
            {
                text = text.Replace(profile.Replace("\\", "\\\\"), parent.Replace("\\", "\\\\") + "\\\\<用户>", StringComparison.OrdinalIgnoreCase);
                text = text.Replace(profile, parent + "\\<用户>", StringComparison.OrdinalIgnoreCase);
            }
        }
        return Regex.Replace(text, @"(\b[A-Z]:(?:\\+|/+)Users(?:\\+|/+))([^\\/\r\n""<>]+)", "$1<用户>", RegexOptions.IgnoreCase);
    }

    public string BuildDiagnosticText(IReadOnlyList<DiagnosticLog> logs) => RedactDiagnosticPaths(
        $"Sims 4 Mod Sieve {ProductVersion}\n系统：{SystemVersion}\n\n" +
        string.Join("\n\n", logs.Select(log => $"{log.Name}\n{(log.HasContent || log.ReadFailed ? log.Content : "暂时没有错误记录" + "（" + log.Content + "）")}")));

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不可访问");
        openTarget(Path.GetFullPath(path));
    }

    public void OpenProjectPage(bool feedback = false) => openTarget(feedback ? ProjectUrl + "/issues" : ProjectUrl);

    public void CopyDiagnosticText(IReadOnlyList<DiagnosticLog> logs) => copyText(BuildDiagnosticText(logs));

    public string OpenLicenseDocument(string document)
    {
        if (document is not ("LICENSE" or "THIRD-PARTY-NOTICES.md")) throw new ArgumentException("未知的许可文件", nameof(document));
        return File.ReadAllText(Path.Combine(applicationDirectory, document));
    }
}
