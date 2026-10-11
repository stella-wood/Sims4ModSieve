using System.Windows;
using System.Windows.Threading;

namespace Sims4ModSieve.Desktop.Tests;

internal static class WpfTestHost
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly Lazy<Task<Dispatcher>> DispatcherTask = new(Start);

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.InitializeComponent();
                // App 的默认启动窗口先关闭，再交给测试各自创建窗口。
                application.Dispatcher.BeginInvoke(() =>
                {
                    foreach (Window window in application.Windows.Cast<Window>().ToArray()) window.Close();
                    ready.SetResult(application.Dispatcher);
                }, DispatcherPriority.ApplicationIdle);
                Dispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true, Name = "WPF test host" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    public static Task RunAsync(Action action) => RunAsync(() => { action(); return Task.CompletedTask; });

    public static async Task RunAsync(Func<Task> action)
    {
        await Gate.WaitAsync();
        try
        {
            var dispatcher = await DispatcherTask.Value;
            await dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                finally
                {
                    foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
                }
            }).Task.Unwrap();
        }
        finally { Gate.Release(); }
    }

    public static async Task LayoutAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }
}
