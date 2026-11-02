using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class SingleInstanceLockTests
{
    [TestMethod]
    public void UnrelatedIoFailureIsNotTreatedAsAnotherInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "application.lock"));
        try { Assert.ThrowsExactly<UnauthorizedAccessException>(() => SingleInstanceLock.TryAcquire(directory)); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task SecondProcessCannotAcquireDataDirectoryUntilFirstExits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "application.lock");
        var script = $"$stream = [System.IO.File]::Open('{path.Replace("'", "''")}', 'OpenOrCreate', 'ReadWrite', 'None'); "
            + "[Console]::Out.WriteLine('READY'); [Console]::In.ReadLine() | Out-Null; $stream.Dispose()";
        var start = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var child = Process.Start(start)!;
        try
        {
            Assert.AreEqual("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsNull(SingleInstanceLock.TryAcquire(directory));
            await child.StandardInput.WriteLineAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, child.ExitCode);
            using var acquired = SingleInstanceLock.TryAcquire(directory);
            Assert.IsNotNull(acquired);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
