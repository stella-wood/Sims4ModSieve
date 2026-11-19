using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Sims4ModSieve.Desktop.Tests;

internal static class NativeInputFailureDiagnostics
{
    public static void Save(Window window, FrameworkElement target)
    {
        var directory = Environment.GetEnvironmentVariable("SIMS4_WPF_FAILURE_DIR");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            var center = target.PointToScreen(new System.Windows.Point(target.ActualWidth / 2, target.ActualHeight / 2));
            var point = new System.Drawing.Point((int)center.X, (int)center.Y);
            GetCursorPos(out var cursor);
            var expected = new WindowInteropHelper(window).Handle;
            var atPoint = WindowFromPoint(point);
            var foreground = GetForegroundWindow();
            var state = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            GetGUIThreadInfo(0, ref state);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"native-input-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}");

            string[] details =
            [
                $"target={point}; cursor={cursor}",
                Describe("expected", expected),
                Describe("atTarget", atPoint),
                Describe("targetRoot", GetAncestor(atPoint, 2)),
                Describe("atCursor", WindowFromPoint(cursor)),
                Describe("foreground", foreground),
                Describe("nativeCapture", state.Capture),
                $"GUI active=0x{state.Active:X}; focus=0x{state.Focus:X}; flags=0x{state.Flags:X}",
                $"WPF directlyOver={Mouse.DirectlyOver}; captured={Mouse.Captured}; hit={window.InputHitTest(window.PointFromScreen(center))}",
            ];
            File.WriteAllLines(path + ".txt", details);
            foreach (var line in details) Console.WriteLine("Native input failure: " + line);
            SaveScreen(path + ".png");
        }
        catch (Exception exception) { Console.WriteLine("Failed to capture the native input failure: " + exception); }
    }

    private static string Describe(string label, IntPtr window)
    {
        if (window == IntPtr.Zero) return $"{label}=0";
        var className = new StringBuilder(256);
        var title = new StringBuilder(256);
        var thread = GetWindowThreadProcessId(window, out var process);
        GetClassName(window, className, className.Capacity);
        GetWindowText(window, title, title.Capacity);
        GetWindowRect(window, out var bounds);
        return $"{label}=0x{window:X}; pid={process}; tid={thread}; class={className}; title={title}; " +
            $"bounds=({bounds.Left},{bounds.Top})–({bounds.Right},{bounds.Bottom}); visible={IsWindowVisible(window)}; enabled={IsWindowEnabled(window)}";
    }

    private static void SaveScreen(string path)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate a desktop bitmap.");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1) ||
                !BitBlt(memory, 0, 0, width, height, screen, left, top, 0x40CC0020))
                throw new InvalidOperationException("Could not copy desktop pixels.");

            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretBounds;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint kind);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr context);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr context);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr context, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr context, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int left, int top, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr context);
}
