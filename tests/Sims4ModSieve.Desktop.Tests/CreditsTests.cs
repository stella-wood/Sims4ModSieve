using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class CreditsTests
{
    [TestMethod]
    public void ConfettiMilestonesOccurOnceAndResetOnLeaving()
    {
        var state = new CreditsInteractionState();
        var expected = new Dictionary<int, CreditsSurprise>
        {
            [5] = CreditsSurprise.Clawd, [6] = CreditsSurprise.Star,
            [7] = CreditsSurprise.ChatGpt, [8] = CreditsSurprise.DeepSeek, [20] = CreditsSurprise.Message
        };
        for (var click = 1; click <= 45; click++)
            Assert.AreEqual(expected.GetValueOrDefault(click), state.ClickConfetti(), $"第 {click} 次点击");
        state.Reset();
        for (var click = 1; click <= 20; click++)
            Assert.AreEqual(expected.GetValueOrDefault(click), state.ClickConfetti(), $"重置后第 {click} 次点击");
    }

    [TestMethod]
    public void HeartBurstGrowsToThreeAndResetsAfterPauseOrLeaving()
    {
        var state = new CreditsInteractionState();
        var now = DateTimeOffset.UtcNow;
        Assert.AreEqual(1, state.ClickHeart(now));
        Assert.AreEqual(2, state.ClickHeart(now.AddMilliseconds(100)));
        Assert.AreEqual(3, state.ClickHeart(now.AddMilliseconds(200)));
        Assert.AreEqual(3, state.ClickHeart(now.AddMilliseconds(300)));
        Assert.AreEqual(1, state.ClickHeart(now.AddSeconds(1)));
        state.Reset();
        Assert.AreEqual(1, state.ClickHeart(now.AddSeconds(1.1)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("[invalid")]
    [DataRow("{\"name\":\"unexpected\"}")]
    [DataRow("[null, \"  \"]")]
    public void EmptyOrInvalidSupporterDataReturnsNoNames(string json)
    {
        var support = new SettingsSupportService(openSupporters: () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.AreEqual(0, support.ReadSupporters().Count);
    }

    [TestMethod]
    public void MissingOrUnreadableSupporterResourceReturnsNoNames()
    {
        foreach (var open in new Func<Stream?>[] { () => null, () => throw new IOException(), () => throw new UnauthorizedAccessException() })
            Assert.AreEqual(0, new SettingsSupportService(openSupporters: open).ReadSupporters().Count);
    }

    [TestMethod]
    public Task EmbeddedSupporterResourceIsUsedByVisiblePage() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        using var stream = typeof(SettingsPage).Assembly.GetManifestResourceStream("Sims4ModSieve.Desktop.Assets.supporters.json");
        Assert.IsNotNull(stream, "名单必须作为嵌入资源打包。");
        var names = (JsonSerializer.Deserialize<string?[]>(stream) ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).ToArray();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var list = (ItemsControl)panel.FindName("SupporterList");
        CollectionAssert.AreEqual(names, list.Items.Cast<string>().ToArray());
        Assert.AreEqual(names.Length > 0, list.IsVisible);
        if (names.Length > 0)
            foreach (var name in names) Assert.IsTrue(Descendants<TextBlock>(list).Any(text => text.Text == name));
        Assert.IsFalse(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "supporters.json")));
        SaveReview(window, "credits-empty.png");
    });

    [TestMethod]
    public Task AddingNamesToJsonDisplaysThemWithoutCodeChanges() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var path = Path.Combine(files.Root, "supporters.json");
        var names = new[] { "支持者示例 01", "支持者示例 02", "支持者示例 03", "支持者示例 04", "支持者示例 05", "支持者示例 06" };
        async Task<Window> ShowNamesAsync(string[] expected)
        {
            var model = files.Model(support: new SettingsSupportService(files.Root, openSupporters: () => File.OpenRead(path)));
            var window = new MainWindow(model) { Width = 1180, Height = 780 };
            window.Show();
            model.OpenSettingsCommand.Execute(null);
            model.SelectSettingsSectionCommand.Execute("credits");
            await WpfTestHost.LayoutAsync(window);
            var panel = Descendants<CreditsPanel>(window).Single();
            var list = (ItemsControl)panel.FindName("SupporterList");
            Assert.AreEqual(expected.Length > 0, list.IsVisible);
            CollectionAssert.AreEqual(expected, list.Items.Cast<string>().ToArray());
            foreach (var name in expected)
                Assert.IsTrue(Descendants<TextBlock>(list).Any(text => text.IsVisible && text.Text == name), name);
            return window;
        }
        File.WriteAllText(path, "[]");
        (await ShowNamesAsync([])).Close();
        File.WriteAllText(path, JsonSerializer.Serialize(names));
        var preview = await ShowNamesAsync(names);
        SaveReview(preview, "credits-names-preview.png");
        preview.Close();
        File.WriteAllText(path, "broken json");
        var empty = await ShowNamesAsync([]);
        var button = (Button)Descendants<CreditsPanel>(empty).Single().FindName("ConfettiButton");
        Assert.IsTrue(button.IsEnabled);
    });

    [TestMethod]
    public Task CreditsClicksTriggerEffectsWithoutOverlayBlockingAndLeavingResets() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var nativeInput = Environment.GetEnvironmentVariable("SIMS4_CREDITS_NATIVE_REVIEW") == "1";
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        if (nativeInput) WpfTestHost.PlaceOnScreen(window);
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var nav = (RadioButton)page.FindName("CreditsSectionButton");
        var navigation = ((Panel)nav.Parent).Children.OfType<RadioButton>().ToList();
        Assert.AreEqual(navigation.IndexOf(nav) + 1, navigation.IndexOf((RadioButton)page.FindName("AboutSectionButton")),
            "感谢名单应紧邻并位于关于之前。");
        AssertHitRectangle(nav);
        if (nativeInput) await WpfTestHost.ClickAsync(nav, new Point(4, nav.ActualHeight / 2));
        else PressSpace(nav);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(model.IsCreditsSettings);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var overlay = (Canvas)panel.FindName("EffectsOverlay");
        var secret = (Border)panel.FindName("SecretMessage");
        Assert.IsFalse(overlay.IsHitTestVisible);
        AssertHitRectangle(button);
        if (nativeInput) await WpfTestHost.ClickAsync(button, new Point(3, button.ActualHeight / 2));
        else
        {
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            PressSpace(button);
        }
        Assert.IsTrue(overlay.Children.Cast<FrameworkElement>().Any(item => Equals(item.Tag, "Heart")));
        Assert.IsTrue(overlay.Children.Cast<FrameworkElement>().Any(item => Equals(item.Tag, "Ribbon") || Equals(item.Tag, "Petal")));
        var heart = overlay.Children.Cast<FrameworkElement>().First(item => Equals(item.Tag, "Heart"));
        var initialX = Canvas.GetLeft(heart);
        var initialY = Canvas.GetTop(heart);
        await Task.Delay(120);
        Assert.IsGreaterThan(initialX, Canvas.GetLeft(heart));
        Assert.IsLessThan(initialY, Canvas.GetTop(heart));
        if (Environment.GetEnvironmentVariable("SIMS4_CREDITS_ANIMATION_REVIEW") == "1")
        {
            for (var frame = 0; frame < 32; frame++)
            {
                SaveReview(window, $"burst-{frame:D2}.png");
                await Task.Delay(125);
            }
        }
        else await Task.Delay(4000);
        Assert.AreEqual(0, overlay.Children.Count, "动画结束必须移除元素。");
        for (var click = 2; click <= 8; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        foreach (var tag in new[] { "Clawd", "Star", "ChatGpt", "DeepSeek" })
            Assert.IsTrue(overlay.Children.Cast<FrameworkElement>().Any(item => Equals(item.Tag, tag)), tag);
        await Task.Delay(350);
        SaveReview(window, "credits-confetti.png");
        for (var click = 9; click <= 20; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.IsTrue(secret.IsVisible);
        await Task.Delay(650);
        Assert.IsTrue(secret.Opacity > 0);
        SaveReview(window, "credits-secret.png");
        await Task.Delay(5600);
        Assert.IsFalse(secret.IsVisible);
        for (var click = 21; click <= 45; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.IsFalse(secret.IsVisible, "第 20 次之后不重复出现文字。");
        Assert.IsTrue(overlay.Children.Count <= CreditsPanel.MaximumParticles + CreditsPanel.MaximumHearts);
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, overlay.Children.Count);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        for (var click = 1; click <= 5; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.IsTrue(overlay.Children.Cast<FrameworkElement>().Any(item => Equals(item.Tag, "Clawd")));
        model.GoHomeCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, overlay.Children.Count);
    });

    [TestMethod]
    public Task ParticleLimitsAndAccessibleConfettiRemainUsable() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 920, Height = 640 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var overlay = (Canvas)panel.FindName("EffectsOverlay");
        for (var index = 0; index < 100; index++)
        {
            ((Grid)panel.FindName("CelebrationSurface")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        Assert.AreEqual(CreditsPanel.MaximumHearts, overlay.Children.Cast<FrameworkElement>().Count(item => Equals(item.Tag, "Heart")));
        Assert.AreEqual(CreditsPanel.MaximumParticles, overlay.Children.Cast<FrameworkElement>().Count(item => !Equals(item.Tag, "Heart")));
        Assert.IsTrue(((Grid)panel.FindName("CelebrationSurface")).ClipToBounds);
        model.SelectSettingsSectionCommand.Execute("folders");
        await WpfTestHost.LayoutAsync(window);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.IsGreaterThan(0, overlay.Children.Count);
        window.Close();
        Assert.AreEqual(0, overlay.Children.Count);
    });

    private static void SaveReview(Window window, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("SIMS4_SETTINGS_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, filename));
        encoder.Save(stream);
    }

    private static void PressSpace(ButtonBase button)
    {
        button.BringIntoView();
        button.Focus();
        Assert.IsTrue(button.IsKeyboardFocused, button.Name);
        var source = PresentationSource.FromVisual(button)!;
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    private static void AssertHitRectangle(FrameworkElement control)
    {
        foreach (var x in new[] { 0.01, 0.5, 0.99 })
        foreach (var y in new[] { 0.01, 0.5, 0.99 })
        {
            var hit = control.InputHitTest(new Point(control.ActualWidth * x, control.ActualHeight * y)) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, control)) hit = VisualTreeHelper.GetParent(hit);
            Assert.AreSame(control, hit, control.Name);
        }
    }
}
