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
                var application = new App(startUserInterface: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.InitializeComponent();
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

    public static void ShowOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.StateChanged += HideMaximizedWindow;
        window.Show();
        window.UpdateLayout();
    }

    private static void HideMaximizedWindow(object? sender, EventArgs args)
    {
        // 最大化会把屏外窗口移回显示器，透明度避免布局测试遮住桌面。
        var window = (Window)sender!;
        window.Opacity = window.WindowState == WindowState.Maximized ? 0 : 1;
    }

    // 仅真实鼠标输入、键盘焦点或前台激活测试使用；其余窗口用 ShowOffScreen。
    public static void PlaceOnScreen(Window window, double width = 1180, double height = 780, Rect? workArea = null)
    {
        var area = workArea ?? SystemParameters.WorkArea;
        window.StateChanged -= HideMaximizedWindow;
        window.Opacity = 1;
        window.ShowInTaskbar = false;
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
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        Console.WriteLine($"Native input window: workArea={area}; bounds={new Rect(window.Left, window.Top, window.Width, window.Height)}; dpi={dpi.PixelsPerInchX}x{dpi.PixelsPerInchY}");
    }

    public static async Task MoveMouseAsync(FrameworkElement element, Point? point = null)
    {
        var target = element.PointToScreen(point ?? new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        GetCursorPos(out var before);
        // 光标原本就在目标点、或窗口移到静止光标下方时，SetCursorPos 到同一点不会产生 WM_MOUSEMOVE，
        // WPF 收不到移动就不会更新 DirectlyOver。先偏移 1 像素再回到目标，确保产生真实移动。
        NudgeCursor(target);
        Assert.IsTrue(SetCursorPos((int)target.X, (int)target.Y), DescribeInput(element, target, before));
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(25);
            if (attempt is 10 or 20 or 30)
            {
                NudgeCursor(target);
                SetCursorPos((int)target.X, (int)target.Y);
            }
            Mouse.Synchronize();
            if (GetCursorPos(out var actual) && Math.Abs(actual.X - target.X) <= 1 && Math.Abs(actual.Y - target.Y) <= 1
                && IsWithin(Mouse.DirectlyOver as DependencyObject, element)) return;
        }
        Assert.Fail("鼠标定位后未实际命中目标；" + DescribeInput(element, target, before));
    }

    private static void NudgeCursor(Point target)
    {
        var x = (int)target.X;
        var y = (int)target.Y;
        SetCursorPos(x > 0 ? x - 1 : x + 1, y);
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

    private static string DescribeInput(FrameworkElement element, Point target, System.Drawing.Point? before = null)
    {
        GetCursorPos(out var actual);
        var window = Window.GetWindow(element);
        var dpi = VisualTreeHelper.GetDpi(element);
        return $"workArea={SystemParameters.WorkArea}; dpi={dpi.PixelsPerInchX}x{dpi.PixelsPerInchY}; target={target}; before={(before is { } b ? b.ToString() : "-")}; actual={actual}; element={element.GetType().Name}/{element.Name}; over={Mouse.DirectlyOver}; capture={Mouse.Captured}; active={window?.IsActive}";
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
