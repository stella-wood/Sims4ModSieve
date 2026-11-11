using System.Windows;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (InstallerChecks.TryRun(args, out var installerExitCode)) return installerExitCode;

        var splashScreen = new SplashScreen("assets/splash.png");
        var app = new App(splashScreen);
        try
        {
            app.InitializeComponent();
            return app.Run();
        }
        finally
        {
            splashScreen.Close(TimeSpan.Zero);
        }
    }
}
