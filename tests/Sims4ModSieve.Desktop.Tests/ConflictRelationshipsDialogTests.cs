using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictRelationshipsDialogTests
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    internal static ConflictResultSession ReviewSession(int partners = 4)
    {
        const string bundle = @"C:\Mods\整合\人物合集.package";
        const string copy = @"D:\Inbox\自然睫毛_副本.package";
        var related = Enumerable.Range(0, partners).Select(index => ($@"C:\Mods\关联文件-{index}.package", (string?)"other")).ToArray();
        return V2([
            Resource(1, new[] { (A, (string?)"same"), (copy, "same") }.Concat(related).Append((bundle, "same")).ToArray()),
            Resource(2, (A, "two"), (copy, "two"), (bundle, "two")),
            Resource(3, (A, "three"), (copy, "three"), (bundle, "three"))],
            new[] { (A, 3), (copy, 3), (bundle, 40) }.Concat(related.Select(file => (file.Item1, 10))).ToArray(), Duplicates(A, copy));
    }

    [TestMethod]
    public Task GroupHeadingAndFileTextAreasRouteToTheirActions() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        window.Left = 70;
        window.Top = 50;
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
        {
            window.Width = width;
            window.Height = height;
            var session = ReviewSession();
            var card = session.Cards.First(card => card.Type == "override" && card.Files.Any(file => file.Path == A));
            await SetSessionAsync(model, session, "all");
            var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
            list.ScrollIntoView(model.ConflictRows.Single(row => row.Key == card.Key));
            await WpfTestHost.LayoutAsync(window);
            var heading = Descendants<Button>(list).Single(button => button.Name == "ExpandButton" && button.DataContext is ConflictResultRow row && row.Key == card.Key);
            foreach (var target in Descendants<TextBlock>(heading).Cast<FrameworkElement>().Concat(Descendants<System.Windows.Shapes.Ellipse>(heading)))
                Assert.AreSame(heading, HitButton(window, target, new Point(target.ActualWidth / 2, target.ActualHeight / 2)));
            foreach (var fraction in new[] { 0.05, 0.95 })
            {
                heading = Descendants<Button>(list).Single(button => button.Name == "ExpandButton" && button.DataContext is ConflictResultRow row && row.Key == card.Key);
                var hit = HitButton(window, heading, new Point(heading.ActualWidth * fraction, heading.ActualHeight * fraction));
                Assert.AreSame(heading, hit);
                Invoke(hit);
                await WpfTestHost.LayoutAsync(window);
                await model.ConflictRowsRefreshTask;
                await WpfTestHost.LayoutAsync(window);
                var expanded = model.ConflictRows.Single(row => row.Key == card.Key).IsExpanded;
                Assert.AreEqual(fraction < 0.5, expanded);
            }
            model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.Key == card.Key));
            await model.ConflictRowsRefreshTask;
            var file = model.ConflictRows.First(row => row.OwnerKey == card.Key && row.File?.Path.Contains("关联文件-0", StringComparison.Ordinal) == true);
            list.ScrollIntoView(file);
            await WpfTestHost.LayoutAsync(window);
            var details = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, file) && button.Name == "FileDetailsButton");
            var surface = Descendants<Border>(list).Single(border => ReferenceEquals(border.DataContext, file) && border.Name == "FileSurface");
            Save(window, $"v2-file-hit-areas-{width}x{height}.png");
            var targets = Descendants<TextBlock>(surface).Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)).ToArray();
            Assert.IsTrue(targets.Any(text => text.Text == file.Title));
            Assert.IsTrue(targets.Any(text => text.Text == file.Annotation));
            Assert.IsTrue(targets.Any(text => text.Text == file.File!.Path));
            Assert.IsTrue(targets.Any(text => text.Text == file.File!.Metadata));
            var title = targets.Single(text => text.Name == "Title");
            var annotation = targets.Single(text => text.Text == file.Annotation);
            Assert.IsFalse(IsInside(window.InputHitTest(annotation.TranslatePoint(new Point(annotation.ActualWidth / 2, annotation.ActualHeight / 2), window)) as DependencyObject, details));
            foreach (var target in targets.Where(text => text.Text != file.Annotation))
            {
                var hit = HitButton(window, target, new Point(Math.Min(title.ActualWidth, target.ActualWidth) / 2, target.ActualHeight / 2));
                Assert.AreSame(details, hit, target.Text);
                if (ReferenceEquals(target, title))
                {
                    details.Focus();
                    Assert.IsTrue(details.IsKeyboardFocused);
                    var source = PresentationSource.FromVisual(details)!;
                    details.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                    Assert.IsTrue(details.IsPressed);
                    Save(window, $"v2-file-details-pressed-{width}x{height}.png");
                    details.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
                }
                else Invoke(hit);
                await WpfTestHost.LayoutAsync(window);
                var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
                Assert.IsTrue(dialog.IsVisible);
                Assert.AreEqual(file.File!.Path, model.ConflictRelationships!.File.Path);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
                {
                    if (!dialog.IsVisible) closed.TrySetResult();
                }
                dialog.IsVisibleChanged += OnVisibilityChanged;
                try
                {
                    model.CloseConflictRelationshipsCommand.Execute(null);
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally { dialog.IsVisibleChanged -= OnVisibilityChanged; }
            }
            foreach (var x in new[] { 0.05, 0.95 })
                foreach (var y in new[] { 0.05, 0.95 })
                    Assert.AreSame(details, HitButton(window, details, new Point(details.ActualWidth * x, details.ActualHeight * y)));
            foreach (var y in new[] { 0.05, 0.5, 0.95 })
                Assert.IsFalse(IsInside(window.InputHitTest(details.TranslatePoint(new Point(details.ActualWidth + 1, details.ActualHeight * y), window)) as DependencyObject, details));
            var actions = Descendants<Button>(surface).Where(button => button.Command != model.OpenConflictRelationshipsCommand && button.IsVisible).ToArray();
            foreach (var action in actions)
            {
                Assert.AreSame(action, HitButton(window, action, new Point(action.ActualWidth / 2, action.ActualHeight / 2)));
                Assert.IsFalse(IsInside(action, details));
            }
            Save(window, $"v2-expanded-hit-areas-{width}x{height}.png");
            var representative = model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.File?.Path == A);
            list.ScrollIntoView(representative);
            await WpfTestHost.LayoutAsync(window);
            var copies = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, representative) && button.Command == model.JumpToConflictGroupCommand);
            Assert.AreSame(copies, HitButton(window, copies, new Point(copies.ActualWidth / 2, copies.ActualHeight / 2)));
            copies.Focus();
            Assert.IsTrue(copies.IsKeyboardFocused);
            Save(window, $"v2-copy-link-focused-{width}x{height}.png");
            Invoke(copies);
            await WpfTestHost.LayoutAsync(window);
            await model.ConflictRowsRefreshTask;
            await WpfTestHost.LayoutAsync(window);
            Assert.IsNull(model.ConflictRelationships);
            Assert.IsTrue(model.ConflictRows.Single(row => row.Key == representative.DuplicateLink!.Key).IsExpanded);
            var collection = session.Cards.First(card => card.Type == "collection");
            model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.Key == collection.Key));
            await model.ConflictRowsRefreshTask;
            var tagged = model.ConflictRows.First(row => row.OwnerKey == collection.Key && row.HasTag);
            list.ScrollIntoView(tagged);
            await WpfTestHost.LayoutAsync(window);
            var taggedSurface = Descendants<Border>(list).Single(border => ReferenceEquals(border.DataContext, tagged) && border.Name == "FileSurface");
            var taggedButton = Descendants<Button>(taggedSurface).Single(button => button.Name == "FileDetailsButton");
            var tag = Descendants<TextBlock>(taggedSurface).Single(text => text.Text == tagged.Tag);
            Assert.IsFalse(IsInside(window.InputHitTest(tag.TranslatePoint(new Point(tag.ActualWidth / 2, tag.ActualHeight / 2), window)) as DependencyObject, taggedButton));
        }
    });

    private static Button HitButton(Window window, FrameworkElement target, Point point)
    {
        var hit = window.InputHitTest(target.TranslatePoint(point, window)) as DependencyObject;
        while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
        Assert.IsInstanceOfType<Button>(hit, $"未命中按钮：{(target as TextBlock)?.Text ?? target.Name}，窗口坐标 {target.TranslatePoint(point, window)}");
        return (Button)hit;
    }

    private static bool IsInside(DependencyObject? child, DependencyObject ancestor)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    [TestMethod]
    public Task DialogShowsDirectRelationsCopiesAndRestoresFocusAfterEscape() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = ReviewSession();
        var card = session.Cards.First(card => card.Type == "override" && card.Files.Any(file => file.Path == A));
        session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        await WpfTestHost.LayoutAsync(window);
        var file = model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.File?.Path == A);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        list.ScrollIntoView(file);
        await WpfTestHost.LayoutAsync(window);
        var name = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, file) && button.Command == model.OpenConflictRelationshipsCommand);
        name.Focus();
        ((IInvokeProvider)new ButtonAutomationPeer(name).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        await Task.Delay(280);
        var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
        Assert.IsTrue(dialog.IsVisible);
        Assert.IsTrue(model.ConflictRelationships!.HasContainment);
        Assert.AreEqual(5, model.ConflictRelationships.Related.Count);
        var relatedList = Descendants<ListBox>(dialog).Single();
        Assert.AreEqual(5, relatedList.Items.Count);
        var toggle = Descendants<Button>(dialog).Single(button => button.Name == "OwnCopiesToggle");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(relatedList.Items.Cast<ConflictRelationshipDisplayRow>().First().IsCopy);
        Assert.IsNotNull(relatedList.Items.Cast<ConflictRelationshipDisplayRow>().First().CopyLink);
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(relatedList.Items.Cast<ConflictRelationshipDisplayRow>().Any(row => row.IsCopy));
        Save(window, "v2-relationship-copies.png");
        var source = PresentationSource.FromVisual(dialog)!;
        dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Task.Delay(230);
        Assert.IsFalse(dialog.IsVisible);
        Assert.IsNull(model.ConflictRelationships);
        Assert.IsTrue(name.IsKeyboardFocused);
        model.OpenConflictRelationshipsCommand.Execute(file);
        await Task.Delay(280);
        await WpfTestHost.LayoutAsync(window);
        var target = session.Cards.First(card => card.Type == "collection");
        var jump = Descendants<Button>(dialog).First(button => button.IsVisible && button.DataContext is ConflictGroupLink link && link.Key == target.Key);
        jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(230);
        await model.ConflictRowsRefreshTask;
        Assert.IsNull(model.ConflictRelationships);
        Assert.IsTrue(model.ConflictRows.Single(row => row.Key == target.Key).IsExpanded);
        var ordinary = model.ConflictRows.First(row => row.File?.Path.Contains("关联文件-0", StringComparison.Ordinal) == true);
        model.OpenConflictRelationshipsCommand.Execute(ordinary);
        await Task.Delay(280);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(model.ConflictRelationships!.HasContainment);
        Assert.IsFalse(Descendants<Border>(dialog).Single(border => AutomationProperties.GetName(border) == "主文件").IsVisible);
        Save(window, "v2-relations-no-containment.png");
        var backdrop = Descendants<Grid>(dialog).Single(grid => grid.Name == "Backdrop");
        backdrop.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        await Task.Delay(230);
        Assert.IsNull(model.ConflictRelationships);
        Assert.IsFalse(dialog.IsVisible);
    });

    [TestMethod]
    public Task OrdinaryAndMinimumWindowsKeepFourRelationsAndScrollableOverflowAvailable() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        // 本测试打开真实 WPF 窗口，同时保存客户区供人工检查。
        window.Left = 70;
        window.Top = 50;
        var trace = new StringWriter();
        var listener = new TextWriterTraceListener(trace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            model.OpenConflictCommand.Execute(null);
            model.OpenConflictSectionCommand.Execute("basic");
            foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
            {
                window.Width = width;
                window.Height = height;
                var session = ReviewSession(partners: 3);
                var card = session.Cards.First(card => card.Type == "override" && card.Files.Any(file => file.Path == A));
                session.ToggleExpanded(card.Key);
                await SetSessionAsync(model, session, "all");
                model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.File?.Path == A));
                await Task.Delay(280);
                await WpfTestHost.LayoutAsync(window);
                var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
                var list = Descendants<ListBox>(dialog).Single();
                Assert.AreEqual(4, model.ConflictRelationships!.Related.Count);
                foreach (var row in list.Items)
                {
                    var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                    Assert.IsNotNull(container);
                    var bounds = container.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
                    Assert.IsTrue(bounds.Bottom <= list.ActualHeight + 1, "四行关联文件应完整可见");
                }
                Assert.IsTrue(Descendants<Button>(dialog).Single(button => button.Name == "CloseButton").IsVisible);
                Save(window, $"v2-relations-four-{width}x{height}.png");
                model.CloseConflictRelationshipsCommand.Execute(null);
                await Task.Delay(230);
                session = ReviewSession(partners: 30);
                session.ToggleExpanded(session.Cards.First(card => card.Type == "override" && card.Files.Any(file => file.Path == A)).Key);
                await SetSessionAsync(model, session, "all");
                model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.File?.Path == A));
                await Task.Delay(280);
                await WpfTestHost.LayoutAsync(window);
                var scroll = Descendants<ScrollViewer>(list).Single();
                Assert.IsTrue(scroll.ScrollableHeight > 0);
                list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                await WpfTestHost.LayoutAsync(window);
                Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1));
                Save(window, $"v2-relations-scroll-{width}x{height}.png");
                model.CloseConflictRelationshipsCommand.Execute(null);
                await Task.Delay(230);
            }
            Assert.AreEqual("", trace.ToString(), "WPF 绑定不应产生异常");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    });

    [TestMethod]
    public Task MinimumWindowWrapsLongTechnicalNamesAndKeysWithOnlyHorizontalInnerScrolling() => WpfTestHost.RunAsync(async () =>
    {
        var first = @"C:\Mods\很长的目录\" + string.Concat(Enumerable.Repeat("自定义睫毛与五官替换说明", 8)) + ".package";
        var second = @"D:\Inbox\另一个目录\" + string.Concat(Enumerable.Repeat("另一版文件完整名称", 8)) + ".package";
        var session = V2(Enumerable.Range(0, 40).Select(index => Resource((ulong)index + 1, (first, "one"), (second, "two"))).ToArray(),
            [(first, 100), (second, 100)]);
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        session.ToggleExpanded(card.Key + "/technical");
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.Width = 920;
        window.Height = 640;
        await SetSessionAsync(model, session, "all");
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var header = model.ConflictRows.Single(row => row.Kind == ConflictRowKind.TechnicalHeader);
        list.ScrollIntoView(header);
        await WpfTestHost.LayoutAsync(window);
        var names = Descendants<TextBlock>(list).Where(text => text.Text == System.IO.Path.GetFileName(first)
            || text.Text == System.IO.Path.GetFileName(second)).ToArray();
        Assert.IsTrue(names.Length >= 2);
        Assert.IsTrue(names.All(text => text.TextWrapping == TextWrapping.Wrap));
        var scrolls = Descendants<ScrollViewer>(list).ToArray();
        Assert.AreEqual(1, scrolls.Count(scroll => scroll.Name != "TechnicalColumnsScroll"));
        Assert.IsTrue(scrolls.Where(scroll => scroll.Name == "TechnicalColumnsScroll")
            .All(scroll => scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled));
        Assert.IsTrue(scrolls.Single(scroll => scroll.Name != "TechnicalColumnsScroll").ScrollableWidth == 0);
        Save(window, "v2-long-technical-header-920x640.png");
        var last = model.ConflictRows.Last();
        list.ScrollIntoView(last);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromItem(last));
        Save(window, "v2-long-technical-values-920x640.png");
    });

    [TestMethod]
    public Task FileActionsHaveRectangularHitAreasAndTechnicalTableStaysInline() => WpfTestHost.RunAsync(async () =>
    {
        var copied = string.Empty;
        var model = CreateModel(text => copied = text);
        var window = OpenWindow(model);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = ContainmentSession();
        var card = session.Cards.First(card => card.Type == "collection");
        session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        await WpfTestHost.LayoutAsync(window);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var file = model.ConflictRows.First(row => row.OwnerKey == card.Key && row.File is not null);
        list.ScrollIntoView(file);
        await WpfTestHost.LayoutAsync(window);
        var actions = Descendants<Button>(list).Where(button => ReferenceEquals(button.DataContext, file) && button.Style == ((ConflictPage)Descendants<ConflictPage>(window).Single()).Resources["ConflictRowActionStyle"]).ToArray();
        Assert.AreEqual(3, actions.Length);
        foreach (var button in actions)
            foreach (var fraction in new[] { 0.05, 0.5, 0.95 })
            {
                var hit = button.InputHitTest(new Point(button.ActualWidth * fraction, button.ActualHeight * fraction)) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                Assert.AreSame(button, hit);
            }
        var detail = model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.IsTechnicalToggle);
        list.ScrollIntoView(detail);
        await WpfTestHost.LayoutAsync(window);
        var detailButton = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, detail));
        detailButton.Focus();
        Assert.IsTrue(detailButton.IsKeyboardFocused);
        var source = PresentationSource.FromVisual(detailButton)!;
        detailButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        Assert.IsTrue(detailButton.IsPressed);
        Save(window, "v2-technical-toggle-pressed.png");
        detailButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
        await model.ConflictRowsRefreshTask;
        Assert.IsTrue(model.ConflictRows.Any(row => row.OwnerKey == card.Key && row.Kind == ConflictRowKind.TechnicalHeader));
        Assert.IsNull(model.ConflictRelationships);
        var copy = model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.Kind == ConflictRowKind.CopyDetails);
        list.ScrollIntoView(copy);
        await WpfTestHost.LayoutAsync(window);
        var copyButton = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, copy));
        copyButton.Focus();
        Assert.IsTrue(copyButton.IsKeyboardFocused);
        Invoke(copyButton);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(copy.CopyText, copied);
        Save(window, "v2-technical-inline.png");
        model.ToggleConflictExpandedCommand.Execute(detail);
        await model.ConflictRowsRefreshTask;
        Assert.IsFalse(model.ConflictRows.Any(row => row.OwnerKey == card.Key && row.Kind == ConflictRowKind.TechnicalHeader));
    });
    [TestMethod]
    public Task RelationshipBadgesKeepDirectionsWithoutRenderingResourceDetails() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = ReviewSession();
        var card = session.Cards.First(card => card.Type == "override" && card.Files.Any(file => file.Path == A));
        session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.File?.Path == A));
        await Task.Delay(280);
        await WpfTestHost.LayoutAsync(window);
        var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
        var relationships = model.ConflictRelationships!.Related.SelectMany(row => row.Relationships).ToArray();
        Assert.IsTrue(relationships.Any(relationship => relationship.Detail.Length > 0), "底层资源事实应保留");
        var badges = Descendants<Border>(dialog).Where(border => border.Name == "Badge" && border.IsVisible).ToArray();
        Assert.IsTrue(badges.Any(badge => badge.DataContext is ConflictRelationship { IsContainment: true }));
        Assert.IsTrue(badges.Any(badge => badge.DataContext is ConflictRelationship { IsContainment: false }));
        foreach (var badge in badges)
        {
            var relationship = (ConflictRelationship)badge.DataContext;
            Assert.AreEqual(relationship.AccessibleLabel, AutomationProperties.GetName(badge));
            var tooltip = (ToolTip)badge.ToolTip;
            tooltip.PlacementTarget = badge;
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(relationship.Detail, tooltip.Content);
            Assert.AreEqual(relationship.Detail.Length > 0, ToolTipService.GetIsEnabled(badge), "空说明不能打开空白提示");
            Assert.IsTrue(Descendants<TextBlock>(badge).Any(text => text.IsVisible && text.Text.Length > 0));
        }
        Assert.IsFalse(Descendants<TextBlock>(dialog).Any(text => text.IsVisible && text.DataContext is ConflictRelationship relationship
            && relationship.Detail.Length > 0 && text.Text == relationship.Detail));
        var reusedBadge = badges.First(badge => ((ConflictRelationship)badge.DataContext).Detail.Length > 0);
        var original = (ConflictRelationship)reusedBadge.DataContext;
        var reusedTooltip = (ToolTip)reusedBadge.ToolTip;
        var replacement = relationships.First(relationship => relationship.Detail != original.Detail);
        try
        {
            reusedBadge.DataContext = replacement;
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(replacement.Detail, reusedTooltip.Content, "视觉节点复用后提示应读取当前关系，不能保留旧说明");
            Assert.AreEqual(replacement.Detail.Length > 0, ToolTipService.GetIsEnabled(reusedBadge));
        }
        finally { reusedBadge.ClearValue(FrameworkElement.DataContextProperty); }
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(original.Detail, reusedTooltip.Content);
        Save(window, "v2-relations-badges-without-resource-details.png");
    });

    [TestMethod]
    public Task RelationshipDialogKeepsActionsAndVirtualizedScrollingAcrossWindowStates() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = ReviewSession(partners: 200);
        var sourceCard = session.Cards.First(card => card.Type == "override" && card.Files.Any(file => file.Path == A));
        session.ToggleExpanded(sourceCard.Key);
        await SetSessionAsync(model, session, "all");
        foreach (var state in new[] { "normal", "maximized", "restored", "minimum" })
        {
            model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.File?.Path == A));
            await Task.Delay(280);
            window.WindowState = state == "maximized" ? WindowState.Maximized : WindowState.Normal;
            if (state == "minimum") { window.Width = 920; window.Height = 640; }
            await WpfTestHost.LayoutAsync(window);
            var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
            var list = Descendants<ListBox>(dialog).Single();
            var close = Descendants<Button>(dialog).Single(button => button.Name == "CloseButton");
            var footer = Descendants<Button>(dialog).Single(button => Equals(button.Content, "打开所在文件夹"));
            Assert.AreSame(close, HitButton(window, close, new Point(close.ActualWidth / 2, close.ActualHeight / 2)));
            Assert.AreSame(footer, HitButton(window, footer, new Point(footer.ActualWidth / 2, footer.ActualHeight / 2)));
            var scroll = Descendants<ScrollViewer>(list).Single();
            Assert.IsTrue(scroll.ScrollableHeight > 0);
            list.ScrollIntoView(list.Items[list.Items.Count - 1]);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(scroll.VerticalOffset > 0);
            var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
            Assert.IsNotNull(last);
            Assert.IsTrue(Descendants<ListBoxItem>(list).Count() < list.Items.Count, "长列表不能实现所有行");
            var jump = Descendants<Button>(last).First(button => button.IsVisible && button.DataContext is ConflictGroupLink);
            Assert.AreSame(jump, HitButton(window, jump, new Point(jump.ActualWidth / 2, jump.ActualHeight / 2)));
            Save(window, $"v2-relations-responsive-{state}.png");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(230);
            Assert.IsFalse(dialog.IsVisible);
            Assert.IsNull(model.ConflictRelationships);
            model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.File?.Path == A));
            await Task.Delay(280);
            await WpfTestHost.LayoutAsync(window);
            list.ScrollIntoView(list.Items[list.Items.Count - 1]);
            await WpfTestHost.LayoutAsync(window);
            last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
            jump = Descendants<Button>(last).First(button => button.IsVisible && button.DataContext is ConflictGroupLink);
            var target = (ConflictGroupLink)jump.DataContext;
            jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(230);
            await model.ConflictRowsRefreshTask;
            Assert.IsFalse(dialog.IsVisible);
            Assert.IsNull(model.ConflictRelationships);
            Assert.IsTrue(model.ConflictRows.Single(row => row.Key == target.Key).IsExpanded);
        }
    });

    [TestMethod]
    public Task RelationshipBadgeNativeHoverShowsDetailsAndAllowsDialogClose() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        window.Left = 60;
        window.Top = 40;
        window.Topmost = true;
        window.Activate();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = ReviewSession();
        var card = session.Cards.First(card => card.Type == "override" && card.Files.Any(file => file.Path == A));
        session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.File?.Path == A));
        await Task.Delay(280);
        await WpfTestHost.LayoutAsync(window);
        var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
        var badge = Descendants<Border>(dialog).First(border => border.Name == "Badge" && border.IsVisible
            && border.DataContext is ConflictRelationship { Detail.Length: > 0 });
        var tooltip = (ToolTip)badge.ToolTip;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tooltip.Opened += (_, _) => opened.TrySetResult();
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            var close = Descendants<Button>(dialog).Single(button => button.Name == "CloseButton");
            var closeScreen = close.PointToScreen(new Point(close.ActualWidth / 2, close.ActualHeight / 2));
            Assert.IsTrue(SetCursorPos((int)closeScreen.X, (int)closeScreen.Y));
            Mouse.Synchronize();
            await Task.Delay(100);
            var center = new Point(badge.ActualWidth / 2, badge.ActualHeight / 2);
            Assert.IsTrue(IsInside(window.InputHitTest(badge.TranslatePoint(center, window)) as DependencyObject, badge));
            var point = badge.PointToScreen(center);
            Assert.IsTrue(SetCursorPos((int)point.X, (int)point.Y));
            Mouse.Synchronize();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(tooltip.IsOpen);
            Assert.AreEqual(((ConflictRelationship)badge.DataContext).Detail, tooltip.Content);
            Save(window, "v2-relationship-badge-hover.png");
            Save(window, "v2-relationship-badge-tooltip.png", tooltip);
            Assert.IsTrue(SetCursorPos((int)closeScreen.X, (int)closeScreen.Y));
            Mouse.Synchronize();
            await Task.Delay(100);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(280);
            Assert.IsNull(model.ConflictRelationships);
            Assert.IsFalse(dialog.IsVisible);
            Assert.IsFalse(tooltip.IsOpen);
        }
        finally { SetCursorPos(previous.X, previous.Y); }
    });

}
