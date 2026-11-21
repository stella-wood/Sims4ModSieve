using System.Diagnostics;

namespace Sims4ModSieve.FileOperations;

internal sealed class FileManagementLock(string? directory)
{
    private static readonly AsyncLocal<Lease?> Held = new();
    private readonly string? path = directory is null ? null : Path.GetFullPath(Path.Combine(directory, ".file-management.lock"));

    public IDisposable? Enter()
    {
        if (path is null) return null;
        for (var lease = Held.Value; lease is not null; lease = lease.Parent)
            if (StringComparer.OrdinalIgnoreCase.Equals(lease.Path, path)) return null;

        var stream = OpenWriter(path, FileMode.OpenOrCreate, FileShare.None,
            "另一个 Sieve 操作正在使用文件管理记录，请稍后重试");
        return Held.Value = new Lease(path, stream, Held.Value);
    }

    internal static FileStream OpenWriter(string path, FileMode mode, FileShare share, string busyMessage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(path, mode, FileAccess.Write, share); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(2))
                    throw new IOException(busyMessage, exception);
                Thread.Sleep(25);
            }
        }
    }

    // A file handle can span awaits and Windows releases it if the process exits.
    private sealed record Lease(string Path, FileStream Stream, Lease? Parent) : IDisposable
    {
        public void Dispose()
        {
            Held.Value = Parent;
            Stream.Dispose();
        }
    }
}
