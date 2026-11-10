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
    [DataRow(0)]
    [DataRow(11)]
    public async Task SecondProcessCannotAcquireDataDirectoryUntilFirstExits(int startupDelaySeconds)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "application.lock");
        var script = $"$ErrorActionPreference = 'Stop'; Start-Sleep -Seconds {startupDelaySeconds}; "
            + $"$stream = [System.IO.File]::Open('{path.Replace("'", "''")}', 'OpenOrCreate', 'ReadWrite', 'None'); "
            + "[Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null; $stream.Dispose()";
        var start = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var child = Process.Start(start)!;
        var standardError = child.StandardError.ReadToEndAsync();
        try
        {
            string? ready;
            try
            {
                ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException exception)
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync();
                throw new TimeoutException($"PowerShell 子进程在 30 秒内未就绪。标准错误：{await standardError}", exception);
            }
            Assert.AreEqual("READY", ready, ready is null ? $"子进程提前关闭输出。标准错误：{await standardError}" : string.Empty);
            Assert.IsNull(SingleInstanceLock.TryAcquire(directory));
            await child.StandardInput.WriteLineAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, child.ExitCode, await standardError);
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
