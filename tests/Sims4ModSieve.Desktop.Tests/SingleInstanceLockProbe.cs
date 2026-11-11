namespace Sims4ModSieve.Desktop.Tests;

internal static class SingleInstanceLockProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--startup-splash", var scenario, var dataDirectory])
            return StartupSplashScreenTests.RunProbe(scenario, dataDirectory);

        if (args is not ["--single-instance-lock", var directory]) return 1;
        try
        {
            using var instance = SingleInstanceLock.TryAcquire(directory);
            if (instance is null)
            {
                Console.WriteLine("LOCKED");
                return 2;
            }
            Console.WriteLine("READY");
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
