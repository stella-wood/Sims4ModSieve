using System.Diagnostics;
using System.Runtime.InteropServices;
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
        using var child = StartProbe(directory);
        var standardError = child.StandardError.ReadToEndAsync();
        try
        {
            string? ready;
            try
            {
                ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException exception)
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync();
                throw new TimeoutException($"锁探针在 10 秒内未就绪。标准错误：{await standardError}", exception);
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

    [TestMethod]
    public async Task ChildProcessCannotAcquireDirectoryHeldByParent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var acquired = SingleInstanceLock.TryAcquire(directory);
            Assert.IsNotNull(acquired);
            using var child = StartProbe(directory);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(2, child.ExitCode, await error);
                Assert.AreEqual("LOCKED", (await output).Trim());
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync();
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ProbeReportsIoFailureWithoutClaimingReadiness()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "application.lock"));
        using var child = StartProbe(directory);
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, child.ExitCode);
            Assert.AreEqual(string.Empty, await output);
            StringAssert.Contains(await error, nameof(UnauthorizedAccessException));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Process StartProbe(string directory)
    {
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(SingleInstanceLockProbe).Assembly.Location);
        start.ArgumentList.Add("--single-instance-lock");
        start.ArgumentList.Add(directory);
        return Process.Start(start)!;
    }
}
