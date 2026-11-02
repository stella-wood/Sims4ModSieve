using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sims4ModSieve.Core.Duplicates;

public static class PhysicalFileIdentity
{
    public static FileIdentity? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不支持重解析点文件操作");

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandleEx(handle, 18, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 1 or 50 or 87 or 120) return null;
            throw new IOException("无法读取文件身份", new Win32Exception(error));
        }
        return new FileIdentity(info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass,
        out FileIdInfo fileInformation, uint bufferSize);
}
