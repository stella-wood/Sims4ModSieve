using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sims4ModSieve.Desktop.Tests;

internal static class WpfTestHost
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly Lazy<Task<Dispatcher>> DispatcherTask = new(Start);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

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
                catch
                {
                    SaveFailure();
                    throw;
                }
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

    public static void PlaceOnScreen(Window window, double width = 1180, double height = 780, Rect? workArea = null)
    {
        var area = workArea ?? SystemParameters.WorkArea;
        window.WindowState = WindowState.Normal;
        var availableWidth = Math.Min(width, area.Width - 32);
        var availableHeight = Math.Min(height, area.Height - 32);
        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        window.Width = availableWidth;
        window.Height = availableHeight;
        window.Left = area.Left + (area.Width - availableWidth) / 2;
        window.Top = area.Top + (area.Height - availableHeight) / 2;
        window.Topmost = true;
        window.Activate();
        var dpi = VisualTreeHelper.GetDpi(window);
        Console.WriteLine($"Native input window: workArea={area}; bounds={new Rect(window.Left, window.Top, window.Width, window.Height)}; dpi={dpi.PixelsPerInchX}x{dpi.PixelsPerInchY}");
    }

    public static async Task MoveMouseAsync(FrameworkElement element, Point? point = null)
    {
        var target = element.PointToScreen(point ?? new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        Assert.IsTrue(SetCursorPos((int)target.X, (int)target.Y), DescribeInput(element, target));
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(25);
            Mouse.Synchronize();
            if (GetCursorPos(out var actual) && Math.Abs(actual.X - target.X) <= 1 && Math.Abs(actual.Y - target.Y) <= 1
                && IsWithin(Mouse.DirectlyOver as DependencyObject, element)) return;
        }
        Assert.Fail("鼠标定位后未实际命中目标；" + DescribeInput(element, target));
    }

    public static async Task ClickAsync(FrameworkElement element, Point? point = null)
    {
        await MoveMouseAsync(element, point);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(150);
    }

    public static async Task BeginDragAsync(Thumb thumb, Point? point = null)
    {
        await MoveMouseAsync(thumb, point);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(25);
            if (thumb.IsDragging && IsWithin(Mouse.Captured as DependencyObject, thumb)) return;
        }
        Assert.Fail("拖动开始后滑块未捕获鼠标；" + DescribeInput(thumb, thumb.PointToScreen(new Point())));
    }

    public static void EndDrag() => mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);

    private static bool IsWithin(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static string DescribeInput(FrameworkElement element, Point target)
    {
        GetCursorPos(out var actual);
        var window = Window.GetWindow(element);
        var dpi = VisualTreeHelper.GetDpi(element);
        return $"workArea={SystemParameters.WorkArea}; dpi={dpi.PixelsPerInchX}x{dpi.PixelsPerInchY}; target={target}; actual={actual}; element={element.GetType().Name}/{element.Name}; over={Mouse.DirectlyOver}; capture={Mouse.Captured}; active={window?.IsActive}";
    }

    private static void SaveFailure()
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_WPF_FAILURE_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        try
        {
            Directory.CreateDirectory(output);
            foreach (Window window in Application.Current.Windows)
            {
                if (!window.IsVisible || window.Content is not FrameworkElement content) continue;
                window.UpdateLayout();
                Console.WriteLine("WPF failure: " + DescribeInput(window, window.PointToScreen(new Point())));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"failure-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{window.GetType().Name}.png"));
                encoder.Save(stream);
            }
        }
        catch (Exception exception) { Console.WriteLine("Failed to capture WPF diagnostics: " + exception); }
    }
}
