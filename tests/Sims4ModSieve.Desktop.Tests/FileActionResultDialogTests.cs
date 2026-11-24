using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Components;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class FileActionResultDialogTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(12)]
    public Task ResultDialogShowsFailureDetailsAndADismissButton(int failureCount) => WpfTestHost.RunAsync(() =>
    {
        var title = failureCount == 1 ? "未能恢复" : "部分文件未能删除";
        var longPath = @"D:\Games\The Sims 4\Mods\家具与装饰\厨房与餐厅扩充包\餐边柜与玻璃陈列柜\浅胡桃木组合套装.package";
        var first = new FileActionFailure(longPath, failureCount == 1 ? "原路径已有同名文件" : "文件正在使用，无法移入回收站");
        var last = new FileActionFailure(string.Empty, "删除记录未保存");
        FileActionFailure[] failures = failureCount == 1
            ? [first]
            : [first, .. Enumerable.Range(1, failureCount - 2)
                .Select(index => new FileActionFailure($@"D:\Games\The Sims 4\Mods\装饰\室内装饰{index}.package", "无法访问文件")), last];
        var owner = new Window();
        WpfTestHost.ShowOffScreen(owner);
        Application.Current.MainWindow = owner;
        Exception? inspectionFailure = null;
        var dismissed = false;
        owner.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<WarningDialog>().Single();
            try
            {
                dialog.UpdateLayout();
                Assert.AreSame(owner, dialog.Owner);
                Assert.AreEqual(title, dialog.Title);
                Assert.IsFalse(((TextBlock)dialog.FindName("MessageText")).IsVisible);
                var list = (ListBox)dialog.FindName("FailureList");
                Assert.IsTrue(list.IsVisible);
                CollectionAssert.AreEqual(failures, list.Items.Cast<FileActionFailure>().ToArray());
                var firstRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var path = Descendants<TextBlock>(firstRow).Single(text => text.Name == "FailurePath");
                var reason = Descendants<TextBlock>(firstRow).Single(text => text.Name == "FailureReason");
                Assert.AreEqual(first.Path, path.Text);
                Assert.AreEqual(TextWrapping.Wrap, path.TextWrapping);
                Assert.AreEqual(first.Message, reason.Text);
                var icon = Descendants<Image>(list).Single(image => image.IsVisible);
                Assert.AreSame(icon, Descendants<Image>(firstRow).Single());
                Assert.AreEqual(VerticalAlignment.Center, icon.VerticalAlignment);
                var button = Descendants<Button>(dialog).Single(candidate => candidate.IsVisible);
                Assert.AreEqual("确定", button.Content);
                Assert.IsTrue(button.IsDefault && button.IsCancel);

                var scroll = Descendants<ScrollViewer>(list).Single();
                Assert.AreEqual(failureCount > 1, scroll.ScrollableHeight > 0);
                Save(dialog, failureCount == 1 ? "restore-failure-warning.png" : "partial-delete-warning.png", dialog);
                if (failureCount > 1)
                {
                    list.ScrollIntoView(last);
                    dialog.UpdateLayout();
                    Assert.IsGreaterThan(0, scroll.VerticalOffset);
                    var lastRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(last);
                    Assert.IsFalse(Descendants<TextBlock>(lastRow).Single(text => text.Name == "FailurePath").IsVisible);
                    Assert.AreEqual(last.Message, Descendants<TextBlock>(lastRow).Single(text => text.Name == "FailureReason").Text);
                    Assert.IsTrue(button.IsVisible);
                }
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dismissed = true;
            }
            catch (Exception exception)
            {
                inspectionFailure = exception;
                dialog.Close();
            }
        }, DispatcherPriority.ApplicationIdle);

        new FileActionDialogService().ShowFileActionResultProblems(title, failures);

        if (inspectionFailure is not null) throw inspectionFailure;
        Assert.IsTrue(dismissed);
    });
}
