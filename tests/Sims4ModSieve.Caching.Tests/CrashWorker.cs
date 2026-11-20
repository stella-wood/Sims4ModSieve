namespace Sims4ModSieve.Caching.Tests;

internal static class CrashWorker
{
    // The test assembly is also a subprocess so the crash test never kills its test runner.
    public static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var cache = new SqliteSha256Cache(args[0], "app-1");
        using var scan = cache.BeginScan();
        for (var i = 1; i <= 2; i++)
            if (!scan.Stage(args[0] + $"-new-{i}", SqliteSha256CacheTests.Stamp, SqliteSha256CacheTests.Hash)) return 1;
        Console.WriteLine("staged");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
