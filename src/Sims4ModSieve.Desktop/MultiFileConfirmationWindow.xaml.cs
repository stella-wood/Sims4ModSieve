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

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
