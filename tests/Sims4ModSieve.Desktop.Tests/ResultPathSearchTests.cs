using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ResultPathSearchTests
{
    private const string Match = @"C:\Demo Mods\Creator\MouthScale.package";
    private const string Peer = @"C:\Demo Mods\Archive\OtherName.package";
    private const string Other = @"C:\Demo Mods\Folder\unrelated.package";
    private const string OtherCopy = @"C:\Demo Mods\Backup\unrelated-copy.package";

    [TestMethod]
    public void DuplicateSearchKeepsWholeGroupsAndResetsWithTheSession()
    {
        var session = new DuplicateResultSession();
        var groups = session.PrepareGroups(DuplicateFixture().Groups);
        session.Replace(groups);

        session.DuplicatePathQuery = @"ｃｒｅａｔｏｒ\ｍｏｕｔｈ";
        Assert.AreSame(groups[0], session.VisibleDuplicateGroups.Single());
        CollectionAssert.AreEquivalent(new[] { Match, Peer },
            session.VisibleDuplicateGroups.Single().Files.Select(file => file.Path).ToArray());
        Assert.AreEqual(2, session.DuplicateGroupCount);
        Assert.AreEqual("找到 1 组", session.DuplicateSearchStatus);
        session.ApplySuggestions();
        Assert.AreEqual(1, groups[0].SelectedCount);
        Assert.AreEqual(0, groups[1].SelectedCount);
        session.DuplicatePathQuery = "";
        Assert.AreSame(groups, session.VisibleDuplicateGroups);

        session.DuplicatePathQuery = "Mouth";
        session.Reset();
        session.Replace(groups);
        Assert.AreEqual("", session.DuplicatePathQuery);
        Assert.AreSame(groups, session.VisibleDuplicateGroups);
    }

    [TestMethod]
    public void ConflictSearchKeepsWholeGroupsAndComposesWithExistingFilters()
    {
        var session = V2([Resource(1, (Match, "one"), (Peer, "two"))], [(Match, 10), (Peer, 10)],
            Duplicates(Other, OtherCopy));
        var card = session.Cards.Single(item => item.Type == "override");
        session.ToggleExpanded(card.Key);
        var expanded = session.GetExpandedKeysSnapshot();

        IReadOnlyList<ConflictResultRow> Search(string filter = "all", HashSet<string>? types = null, bool showIgnored = false) =>
            session.BuildVisibleRows("all", filter, expanded, showIgnored: showIgnored, types: types, search: new("mouth"));
        var rows = Search();
        Assert.AreEqual(card.Key, rows.Single(row => row.IsCardStart).Key);
        CollectionAssert.AreEquivalent(new[] { Match, Peer },
            rows.Where(row => row.File is not null).Select(row => row.File!.Path).ToArray());
        CollectionAssert.AreEqual(rows.Select(row => row.Key).ToArray(), Search("different").Select(row => row.Key).ToArray());
        Assert.IsEmpty(Search("same"));
        Assert.IsEmpty(Search(types: ["duplicates"]));

        session.SetIgnoredFiles([Match]);
        Assert.IsEmpty(Search());
        Assert.AreEqual(card.Key, Search(showIgnored: true).Single(row => row.IsCardStart).Key);
    }

    [TestMethod]
    public Task DetectionPagesKeepTheirOwnQueryWhenNavigating() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await SetSessionAsync(model, V2([], [], DuplicateFixture()), "all");
        model.ConflictPathQuery = "unrelated";
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual("找到 1 组", model.ConflictSearchStatus);

        model.OpenDuplicateScannerCommand.Execute(null);
        model.DuplicatePathQuery = "does not exist";
        Assert.AreEqual("没找到相关文件", model.DuplicatePlaceholderText);
        Assert.AreEqual("找到 0 组", model.DuplicateSearchStatus);

        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        Assert.AreEqual("unrelated", model.ConflictPathQuery);
        Assert.AreEqual("找到 1 组", model.ConflictSearchStatus);
        model.ConflictPathQuery = "missing";
        await model.ConflictRowsRefreshTask;
        Assert.IsTrue(model.ShowEmptyConflictRows);
        Assert.AreEqual("没找到相关文件", model.EmptyConflictText);
        Assert.AreEqual("找到 0 组", model.ConflictSearchStatus);

        model.OpenDuplicateScannerCommand.Execute(null);
        Assert.AreEqual("does not exist", model.DuplicatePathQuery);
    });

    private static DuplicateReport DuplicateFixture()
    {
        var first = Duplicates(Match, Peer);
        return first with { Groups = [first.Groups.Single(), Duplicates(Other, OtherCopy).Groups.Single() with { Sha256 = "SECOND-HASH" }] };
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SearchInputUpdatesResultsAndClearsOnEscape(bool conflict) => WpfTestHost.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var duplicates = new DuplicateResultSession();
        duplicates.Replace(duplicates.PrepareGroups(DuplicateFixture().Groups));
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FolderPickerService(), new DuplicateSettingsStore(temp.Path), resultSession: duplicates);
        if (conflict)
        {
            model.OpenConflictCommand.Execute(null);
            model.OpenConflictSectionCommand.Execute("basic");
            var session = V2([Resource(1, (Match, "one"), (Peer, "two"))], [(Match, 10), (Peer, 10)]);
            session.ToggleExpanded(session.Cards.Single().Key);
            await SetSessionAsync(model, session, "all");
        }
        else model.OpenDuplicateScannerCommand.Execute(null);

        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await WpfTestHost.LayoutAsync(window);
        var search = Descendants<ResultPathSearch>(window).Single(control => control.IsVisible);
        var input = (TextBox)search.FindName("QueryInput");
        var surface = search.ResultSurface!;
        var list = Descendants<ListBox>(window).Single(control => control.Name == (conflict ? "ConflictRowsList" : "DuplicateGroupsList"));
        var presenter = Descendants<ScrollContentPresenter>(list).First();
        TextBlock CountLine() => Descendants<TextBlock>(presenter).Single(text => text.Name == "ResultCount" && text.IsVisible);
        double FirstCardTop() => Descendants<ContentPresenter>(CountLine().Parent).First().TranslatePoint(new Point(), presenter).Y;
        var before = FirstCardTop();
        Assert.AreEqual("", CountLine().Text);
        search.FocusInput();
        input.Text = "Creator Mouth";
        await CheckTransition(() => SendKey(Key.Enter), () => conflict ? model.ConflictSearch.IsActive : model.DuplicateSearch.IsActive);
        Assert.AreEqual("Creator Mouth", conflict ? model.ConflictPathQuery : model.DuplicatePathQuery);
        var matches = Descendants<TextBlock>(window).Where(text => text.IsVisible)
            .SelectMany(text => text.Inlines.OfType<Run>()).Where(run => run.Background is not null).Select(run => run.Text).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "Creator", "Mouth" }, matches);
        list.AlternationCount = 1; // 每个已生成的容器都可能拿到 0，计数仍只能跟着真正的首项。
        var count = CountLine();
        Assert.AreSame(list.Items[0], count.DataContext);
        Assert.AreEqual("找到 1 组", count.Text);
        Assert.AreEqual(before, FirstCardTop(), 1d, "组数文字出现时首组应保持位置");
        if (conflict)
        {
            await CheckTransition(() => model.SelectConflictFilterCommand.Execute("same"), () => model.ConflictRows.Count == 0);
            model.SelectConflictFilterCommand.Execute("all");
            await Refresh();
        }
        if (conflict) Save(window, "result-path-search.png", surface);
        model.GoHomeCommand.Execute(null);
        if (conflict)
        {
            model.OpenConflictCommand.Execute(null);
            model.OpenConflictSectionCommand.Execute("basic");
        }
        else model.OpenDuplicateScannerCommand.Execute(null);
        await Refresh();
        Assert.IsTrue(input.IsVisible);
        Assert.AreEqual("Creator Mouth", input.Text);

        input.Text = "missing";
        SendKey(Key.Enter);
        await Refresh();
        Assert.IsTrue(Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == "没找到相关文件"));
        Assert.IsTrue(Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == "找到 0 组"));
        SendKey(Key.Escape);
        await Refresh();
        Assert.AreEqual("", input.Text);
        Assert.AreEqual("", conflict ? model.ConflictPathQuery : model.DuplicatePathQuery);

        void SendKey(Key key) => input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(input)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        async Task Refresh()
        {
            await model.ConflictRowsRefreshTask;
            await model.DuplicateSearchRefreshTask;
            await WpfTestHost.LayoutAsync(window);
        }

        async Task CheckTransition(Action change, Func<bool> applied)
        {
            var frames = new List<(bool Applied, double Opacity, double Offset)>();
            void Render(object? sender, EventArgs e) => frames.Add((applied(), surface.Opacity,
                (surface.RenderTransform as TranslateTransform)?.Y ?? 0));
            CompositionTarget.Rendering += Render;
            try
            {
                change();
                await Refresh();
                await Task.Delay(180);
                await WpfTestHost.LayoutAsync(window);
            }
            finally { CompositionTarget.Rendering -= Render; }
            Assert.IsTrue(applied());
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.IsTrue(frames.Any(frame => !frame.Applied && frame.Opacity is > 0 and < 1 && frame.Offset == 0),
                    $"旧结果应在原位淡出：{string.Join(", ", frames)}");
                Assert.IsTrue(frames.Any(frame => frame.Applied && frame.Opacity is > 0 and < 1 && frame.Offset > 0),
                    "新结果应在列表容器上同时淡入和上滑");
            }
            else Assert.IsTrue(frames.All(frame => frame.Opacity == 1 && frame.Offset == 0));
            Assert.AreEqual(1d, surface.Opacity);
        }
    });
}
