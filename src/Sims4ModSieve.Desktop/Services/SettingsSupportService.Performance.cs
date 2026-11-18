using System.IO;
using System.Text;

namespace Sims4ModSieve.Desktop.Services;

public sealed partial class SettingsSupportService
{
    public const string RuntimeLogName = "application.log";
    public const string LatestScanTimingName = "scan-performance-latest.log";
    private const int MaximumSummaryLogBytes = 2 * 1024 * 1024;
    private string? latestScanTiming;
    private readonly SemaphoreSlim scanTimingWriteGate = new(1);

    public async Task<string> WriteScanTimingAsync(string summary)
    {
        await scanTimingWriteGate.WaitAsync();
        try
        {
            latestScanTiming = summary;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, RuntimeLogName);
                var entry = summary + Environment.NewLine;
                var current = new FileInfo(path);
                if (current.Exists && current.Length > 0 && current.Length + Encoding.UTF8.GetByteCount(entry) > MaximumSummaryLogBytes)
                {
                    var previous = Path.Combine(LogDirectory, "application.1.log");
                    if (File.Exists(previous)) File.Move(previous, Path.Combine(LogDirectory, "application.2.log"), overwrite: true);
                    File.Move(path, previous, overwrite: true);
                }
                await File.AppendAllTextAsync(path, entry);
                await File.WriteAllTextAsync(Path.Combine(LogDirectory, LatestScanTimingName), summary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                latestScanTiming += "\n计时日志写入失败，本次汇总仍可复制";
            }
            return latestScanTiming;
        }
        finally
        {
            scanTimingWriteGate.Release();
        }
    }

    public async Task<string> ReadLatestScanTimingAsync()
    {
        if (latestScanTiming is not null) return latestScanTiming;
        try
        {
            using var reader = new StreamReader(new FileStream(Path.Combine(LogDirectory, LatestScanTimingName),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var buffer = new char[MaximumLogCharacters + 1];
            var count = await reader.ReadBlockAsync(buffer.AsMemory());
            var text = RedactDiagnosticPaths(new string(buffer, 0, Math.Min(count, MaximumLogCharacters)));
            return count > MaximumLogCharacters ? text + "\n（计时汇总过长，仅显示／复制前 65536 个字符）" : text;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return "尚无扫描计时";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "无法读取扫描计时";
        }
    }

    public void CopyScanTiming(string summary) => copyText(summary);
}
