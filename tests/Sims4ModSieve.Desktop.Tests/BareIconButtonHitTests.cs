using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class BareIconButtonHitTests
{
    [TestMethod]
    public void BareIconHitAreaAndDeleteConfirmationRenderCorrectly()
    {
        WpfTestHost.RunAsync(() =>
        {
                var app = Application.Current;
                var button = new Button
                {
                    Width = 24,
                    Height = 24,
                    Background = (Brush)app.Resources["SurfaceBrush"],
                    Style = (Style)app.Resources["BareIconButtonStyle"],
                    Content = new Grid
                    {
                        Width = 16,
                        Height = 16,
                        IsHitTestVisible = false,
                    },
                };
                var host = new Window
                {
                    Width = 100,
                    Height = 100,
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = button,
                };

                host.Show();
                button.ApplyTemplate();
                host.UpdateLayout();

                Point[] points =
                [
                    new(12, 12),
                    new(2, 2),
                    new(22, 2),
                    new(2, 22),
                    new(22, 22),
                ];

                foreach (var point in points)
                {
                    var hit = button.InputHitTest(point) as DependencyObject;
                    Assert.IsNotNull(hit, $"No hit target at {point}.");
                    Assert.IsTrue(IsInsideButton(hit, button), $"Hit at {point} did not route through the button.");
                }

                var confirmation = new DeleteConfirmationWindow
                {
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };

                confirmation.Show();
                confirmation.UpdateLayout();
                Assert.IsTrue(confirmation.IsVisible);
                confirmation.Close();
                host.Close();
        }).GetAwaiter().GetResult();
    }

    private static bool IsInsideButton(DependencyObject current, Button button)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, button))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
