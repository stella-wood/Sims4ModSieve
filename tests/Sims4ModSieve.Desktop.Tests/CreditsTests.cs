using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
    public Task IconsLandStayStillAndFadeAfterThreeSecondsIdle() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var layer = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        for (var click = 1; click <= 5; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var icon = layer.Particles.Single(particle => particle.Tag == "Clawd");
        var landedTime = icon.Birth + 7;
        panel.AdvanceAnimation(landedTime);
        Assert.IsNotNull(icon.LandedAt);
        var impact = icon.Transform.Matrix;
        panel.AdvanceAnimation(landedTime + 0.1);
        Assert.AreNotEqual(impact, icon.Transform.Matrix, "触底后应缓动回正。");
        Assert.AreNotEqual(0, icon.Transform.Matrix.M12, "不能触底就突然跳到正立姿态。");
        panel.AdvanceAnimation(landedTime + 0.8);
        Assert.AreEqual(0, icon.Transform.Matrix.M12, "图标落地后应回正。");
        Assert.AreEqual(0, icon.Transform.Matrix.M21, "图标落地后应回正。");
        Assert.IsGreaterThan(layer.ActualHeight / 2, icon.Position.Y);
        var resting = icon.Transform.Matrix;
        panel.AdvanceAnimation(landedTime + 2.9);
        Assert.AreEqual(resting, icon.Transform.Matrix, "落地后不再位移或旋转。");
        Assert.AreEqual(1, icon.Opacity);
        panel.AdvanceAnimation(landedTime + 3.5);
        Assert.IsTrue(icon.Opacity > 0 && icon.Opacity < 1, "停点满三秒后应渐隐。");
        panel.AdvanceAnimation(landedTime + 4.1);
        Assert.IsFalse(layer.Particles.Contains(icon));
    });

    [TestMethod]
    public Task ContinuedClicksExtendLandedIconsAndMessageRemovesThemTogether() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var layer = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        var message = (Border)panel.FindName("SecretMessage");
        for (var click = 1; click <= 12; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var icons = layer.Particles.Where(particle => particle.IsSurprise).ToArray();
        Assert.AreEqual(4, icons.Length);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (icons.Any(icon => icon.LandedAt is null) && timeout.Elapsed.TotalSeconds < 8) await Task.Delay(100);
        Assert.IsTrue(icons.All(icon => icon.LandedAt is not null));
        await Task.Delay(2100);
        Assert.IsTrue(icons.All(icon => icon.Transform.Matrix.M12 == 0 && icon.Transform.Matrix.M21 == 0), "四个图标都应正立停留。");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(1200);
        Assert.IsTrue(icons.All(icon => layer.Particles.Contains(icon) && icon.Opacity == 1), "持续点击应延长落地图标的停留。");
        await Task.Delay(2050);
        Assert.IsTrue(icons.All(icon => icon.Opacity > 0 && icon.Opacity < 1));
        for (var click = 14; click <= 20; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var messageTime = layer.Particles.Last().Birth;
        panel.AdvanceAnimation(messageTime + 0.6);
        Assert.IsTrue(message.IsVisible);
        Assert.IsTrue(icons.All(icon => icon.Opacity == 1 && icon.FadeStartedAt is null), "继续点击应恢复尚未消失的图标。");
        panel.AdvanceAnimation(messageTime + 5.2);
        Assert.IsTrue(message.Opacity > 0 && message.Opacity < 1);
        foreach (var icon in icons) Assert.AreEqual(message.Opacity, icon.Opacity, 0.001);
        panel.AdvanceAnimation(messageTime + 6.1);
        Assert.IsFalse(message.IsVisible);
        Assert.IsFalse(layer.Particles.Any(particle => particle.IsSurprise));
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, layer.Particles.Count);
    });

    [TestMethod]
    public Task EffectsClipAtSameFloorWhereIconsStandAfterResize() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var layer = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        for (var click = 1; click <= 5; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var icon = layer.Particles.Single(particle => particle.IsSurprise);
        panel.AdvanceAnimation(icon.Birth + 7);
        panel.AdvanceAnimation(icon.Birth + 8);
        void AssertSharedFloor()
        {
            var iconBottom = icon.Transform.TransformBounds(new Rect(-0.5, -0.5, 1, 1)).Bottom;
            Assert.AreEqual(iconBottom, layer.FloorClip.Bounds.Bottom, 0.001, "图标脚下和礼花裁剪应共用同一条底线。");
            Assert.IsFalse(layer.FloorClip.FillContains(new Point(layer.ActualWidth / 2, iconBottom + 1)));
        }
        AssertSharedFloor();
        panel.EmitHeart(new Point(layer.ActualWidth / 2, layer.ActualHeight - 1));
        var heart = layer.Particles.Single(particle => particle.IsHeart);
        panel.AdvanceAnimation(heart.Birth + 0.01);
        Assert.IsTrue(layer.Particles.Contains(heart), "礼花底线不应阻止页面最下方点击产生的爱心。");
        window.Height = 640;
        await WpfTestHost.LayoutAsync(window);
        panel.AdvanceAnimation(icon.Birth + 8.5);
        AssertSharedFloor();
    });

    [TestMethod]
    public Task AfdianLinkOpensProvidedAddressAndReportsLaunchFailure() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var opened = new List<string>();
        var fail = false;
        var support = new SettingsSupportService(files.Root, openTarget: target =>
        {
            if (fail) throw new IOException();
            opened.Add(target);
        });
        var model = files.Model(support: support);
        var window = new MainWindow(model) { Width = 920, Height = 640 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var link = (Hyperlink)panel.FindName("AfdianLink");
        Assert.IsTrue(link.IsEnabled);
        Assert.AreSame(model.OpenAfdianCommand, link.Command);
        ((IInvokeProvider)new HyperlinkAutomationPeer(link).GetPattern(PatternInterface.Invoke)).Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        CollectionAssert.AreEqual(new[] { "https://ifdian.net/a/stella063" }, opened);
        fail = true;
        ((IInvokeProvider)new HyperlinkAutomationPeer(link).GetPattern(PatternInterface.Invoke)).Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.AreEqual("无法打开爱发电页面", model.CreditsFeedback);
        var intro = (TextBlock)panel.FindName("SecretIntro");
        Assert.AreEqual("感谢每一位开发者、\n贡献者、请我吃过薯片的朋友们\n以及亲爱的agent们", intro.Text);
        Assert.AreEqual("谢谢你还在使用它", ((TextBlock)panel.FindName("SecretThanks")).Text);
    });

    [TestMethod]
    public Task ConsecutiveBurstsIncreaseLaunchSpeedUpToThirdAndResetAfterRest() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var layer = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        double Launch()
        {
            var previous = layer.Particles.Count;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return layer.Particles.Skip(previous).Where(particle => particle.IsPaper).Max(particle => -particle.VelocityY);
        }
        var first = Launch();
        var firstParticle = layer.Particles.First();
        var second = Launch();
        var third = Launch();
        var fourth = Launch();
        Assert.IsGreaterThan(first, second);
        Assert.IsGreaterThan(second, third);
        Assert.AreEqual(third, fourth, 0.000001, "第三档之后不能继续增强。");
        Assert.IsTrue(layer.Particles.Contains(firstParticle));
        await Task.Delay(2100);
        Assert.AreEqual(first, Launch(), 0.000001, "停顿后应从第一档重新开始。");
        Assert.IsTrue(layer.Particles.Any(particle => particle.Tag == "Clawd"), "力度重置不能清零彩蛋累计次数。");
        if (Environment.GetEnvironmentVariable("SIMS4_CREDITS_ANIMATION_REVIEW") == "1")
        {
            model.SelectSettingsSectionCommand.Execute("about");
            await WpfTestHost.LayoutAsync(window);
            model.SelectSettingsSectionCommand.Execute("credits");
            await WpfTestHost.LayoutAsync(window);
            for (var frame = 0; frame < 68; frame++)
            {
                if (frame is 0 or 3 or 6) PressSpace(button);
                SaveReview(window, $"boosted-{frame:D2}.png");
                await Task.Delay(100);
            }
        }
    });

    [TestMethod]
    public Task PetalsStayUprightWhileRibbonsKeepFlipping() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var layer = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        PressSpace(button);
        var petals = layer.Particles.Where(particle => particle.Tag == "Petal").ToArray();
        var ribbon = layer.Particles.First(particle => particle.IsRibbon);
        var sawFront = false;
        var sawBack = false;
        for (var frame = 0; frame < 40; frame++)
        {
            await Task.Delay(50);
            foreach (var petal in petals)
            {
                Assert.IsGreaterThan(0, petal.ScaleX, "花瓣不能翻面。");
                var angle = Math.Atan2(petal.Transform.Matrix.M12, petal.Transform.Matrix.M11) * 180 / Math.PI;
                Assert.IsLessThan(90, Math.Abs(angle - petal.InitialAngle), "花瓣只应轻摆，不能连续旋转。");
            }
            sawFront |= ribbon.ScaleX > 0;
            sawBack |= ribbon.ScaleX < 0;
        }
        Assert.IsTrue(sawFront && sawBack, "纸条仍然需要翻面。");
    });

    [TestMethod]
    public Task ConfettiButtonCompressesReboundsAndSettlesWithoutChangingHitArea() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var face = (FrameworkElement)button.Template.FindName("FloatingFace", button);
        var scale = (ScaleTransform)face.RenderTransform;
        var initial = scale.ScaleX;
        var recording = Environment.GetEnvironmentVariable("SIMS4_CREDITS_ANIMATION_REVIEW") == "1";
        if (recording)
        {
            var bounds = button.TransformToAncestor((FrameworkElement)window.Content).TransformBounds(new Rect(button.RenderSize));
            var directory = Environment.GetEnvironmentVariable("SIMS4_SETTINGS_REVIEW_DIR")!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "button-bounds.json"), JsonSerializer.Serialize(new { bounds.X, bounds.Y, bounds.Width, bounds.Height }));
        }
        button.Focus();
        var source = PresentationSource.FromVisual(button)!;
        void Key(RoutedEvent routed) => button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Space) { RoutedEvent = routed });
        Key(Keyboard.KeyDownEvent);
        if (recording)
            for (var frame = 0; frame < 4; frame++)
            {
                SaveReview(window, $"button-{frame:D2}.png");
                await Task.Delay(20);
            }
        else await Task.Delay(80);
        Assert.IsLessThan(initial, scale.ScaleX, "按下时先缩小。");
        AssertHitRectangle(button);
        Key(Keyboard.KeyUpEvent);
        var expanded = false;
        for (var frame = 0; frame < 25; frame++)
        {
            await Task.Delay(20);
            expanded |= scale.ScaleX > initial;
            AssertHitRectangle(button);
            if (recording) SaveReview(window, $"button-{frame + 4:D2}.png");
        }
        Assert.IsTrue(expanded, "松开后应放大回弹。");
        Assert.AreEqual(initial, scale.ScaleX, 0.000001, "回弹结束应回到原始大小。");
        PressSpace(button);
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(initial, ((ScaleTransform)face.RenderTransform).ScaleX, 0.000001);
    });

    [TestMethod]
    public Task BurstUpdatesRenderTransformsWithoutRelayout() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var overlay = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        for (var click = 0; click < 25; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(200);
        var layouts = 0;
        var frames = 0;
        var intervals = new List<double>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var lastFrame = timer.Elapsed.TotalMilliseconds;
        void Layout(object? sender, EventArgs e) => layouts++;
        void Frame(object? sender, EventArgs e)
        {
            var now = timer.Elapsed.TotalMilliseconds;
            intervals.Add(now - lastFrame);
            lastFrame = now;
            frames++;
        }
        overlay.LayoutUpdated += Layout;
        CompositionTarget.Rendering += Frame;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try { await Task.Delay(1200); }
        finally
        {
            overlay.LayoutUpdated -= Layout;
            CompositionTarget.Rendering -= Frame;
        }
        intervals.Sort();
        Assert.IsGreaterThan(0, frames);
        Console.WriteLine($"Particle animation: frames={frames}, layouts={layouts}, UI allocations={GC.GetAllocatedBytesForCurrentThread() - allocated}, frame p95={intervals[(int)(intervals.Count * 0.95)]:F1} ms");
        Assert.IsLessThan(frames / 2, layouts, "粒子位移不应在每一帧重排页面布局。");
    });

    [TestMethod]
    public void TwentyFifthClickPausesTwoSecondsWithoutResettingMilestones()
    {
        var state = new CreditsInteractionState();
        var surprises = new List<CreditsSurprise>();
        for (var click = 1; click <= 25; click++)
        {
            Assert.IsTrue(state.TryClickConfetti(0, out var surprise));
            if (surprise != CreditsSurprise.None) surprises.Add(surprise);
        }
        CollectionAssert.AreEqual(new[] { CreditsSurprise.Clawd, CreditsSurprise.Star, CreditsSurprise.ChatGpt,
            CreditsSurprise.DeepSeek, CreditsSurprise.Message }, surprises);
        Assert.IsFalse(state.CanClick(1.999));
        Assert.IsFalse(state.TryClickConfetti(1.999, out _));
        Assert.IsTrue(state.TryClickConfetti(2, out var resumed));
        Assert.AreEqual(CreditsSurprise.None, resumed);
        Assert.AreEqual(new CreditsInteractionState().BurstStrength, state.BurstStrength, "暂停恢复后应从第一档重新开始。");
        state.Reset();
        Assert.IsTrue(state.CanClick(0));
    }

    [TestMethod]
    public Task BurstsAccumulateUntilTwentyFiveAndResumeWithoutClearingParticles() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        var recording = Environment.GetEnvironmentVariable("SIMS4_CREDITS_ANIMATION_REVIEW") == "1";
        if (recording)
        {
            window.PreviewMouseDown += (_, e) => { if (e.Timestamp != 0) e.Handled = true; };
        }
        WpfTestHost.ShowOffScreen(window);
        if (recording) WpfTestHost.PlaceOnScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var layer = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        var message = (Border)panel.FindName("SecretMessage");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var first = layer.Particles.First();
        for (var click = 2; click <= 25; click++)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual(click * 70, layer.Particles.Count(particle => particle.IsPaper), "每次都必须追加完整一轮。");
            Assert.IsTrue(layer.Particles.Contains(first), "新喷射不能清掉已经喷出的粒子。");
            if (click == 20) Assert.IsTrue(message.IsVisible);
        }
        Assert.IsFalse(button.IsEnabled);
        Assert.IsNull(panel.FindName("ConfettiClickCount"));
        var pausedCount = layer.Particles.Count;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.AreEqual(pausedCount, layer.Particles.Count);
        var initial = first.Position;
        await Task.Delay(150);
        Assert.AreNotEqual(initial, first.Position, "暂停按钮不能暂停已飞出的粒子。");
        SaveReview(window, "twenty-five-paused.png");
        await Task.Delay(2000);
        Assert.IsTrue(button.IsEnabled);
        var beforeResume = layer.Particles.Count(particle => particle.IsPaper);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.AreEqual(beforeResume + 70, layer.Particles.Count(particle => particle.IsPaper));
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, layer.Particles.Count);
        Assert.IsTrue(button.IsEnabled);
        if (recording)
        {
            model.SelectSettingsSectionCommand.Execute("credits");
            await WpfTestHost.LayoutAsync(window);
            CreditsPanel.Particle? firstBurst = null;
            for (var frame = 0; frame < 68; frame++)
            {
                if (frame == 0)
                {
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    firstBurst = layer.Particles.First();
                }
                if (frame == 8)
                {
                    Assert.IsTrue(layer.Particles.Contains(firstBurst!));
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.IsTrue(layer.Particles.Contains(firstBurst!));
                }
                SaveReview(window, $"retained-{frame:D2}.png");
                await Task.Delay(100);
            }
        }
    });

    [TestMethod]
    public Task WholePageClicksCreateHeartsExceptFloatingConfettiButton() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var panel = Descendants<CreditsPanel>(window).Single();
        var nav = (RadioButton)page.FindName("CreditsSectionButton");
        var sidebar = (FrameworkElement)((FrameworkElement)nav.Parent).Parent;
        var overlay = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        var rightPane = new Rect(sidebar.ActualWidth, 0, page.ActualWidth - sidebar.ActualWidth, page.ActualHeight);
        foreach (var point in new[] { new Point(rightPane.Left + 2, 2), new Point(rightPane.Right - 2, rightPane.Height / 2),
            new Point(rightPane.Left + 2, rightPane.Bottom - 2) })
        {
            var hit = page.InputHitTest(point) as DependencyObject;
            Assert.IsNotNull(hit);
            Assert.IsTrue(ReferenceEquals(hit, panel) || panel.IsAncestorOf(hit), "页面留白也应属于爱心点击范围。");
            ((UIElement)hit).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
        }
        Assert.AreEqual(3, overlay.Particles.Count);
        var button = (Button)panel.FindName("ConfettiButton");
        AssertHitRectangle(button);
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        PressSpace(button);
        Assert.AreEqual(3, overlay.Particles.Count(item => Equals(item.Tag, "Heart")));
        Assert.IsNull(panel.FindName("ConfettiClickCount"));
        model.GoHomeCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, overlay.Particles.Count);
    });

    [TestMethod]
    public void ConfettiMilestonesOccurOnceAndResetOnLeaving()
    {
        var state = new CreditsInteractionState();
        var expected = new Dictionary<int, CreditsSurprise>
        {
            [5] = CreditsSurprise.Clawd, [6] = CreditsSurprise.Star,
            [8] = CreditsSurprise.ChatGpt, [12] = CreditsSurprise.DeepSeek, [20] = CreditsSurprise.Message
        };
        for (var click = 1; click <= 45; click++)
        {
            Assert.IsTrue(state.TryClickConfetti((click - 1) / 25 * 2, out var surprise));
            Assert.AreEqual(expected.GetValueOrDefault(click), surprise, $"第 {click} 次点击");
        }
        state.Reset();
        for (var click = 1; click <= 20; click++)
        {
            Assert.IsTrue(state.TryClickConfetti(0, out var surprise));
            Assert.AreEqual(expected.GetValueOrDefault(click), surprise, $"重置后第 {click} 次点击");
        }
    }

    [TestMethod]
    public Task HeartsStayAtFourAndOldestFadesBeforeReplacement() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        var recording = Environment.GetEnvironmentVariable("SIMS4_CREDITS_ANIMATION_REVIEW") == "1";
        if (recording)
        {
            // 录制只回放脚本点击，避免桌面上的真实鼠标点击混入。
            window.PreviewMouseDown += (_, e) => { if (e.Timestamp != 0) e.Handled = true; };
        }
        WpfTestHost.ShowOffScreen(window);
        if (recording) WpfTestHost.PlaceOnScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var surface = (Grid)panel.FindName("CelebrationSurface");
        var overlay = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        void Click() => panel.EmitHeart(new Point(surface.ActualWidth * 0.48, surface.ActualHeight * 0.55));
        for (var click = 1; click <= 4; click++)
        {
            Click();
            Assert.AreEqual(click, overlay.Particles.Count, "每次点击只生成一颗爱心。");
        }
        var oldest = overlay.Particles[0];
        var initialOpacity = oldest.Opacity;
        Click();
        Assert.AreEqual(4, overlay.Particles.Count);
        await Task.Delay(100);
        Assert.IsTrue(overlay.Particles.Contains(oldest));
        Assert.IsLessThan(initialOpacity, oldest.Opacity);
        await Task.Delay(160);
        Assert.IsFalse(overlay.Particles.Contains(oldest));
        Assert.AreEqual(4, overlay.Particles.Count);
        for (var click = 0; click < 30; click++)
        {
            Click();
            Assert.IsTrue(overlay.Particles.Count <= 4);
        }
        await Task.Delay(1900);
        Assert.AreEqual(0, overlay.Particles.Count, "替换中的爱心也应在本轮结束后清理。");
        if (recording)
        {
            for (var frame = 0; frame < 34; frame++)
            {
                if (frame <= 14 && frame % 2 == 0) Click();
                Assert.IsTrue(overlay.Particles.Count <= 4);
                SaveReview(window, $"hearts-{frame:D2}.png");
                await Task.Delay(100);
            }
            Assert.AreEqual(0, overlay.Particles.Count);
        }
        Click();
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, overlay.Particles.Count);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        Click();
        Assert.AreEqual(1, overlay.Particles.Count);
    });

    [TestMethod]
    public Task ConfettiButtonStaysFixedWhenNamesGrowScrollAndWindowResizes() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        double? bottomInset = null;
        foreach (var size in new[] { new Size(1180, 780), new Size(920, 640) })
        {
            Rect? emptyBounds = null;
            foreach (var count in new[] { 0, 300 })
            {
                var json = JsonSerializer.Serialize(Enumerable.Range(1, count).Select(index => $"测试支持者 {index}"));
                var support = new SettingsSupportService(files.Root, openSupporters: () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
                var model = files.Model(support: support);
                var window = new MainWindow(model) { Width = size.Width, Height = size.Height };
                WpfTestHost.ShowOffScreen(window);
                model.OpenSettingsCommand.Execute(null);
                model.SelectSettingsSectionCommand.Execute("credits");
                await WpfTestHost.LayoutAsync(window);
                var page = Descendants<SettingsPage>(window).Single();
                var panel = Descendants<CreditsPanel>(window).Single();
                var button = (Button)panel.FindName("ConfettiButton");
                var scroll = (ScrollViewer)panel.FindName("SupportersScroll");
                Rect Bounds() => button.TransformToAncestor(page).TransformBounds(new Rect(button.RenderSize));
                var bounds = Bounds();
                bottomInset ??= page.ActualHeight - bounds.Bottom;
                Assert.AreEqual(bottomInset.Value, page.ActualHeight - bounds.Bottom, 1, "按钮应固定在页面底部。");
                Assert.AreEqual(panel.ActualWidth / 2, button.TranslatePoint(new Point(button.ActualWidth / 2, 0), panel).X, 1);
                if (count == 0) emptyBounds = bounds;
                else
                {
                    Assert.AreEqual(emptyBounds!.Value, bounds, "名单变长不能改变按钮位置。");
                    Assert.IsGreaterThan(0, scroll.ScrollableHeight);
                    scroll.ScrollToEnd();
                    await WpfTestHost.LayoutAsync(window);
                    Assert.IsGreaterThan(0, scroll.VerticalOffset);
                    Assert.AreEqual(bounds, Bounds(), "滚动名单不能带动礼花筒。");
                }
                AssertHitRectangle(button);
                window.Close();
            }
        }
    });

    [TestMethod]
    public Task BurstFollowsReferenceMotionWithinPageAndFallsNaturally() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var overlay = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        PressSpace(button);
        Assert.AreEqual(55, overlay.Particles.Count(item => Equals(item.Tag, "Ribbon")));
        Assert.AreEqual(15, overlay.Particles.Count(item => Equals(item.Tag, "Petal")));
        var ribbon = overlay.Particles.First(item => Equals(item.Tag, "Ribbon"));
        var sawFront = false;
        var sawBack = false;
        for (var frame = 0; frame < 68; frame++)
        {
            if (Environment.GetEnvironmentVariable("SIMS4_CREDITS_ANIMATION_REVIEW") == "1")
                SaveReview(window, $"confetti-{frame:D2}.png");
            if (frame < 17)
            {
                sawFront |= ribbon.ScaleX > 0;
                sawBack |= ribbon.ScaleX < 0;
            }
            if (frame == 16)
            {
                var pieces = overlay.Particles.ToArray();
                var positions = pieces.Select(piece => ParticleCenter(piece, overlay)).ToArray();
                var spread = positions.Max(point => point.X) - positions.Min(point => point.X);
                Assert.IsGreaterThan(overlay.ActualWidth * 0.8, spread, "爆发后应铺开到板块两侧。");
                Assert.IsLessThan(overlay.ActualHeight * 0.4, positions.Min(point => point.Y), "第一档也应喷到页面上半部。");
                Assert.IsTrue(positions.Any(point => point.Y > overlay.ActualHeight * 0.35 && point.Y < overlay.ActualHeight * 0.6),
                    "同一时刻也要有中景粒子，不能都挤在一条弧线上。");
                Assert.IsTrue(sawFront && sawBack, "彩带应边转边翻面。");
            }
            await Task.Delay(100);
        }
        Assert.AreEqual(0, overlay.Particles.Count);
    });

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
        WpfTestHost.ShowOffScreen(window);
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
            WpfTestHost.ShowOffScreen(window);
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
        WpfTestHost.ShowOffScreen(window);
        if (nativeInput) WpfTestHost.PlaceOnScreen(window);
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
        var overlay = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        var secret = (Border)panel.FindName("SecretMessage");
        Assert.IsFalse(overlay.IsHitTestVisible);
        AssertHitRectangle(button);
        if (nativeInput) await WpfTestHost.ClickAsync(button, new Point(3, button.ActualHeight / 2));
        else
        {
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            PressSpace(button);
        }
        Assert.IsFalse(overlay.Particles.Any(item => Equals(item.Tag, "Heart")), "礼花按钮不能同时生成爱心。");
        Assert.IsTrue(overlay.Particles.Any(item => Equals(item.Tag, "Ribbon") || Equals(item.Tag, "Petal")));
        panel.EmitHeart(new Point(overlay.ActualWidth / 2, overlay.ActualHeight / 2));
        await WpfTestHost.LayoutAsync(window);
        var heart = overlay.Particles.First(item => Equals(item.Tag, "Heart"));
        var initial = ParticleCenter(heart, overlay);
        await Task.Delay(120);
        Assert.IsGreaterThan(initial.X, ParticleCenter(heart, overlay).X);
        Assert.IsLessThan(initial.Y, ParticleCenter(heart, overlay).Y);
        await Task.Delay(6000);
        Assert.AreEqual(0, overlay.Particles.Count, "动画结束必须移除元素。");
        var surprises = new Dictionary<int, string> { [5] = "Clawd", [6] = "Star", [8] = "ChatGpt", [12] = "DeepSeek" };
        for (var click = 2; click <= 12; click++)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (surprises.TryGetValue(click, out var tag))
                Assert.IsTrue(overlay.Particles.Any(item => Equals(item.Tag, tag)), tag);
        }
        foreach (var tag in surprises.Values)
            Assert.IsTrue(overlay.Particles.Any(item => Equals(item.Tag, tag)), "连续喷射仍需保留彩蛋：" + tag);
        await Task.Delay(350);
        var iconBounds = overlay.Particles.Where(item => surprises.Values.Contains(item.Tag as string))
            .Select(item => item.Transform.TransformBounds(new Rect(-0.5, -0.5, 1, 1))).OrderBy(bounds => bounds.Left).ToArray();
        for (var index = 1; index < iconBounds.Length; index++)
            Assert.IsFalse(iconBounds[index - 1].IntersectsWith(iconBounds[index]), "快速连点的图标不能互相遮住。");
        SaveReview(window, "credits-confetti.png");
        if (Environment.GetEnvironmentVariable("SIMS4_CREDITS_ANIMATION_REVIEW") == "1")
            for (var frame = 0; frame < 24; frame++)
            {
                SaveReview(window, $"easter-eggs-{frame:D2}.png");
                await Task.Delay(100);
            }
        for (var click = 13; click <= 20; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.IsTrue(secret.IsVisible);
        await Task.Delay(650);
        Assert.IsTrue(secret.Opacity > 0);
        SaveReview(window, "credits-secret.png");
        await Task.Delay(5600);
        Assert.IsFalse(secret.IsVisible);
        for (var click = 21; click <= 45; click++) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.IsFalse(secret.IsVisible, "第 20 次之后不重复出现文字。");
        Assert.IsTrue(overlay.Particles.Count(particle => particle.IsHeart) <= CreditsPanel.MaximumHearts);
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, overlay.Particles.Count);
        Assert.IsNull(panel.FindName("ConfettiClickCount"));
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        for (var click = 1; click <= 5; click++)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        Assert.IsTrue(overlay.Particles.Any(item => Equals(item.Tag, "Clawd")));
        model.GoHomeCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, overlay.Particles.Count);
    });

    [TestMethod]
    public Task ConsecutivePauseAndAccessibleConfettiRemainUsable() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var window = new MainWindow(model) { Width = 920, Height = 640 };
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        var panel = Descendants<CreditsPanel>(window).Single();
        var button = (Button)panel.FindName("ConfettiButton");
        var overlay = (CreditsEffectsLayer)panel.FindName("EffectsOverlay");
        for (var index = 0; index < 100; index++)
        {
            ((Grid)panel.FindName("CelebrationSurface")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        Assert.AreEqual(CreditsPanel.MaximumHearts, overlay.Particles.Count(item => Equals(item.Tag, "Heart")));
        Assert.AreEqual(25 * 70, overlay.Particles.Count(particle => particle.IsPaper));
        Assert.IsFalse(button.IsEnabled);
        Assert.IsTrue(((Grid)panel.FindName("CelebrationSurface")).ClipToBounds);
        model.SelectSettingsSectionCommand.Execute("folders");
        await WpfTestHost.LayoutAsync(window);
        model.SelectSettingsSectionCommand.Execute("credits");
        await WpfTestHost.LayoutAsync(window);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.IsGreaterThan(0, overlay.Particles.Count);
        window.Close();
        Assert.AreEqual(0, overlay.Particles.Count);
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

    private static Point ParticleCenter(CreditsPanel.Particle particle, CreditsEffectsLayer overlay) => particle.Position;

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
