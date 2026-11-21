using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Sims4ModSieve.FileOperations;

public sealed class WindowsRecycleBinAdapter : IRecycleBinAdapter
{
    private const uint FofRenameOnCollision = 0x0008;
    private const uint FofxEarlyFailure = 0x00100000;
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofNoErrorUi = 0x0400;
    private const string RecycleBin = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";

    public bool CanRecycle(string path)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root)
                && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default) =>
        RunStaAsync(() => MoveToRecycleBin(paths, cancellationToken), cancellationToken);

    public Task<FileActionBatchResult> RestoreAsync(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken = default) =>
        RunStaAsync(() => Restore(items, cancellationToken), cancellationToken);

    public Task<RecycleBinPresence> QueryAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) =>
        RunStaAsync(() =>
        {
            var present = new List<RecycleBinItem>();
            var missing = new List<RecycleBinItem>();
            var failures = new List<FileActionFailure>();
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IShellItem? shell = null;
                try
                {
                    shell = CreateShellItem(item.AbsolutePidl);
                    var physicalPath = FileOperationProgressSink.GetFileSystemPath(shell);
                    if (!Path.IsPathFullyQualified(physicalPath)) throw new IOException("无法核对回收站条目");
                    // GetAttributes distinguishes a missing entry from an inaccessible recycle directory.
                    File.GetAttributes(physicalPath);
                    present.Add(item);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
                    || exception is COMException com && com.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
                { missing.Add(item); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or COMException)
                { failures.Add(new(item.OriginalPath, "无法核对回收站状态：" + exception.Message)); }
                finally { ReleaseComObject(shell); }
            }
            return new RecycleBinPresence(present, missing, failures);
        }, cancellationToken);

    private static RecycleBinDeleteResult MoveToRecycleBin(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var normalizedPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var completed = new List<RecycleBinItem>();
        var failures = new List<FileActionFailure>();
        for (var index = 0; index < normalizedPaths.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested && completed.Count > 0)
            {
                failures.AddRange(normalizedPaths.Skip(index).Select(path => new FileActionFailure(path, "操作已取消，此文件未执行")));
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = normalizedPaths[index];
            try { completed.Add(RecycleOne(path)); }
            catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { failures.Add(new(path, exception.Message)); }
        }
        return new(completed, failures);
    }

    private static RecycleBinItem RecycleOne(string path)
    {
        RequireRecycleEnabled(path);
        IShellItem? item = null, bin = null, parent = null, recycled = null;
        ITransferSource? transfer = null;
        try
        {
            item = CreateShellItem(path);
            bin = CreateShellItem(RecycleBin);
            ThrowIfFailed(item.GetParent(out parent));
            var handler = new Guid("D5E346A1-F753-4932-B403-4574800E2498"); // BHID_Transfer
            var iid = typeof(ITransferSource).GUID;
            ThrowIfFailed(parent.BindToHandler(IntPtr.Zero, ref handler, ref iid, out var pointer));
            try { transfer = (ITransferSource)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            // This Shell operation can only recycle. DeleteItem can fall back to permanent deletion.
            ThrowIfFailed(transfer.RecycleItem(item, bin, 0, out recycled));
            if (recycled is null) throw new IOException("Windows 未返回可撤回的回收站文件");
            return new(path, FileOperationProgressSink.GetAbsolutePidl(recycled), FileOperationProgressSink.GetFileSystemPath(recycled));
        }
        finally
        {
            ReleaseComObject(recycled);
            ReleaseComObject(transfer);
            ReleaseComObject(parent);
            ReleaseComObject(bin);
            ReleaseComObject(item);
        }
    }

    private static void RequireRecycleEnabled(string path)
    {
        using var policy = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
        var volume = new StringBuilder(512);
        var name = new StringBuilder(64);
        if (!GetVolumePathName(path, volume, volume.Capacity) || !GetVolumeNameForVolumeMountPoint(volume.ToString(), name, name.Capacity))
            throw new IOException("无法确定文件所在卷的回收站设置，未改动原文件");
        var volumeId = name.ToString()[10..^1]; // \\?\Volume{guid}\ → {guid}
        using var settings = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{volumeId}");
        if (policy?.GetValue("NoRecycleFiles") is int and not 0 || settings?.GetValue("NukeOnDelete") is int and not 0)
            throw new IOException("该卷的 Windows 回收站已禁用，未改动原文件");
        if (settings?.GetValue("MaxCapacity") is int megabytes && new FileInfo(path).Length > (long)(uint)megabytes * 1024 * 1024)
            throw new IOException("文件超过该卷的 Windows 回收站容量，未改动原文件");
    }

    private static FileActionBatchResult Restore(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken)
    {
        var completed = new List<string>();
        var failures = new List<FileActionFailure>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            {
                failures.Add(new FileActionFailure(item.OriginalPath, "原位置已有同名文件，未覆盖"));
                continue;
            }

            var parentPath = Path.GetDirectoryName(item.OriginalPath);
            if (string.IsNullOrWhiteSpace(parentPath) || !Directory.Exists(parentPath))
            {
                failures.Add(new FileActionFailure(item.OriginalPath, "原文件夹已不存在"));
                continue;
            }

            IFileOperation? operation = null;
            IShellItem? recycledItem = null;
            IShellItem? destinationFolder = null;
            try
            {
                recycledItem = CreateShellItem(item.AbsolutePidl);
                destinationFolder = CreateShellItem(parentPath);
                operation = CreateFileOperation();
                ThrowIfFailed(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofNoErrorUi | FofRenameOnCollision | FofxEarlyFailure));
                var sink = new FileOperationProgressSink(item.OriginalPath);
                ThrowIfFailed(operation.MoveItem(
                    recycledItem,
                    destinationFolder,
                    Path.GetFileName(item.OriginalPath),
                    sink));
                ThrowIfFailed(operation.PerformOperations());
                ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));

                if (!aborted && sink.MoveCompleted && StringComparer.OrdinalIgnoreCase.Equals(sink.MovedPath, item.OriginalPath))
                {
                    completed.Add(item.OriginalPath);
                }
                else
                {
                    failures.Add(new FileActionFailure(item.OriginalPath, sink.MoveCompleted
                        ? "原位置出现同名文件，Windows 已将恢复文件保存到：" + sink.MovedPath
                        : "Windows 未能从回收站恢复该文件"));
                }
            }
            catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
            {
                failures.Add(new FileActionFailure(item.OriginalPath, exception.Message));
            }
            finally
            {
                ReleaseComObject(recycledItem);
                ReleaseComObject(destinationFolder);
                ReleaseComObject(operation);
            }
        }

        return new FileActionBatchResult(completed, failures, items.Count > completed.Count);
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(action());
            }
            catch (OperationCanceledException exception)
            {
                completion.SetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Sims4ModSieve.WindowsRecycleBin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IFileOperation CreateFileOperation()
    {
        var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!;
        return (IFileOperation)Activator.CreateInstance(type)!;
    }

    private static IShellItem CreateShellItem(string path)
    {
        var shellItemId = typeof(IShellItem).GUID;
        ThrowIfFailed(SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out var item));
        return item;
    }

    private static IShellItem CreateShellItem(byte[] absolutePidl)
    {
        var offset = 0;
        while (offset + 2 <= absolutePidl.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(absolutePidl.AsSpan(offset, 2));
            if (length == 0)
            {
                if (offset != absolutePidl.Length - 2) throw new InvalidDataException("回收站记录的 PIDL 无效");
                break;
            }
            if (length < 2 || offset + length > absolutePidl.Length - 2) throw new InvalidDataException("回收站记录的 PIDL 无效");
            offset += length;
        }
        if (offset != absolutePidl.Length - 2) throw new InvalidDataException("回收站记录的 PIDL 缺少终止项");
        var memory = Marshal.AllocCoTaskMem(absolutePidl.Length);
        try
        {
            Marshal.Copy(absolutePidl, 0, memory, absolutePidl.Length);
            var shellItemId = typeof(IShellItem).GUID;
            ThrowIfFailed(SHCreateItemFromIDList(memory, ref shellItemId, out var item));
            return item;
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [ComVisible(true)]
    private sealed class FileOperationProgressSink(string restorePath) : IFileOperationProgressSink
    {
        public bool MoveCompleted { get; private set; }
        public string? MovedPath { get; private set; }

        public int StartOperations() => 0;

        public int FinishOperations(int result) => 0;

        public int PreRenameItem(uint flags, IShellItem item, string newName) => 0;

        public int PostRenameItem(uint flags, IShellItem item, string newName, int result, IShellItem? newItem) => 0;

        public int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) =>
            File.Exists(restorePath) || Directory.Exists(restorePath) ? unchecked((int)0x80070050) : 0;

        public int PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? newItem)
        {
            if (result >= 0 && newItem is not null)
            {
                try { MovedPath = GetFileSystemPath(newItem); MoveCompleted = true; }
                catch (COMException) { }
            }
            return 0;
        }

        public int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;

        public int PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? newItem) => 0;

        public int PreDeleteItem(uint flags, IShellItem item) => unchecked((int)0x800704C7);
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem) => 0;

        public int PreNewItem(uint flags, IShellItem destinationFolder, string newName) => 0;

        public int PostNewItem(uint flags, IShellItem destinationFolder, string newName, string? templateName, uint attributes, int result, IShellItem? newItem) => 0;

        public int UpdateProgress(uint workTotal, uint workSoFar) => 0;

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;

        public static string GetFileSystemPath(IShellItem item)
        {
            const uint sigdnFileSystemPath = 0x80058000;
            ThrowIfFailed(item.GetDisplayName(sigdnFileSystemPath, out var value));
            try
            {
                return Marshal.PtrToStringUni(value) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }

        public static byte[] GetAbsolutePidl(IShellItem item)
        {
            ThrowIfFailed(SHGetIDListFromObject(item, out var pidl));
            try
            {
                var length = checked((int)ILGetSize(pidl));
                var bytes = new byte[length];
                Marshal.Copy(pidl, bytes, 0, length);
                return bytes;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volumePath, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumePath, StringBuilder volumeName, int length);

    [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferSource
    {
        // The preceding slots are unused. Keep them in SDK vtable order.
        void Advise();
        void Unadvise();
        void SetProperties();
        void OpenItem();
        void MoveItem();
        [PreserveSig] int RecycleItem(IShellItem item, IShellItem destination, uint flags, out IShellItem recycled);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemFromIDList(
        IntPtr pidl,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetIDListFromObject(
        [MarshalAs(UnmanagedType.IUnknown)] object source,
        out IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr pidl);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindingContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(uint displayNameType, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint requestedAttributes, out uint attributes);

        [PreserveSig]
        int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object progressDialog);
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int NewItem([MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem item, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint fileAttributes, int result, [MarshalAs(UnmanagedType.Interface)] IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint workTotal, uint workSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
