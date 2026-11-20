using System.Windows;

namespace Sims4ModSieve.Desktop.Components;

public partial class WarningDialog : Window
{
    public WarningDialog(string title, string message, string? actionLabel = null, string? closeLabel = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        if (closeLabel is not null) CloseButton.Content = closeLabel;
        CloseButton.IsDefault = actionLabel is null;

        if (actionLabel is not null)
        {
            ActionButton.Content = actionLabel;
            ActionButton.IsDefault = true;
            ActionButton.Visibility = Visibility.Visible;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Action_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
