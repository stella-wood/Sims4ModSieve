using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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

    public static string DescribeScanStorage(IEnumerable<string> sources)
    {
        var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            try
            {
                if (Path.GetPathRoot(Path.GetFullPath(source)) is { } root) volumes.Add(root);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                // 无效来源仍由扫描流程报告，性能诊断不改变来源校验。
            }
        }
        return string.Join("；", volumes.Select(volume => $"{volume} {DescribeVolume(volume)}"));
    }

    private static string DescribeVolume(string volume)
    {
        if (volume.Length != 3 || volume[1] != ':') return "SSD/HDD 未知";
        using var handle = CreateFile(@"\\.\" + volume[..2], 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return "SSD/HDD 未知";
        // StorageDeviceSeekPenaltyProperty；不请求读写磁盘数据的权限。
        var query = new byte[12];
        query[0] = 7;
        var result = new byte[12];
        if (!DeviceIoControl(handle, 0x002d1400, query, query.Length, result, result.Length, out var returned, IntPtr.Zero)
            || returned < 9) return "SSD/HDD 未知";
        return result[8] == 0 ? "无寻道惩罚（通常为 SSD）" : "有寻道惩罚（通常为 HDD）";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
