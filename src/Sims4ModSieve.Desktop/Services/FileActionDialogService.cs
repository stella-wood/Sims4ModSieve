using System.Windows;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Desktop.Components;

namespace Sims4ModSieve.Desktop.Services;

public interface IFileActionDialogService
{
    bool ConfirmDeletion(FileDeletionPreflight preflight);

    bool ConfirmFiles(string title, string message);

    void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures);

    bool ConfirmDuplicateGroupRefresh(int changedGroupCount) => false;

    bool ShowConflictGroupingWarning(ConflictGroupingSize size) => false;
}

public sealed class FileActionDialogService : IFileActionDialogService
{
    public bool ConfirmDuplicateGroupRefresh(int changedGroupCount)
    {
        var dialog = new WarningDialog("重复文件有变动",
            $"有 {changedGroupCount} 组重复文件发生变动。\n部分文件已不存在，刷新后继续确认剩余文件的操作。", "刷新结果")
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    public bool ShowConflictGroupingWarning(ConflictGroupingSize size)
    {
        var duplicateCopies = size.Reason == "duplicate-copies";
        var message = size.WarningMessage.TrimStart('\u26A0', '\uFE0F');
        if (duplicateCopies)
        {
            // 保持括号内的副本数量在同一行，同时允许正文自然换行。
            message = message.Replace("份）", "\u2060份）", StringComparison.Ordinal);
        }

        var dialog = new WarningDialog("扫描未完成", message, duplicateCopies ? "前往重复检测" : null)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    public bool ConfirmDeletion(FileDeletionPreflight preflight)
    {
        return ConfirmFiles("删除文件", MultiFileConfirmationWindow.ConfirmationMessage(
            "删除", preflight.Candidates.Count, closeGame: true));
    }

    public bool ConfirmFiles(string title, string message)
    {
        var dialog = new MultiFileConfirmationWindow(title, message)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var details = string.Join(
            Environment.NewLine,
            failures.Take(8).Select(failure =>
                string.IsNullOrWhiteSpace(failure.Path)
                    ? $"• {failure.Message}"
                    : $"• {failure.Path}{Environment.NewLine}  {failure.Message}"));
        if (failures.Count > 8)
        {
            details += $"{Environment.NewLine}…另有 {failures.Count - 8} 项";
        }

        MessageBox.Show(
            Application.Current?.MainWindow,
            details,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
