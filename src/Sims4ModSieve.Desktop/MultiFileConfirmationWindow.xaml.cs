using System.Windows;
using Sims4ModSieve.Core.Conflicts;

namespace Sims4ModSieve.Desktop;

public partial class MultiFileConfirmationWindow : Window
{
    public MultiFileConfirmationWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
    }

    public MultiFileConfirmationWindow(ConflictGroupingSize size) : this("扫描没有完成", size.WarningMessage)
    {
        Width = 400;
        Height = 160;
        CancelButton.Content = "关闭";
        CancelButton.Width = 56;
        CancelButton.IsCancel = true;
        ConfirmButton.Content = "前往重复检测";
        ConfirmButton.Width = 112;
        ConfirmButton.IsDefault = true;
        if (size.Reason != "duplicate-copies")
        {
            ConfirmButton.Visibility = Visibility.Collapsed;
            CancelButton.Margin = new Thickness(0);
        }
    }

    public static string ConfirmationMessage(string action, int count, bool closeGame) =>
        (closeGame ? "请先关闭游戏\n" : string.Empty) +
        (count == 1 ? $"确定{action}所选文件？" : $"确定{action}这 {count} 个文件？");

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
