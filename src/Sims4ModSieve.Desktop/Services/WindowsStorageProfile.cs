using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Sims4ModSieve.Core.Duplicates;

#if CLI_STORAGE_PROFILE
namespace Sims4ModSieve.Cli;
#else
namespace Sims4ModSieve.Desktop.Services;
#endif

internal sealed class WindowsStorageProfile : IStorageProfile
{
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint VolumeNameGuid = 0x01;
    private const uint FileNameOpened = 0x08;
    private const uint IoctlStorageQueryProperty = 0x002d1400;
    private const byte StorageDeviceSeekPenaltyProperty = 7;
    private const string VolumePrefix = @"\\?\Volume{";
    private readonly Dictionary<string, StorageVolumeProfile> volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unresolved = [];

    public StorageVolumeProfile GetVolume(string directoryPath)
    {
        if (!OperatingSystem.IsWindows()) return Unknown("当前平台不支持卷探测");

        var path = Path.TrimEndingDirectorySeparator(directoryPath);
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;
        }

        using var directory = CreateFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (directory.IsInvalid) return Unknown("无法打开来源目录");
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(directory, buffer, (uint)buffer.Capacity, VolumeNameGuid | FileNameOpened);
        if (length >= buffer.Capacity && length <= 32_768)
        {
            buffer.EnsureCapacity((int)length);
            length = GetFinalPathNameByHandle(directory, buffer, (uint)buffer.Capacity, VolumeNameGuid | FileNameOpened);
        }
        if (length == 0 || length >= buffer.Capacity) return Unknown("无法解析本地卷（可能是网络位置）");

        var resolvedPath = buffer.ToString();
        var end = resolvedPath.IndexOf('}');
        if (end < 0 || !resolvedPath.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase))
            return Unknown("无法识别本地卷");

        var volume = resolvedPath[..(end + 1)];
        if (!volumes.TryGetValue(volume, out var result))
        {
            result = QueryVolume(volume);
            volumes.Add(volume, result);
        }
        return result;
    }

    public string Describe()
    {
        var descriptions = volumes.Values.Select((volume, index) => $"卷 {index + 1} " + (volume.IncursSeekPenalty switch
        {
            true => "有寻道惩罚（通常为 HDD）",
            false => "无寻道惩罚（通常为 SSD）",
            null => $"SSD/HDD 未知（{volume.UnknownReason}）",
        })).Concat(unresolved.Select(reason => $"SSD/HDD 未知（{reason}）")).ToArray();
        return descriptions.Length == 0 ? "未探测（手动档或没有参与哈希的来源）" : string.Join("；", descriptions);
    }

    private StorageVolumeProfile Unknown(string reason)
    {
        unresolved.Add(reason);
        return new(null, null, reason);
    }

    private static StorageVolumeProfile QueryVolume(string volume)
    {
        // The GUID path without a trailing slash opens the volume device, with no data access.
        using var handle = CreateFile(volume, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return new(volume, null, "无法打开存储卷");
        var query = new byte[12];
        query[0] = StorageDeviceSeekPenaltyProperty;
        var result = new byte[12];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, result, result.Length, out var returned, IntPtr.Zero)
            || returned < 9 || result[8] > 1) return new(volume, null, "设备未返回寻道惩罚");
        return new(volume, result[8] != 0);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
