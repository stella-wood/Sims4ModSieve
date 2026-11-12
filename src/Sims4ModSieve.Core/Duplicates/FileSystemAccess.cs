namespace Sims4ModSieve.Core.Duplicates;

public interface IFileSystemAccess
{
    bool DirectoryExists(string path);

    IReadOnlyList<string> EnumerateFileSystemEntries(string directory);

    FileAttributes GetAttributes(string path);

    FileStamp GetFileStamp(string path);

    Stream OpenRead(string path);

    /// <summary>联接或符号链接指向的最终位置；不是链接或读不到时返回 null。</summary>
    string? GetLinkTarget(string path) => null;
}

public sealed class PhysicalFileSystemAccess : IFileSystemAccess
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).ToArray();

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public FileStamp GetFileStamp(string path)
    {
        var info = new FileInfo(path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc, PhysicalFileIdentity.Read(path));
    }

    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public string? GetLinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
