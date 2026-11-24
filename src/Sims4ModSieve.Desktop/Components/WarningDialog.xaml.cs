using System.Windows;

namespace Sims4ModSieve.Desktop.Components;

public partial class WarningDialog : Window
{
    public WarningDialog(string title, string message, string? actionLabel = null, string? closeLabel = null,
        bool primaryCloseButton = false)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        if (closeLabel is not null) CloseButton.Content = closeLabel;
        if (primaryCloseButton) CloseButton.Style = (Style)FindResource("PrimaryButtonStyle");
        CloseButton.IsDefault = actionLabel is null;

        if (actionLabel is not null)
        {
            ActionButton.Content = actionLabel;
            ActionButton.IsDefault = true;
            ActionButton.Visibility = Visibility.Visible;
        }
    }

    internal WarningDialog(string title, IReadOnlyList<FileActionFailure> failures)
        : this(title, string.Empty, closeLabel: "确定")
    {
        Width = (double)FindResource("FileActionWarningWidth");
        Height = (double)FindResource(failures.Count == 1 ? "FileActionWarningSingleHeight" : "FileActionWarningBatchHeight");
        MessageBody.Visibility = Visibility.Collapsed;
        FailureList.Visibility = Visibility.Visible;
        FailureList.VerticalAlignment = failures.Count == 1 ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        FailureList.AlternationCount = failures.Count;
        FailureList.ItemsSource = failures;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Action_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
