using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ResultGroupMotionTests
{
    [TestMethod]
    public Task LongGroupReachesViewportEdgeBeforeFinishingOutsideIt() => WpfTestHost.RunAsync(() =>
    {
        const double fullHeight = 900;
        const double visibleHeight = 150;
        var expansion = (AnimationClock)ResultListViewport.CreateHeightAnimation(0, fullHeight, true, visibleHeight).CreateClock(true);
        Assert.IsTrue(HeightAt(expansion, 0, fullHeight, 80) is > 0 and < visibleHeight);
        Assert.AreEqual(visibleHeight, HeightAt(expansion, 0, fullHeight, 135), 0.001);
        Assert.IsTrue(HeightAt(expansion, 0, fullHeight, 160) is > visibleHeight and < fullHeight);
        Assert.AreEqual(fullHeight, HeightAt(expansion, 0, fullHeight, 190), 0.001);

        var smallGroup = (AnimationClock)ResultListViewport.CreateHeightAnimation(0, visibleHeight, true, 0).CreateClock(true);
        Assert.AreEqual(visibleHeight * 0.875, HeightAt(smallGroup, 0, visibleHeight, 95), 0.001, "短组沿用原展开曲线");
        var collapse = (AnimationClock)ResultListViewport.CreateHeightAnimation(fullHeight, 0, false, visibleHeight).CreateClock(true);
        Assert.AreEqual(fullHeight * 0.875, HeightAt(collapse, fullHeight, 0, 70), 0.001, "收起沿用原曲线");
        return Task.CompletedTask;

        static double HeightAt(AnimationClock clock, double from, double to, double milliseconds)
        {
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime);
            return (double)clock.GetCurrentValue(from, to);
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task LongGroupsAnimateWhileTheirHeadersStayInPlace(bool conflict) => WpfTestHost.RunAsync(async () =>
    {
        var report = Duplicates(@"C:\Mods\a.package", @"C:\Mods\b.package");
        report = report with
        {
            Groups = Enumerable.Range(0, 32).Select(group => Duplicates(Enumerable.Range(0, 24)
                .Select(file => $@"C:\Mods\group{group:D2}\file{file:D2}.package").ToArray()).Groups.Single() with { Sha256 = $"HASH{group:D2}" }).ToArray(),
        };
        using var temp = new TempDirectory();
        var duplicateSession = new DuplicateResultSession();
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FolderPickerService(), new DuplicateSettingsStore(temp.Path), resultSession: duplicateSession);
        if (conflict)
        {
            model.OpenConflictCommand.Execute(null);
            model.OpenConflictSectionCommand.Execute("basic");
            await SetSessionAsync(model, V2([], [], report), "all");
        }
        else
        {
            model.OpenDuplicateScannerCommand.Execute(null);
            var groups = duplicateSession.PrepareGroups(report.Groups);
            foreach (var group in groups) group.IsExpanded = false;
            duplicateSession.Replace(groups);
        }

        var window = OpenWindow(model);
        await WpfTestHost.LayoutAsync(window);
        var list = Descendants<ListBox>(window).Single(element => element.Name == (conflict ? "ConflictRowsList" : "DuplicateGroupsList"));
        var scroll = Descendants<ScrollViewer>(list).First();
        var presenter = Descendants<ScrollContentPresenter>(scroll).First();
        var target = list.Items[12];
        var key = (target as ConflictResultRow)?.Key;
        list.ScrollIntoView(target);
        await WpfTestHost.LayoutAsync(window);
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(target);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Top(item) - presenter.ActualHeight * 0.65);
        await WpfTestHost.LayoutAsync(window);

        item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(target);
        var expander = Descendants<Expander>(item).SingleOrDefault();
        var header = expander is null ? item : (FrameworkElement)expander.Template.FindName("ExpanderHeader", expander);
        var initialTop = Top(header);
        var visibleHeight = presenter.ActualHeight - initialTop - header.ActualHeight;
        Assert.IsTrue(initialTop > presenter.ActualHeight / 2 && visibleHeight > 0, "长组标题应在视口下半部分");

        var frames = new List<(TimeSpan Time, double HeaderTop)>();
        void Record(object? sender, EventArgs e)
        {
            var body = key is null
                ? (Border)expander!.Template.FindName("ExpandedContent", expander)
                : list.Items.OfType<ListBoxItem>().FirstOrDefault(row => row.Tag is ConflictResultRow tag && tag.Key == key)?.Content as Border;
            if (body is not { Height: > 0 } || !DependencyPropertyHelper.GetValueSource(body, FrameworkElement.HeightProperty).IsAnimated) return;
            var currentHeader = key is null ? header : ConflictHeader();
            if (currentHeader is not null) frames.Add((((RenderingEventArgs)e).RenderingTime, Top(currentHeader)));
        }

        CompositionTarget.Rendering += Record;
        try
        {
            if (conflict)
            {
                Descendants<Button>(item).Single(button => button.Name == "ExpandButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await model.ConflictRowsRefreshTask;
            }
            else
            {
                var toggle = Descendants<ToggleButton>(item).Single(button => button.Name == "DuplicateHeaderToggle");
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            await Task.Delay(300);
            await WpfTestHost.LayoutAsync(window);
        }
        finally { CompositionTarget.Rendering -= Record; }

        if (SystemParameters.ClientAreaAnimation)
        {
            Assert.IsTrue(frames.Select(frame => frame.Time).Distinct().Count() > 1, "长组的高度动画应持续多个渲染帧");
            Assert.IsTrue(frames.All(frame => Math.Abs(frame.HeaderTop - initialTop) <= 1), "动画期间应固定组标题位置");
        }
        else Assert.IsEmpty(frames);
        var finalHeader = conflict ? ConflictHeader() : header;
        Assert.IsNotNull(finalHeader);
        Assert.AreEqual(initialTop, Top(finalHeader), 1d, "展开结束后标题应留在原位");
        Save(window, $"long-result-group-{(conflict ? "conflict" : "duplicate")}.png");

        double Top(FrameworkElement element) => element.TranslatePoint(new Point(), presenter).Y;
        ListBoxItem? ConflictHeader() => Descendants<ListBoxItem>(list)
            .FirstOrDefault(row => row.DataContext is ConflictResultRow data && data.Key == key);
    });
}
