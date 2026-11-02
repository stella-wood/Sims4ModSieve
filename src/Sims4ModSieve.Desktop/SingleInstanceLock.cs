using System.IO;

namespace Sims4ModSieve.Desktop;

public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream stream;

    private SingleInstanceLock(FileStream stream) => this.stream = stream;

    public static SingleInstanceLock? TryAcquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var stream = new FileStream(Path.Combine(dataDirectory, "application.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceLock(stream);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }
    }

    public void Dispose() => stream.Dispose();
}
