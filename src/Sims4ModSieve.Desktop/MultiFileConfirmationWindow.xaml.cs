using System.Windows;

namespace Sims4ModSieve.Desktop;

public partial class MultiFileConfirmationWindow : Window
{
    public MultiFileConfirmationWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
    }

    public static string ConfirmationMessage(string action, int count, bool closeGame) =>
        (closeGame ? "请先关闭游戏\n" : string.Empty) +
        (count == 1 ? $"确定{action}所选文件？" : $"确定{action}这 {count} 个文件？");

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
