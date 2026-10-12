using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Sims4ModSieve.Desktop;

public partial class ConflictPage : UserControl
{
    public ConflictPage()
    {
        InitializeComponent();
        ConflictFilterPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Horizontal)];
    }

    private void ConflictOverviewViewport_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0 && e.ViewportHeightChange == 0) return;

        var window = Window.GetWindow(this);
        if (window is null) return;

        var scale = Math.Min(window.ActualWidth / 1180, window.ActualHeight / 780);
        scale = Math.Min(scale, (ConflictOverviewViewport.ViewportWidth - 20) / (430 * 2));
        scale = Math.Min(scale, (ConflictOverviewViewport.ViewportHeight - 20) / (210 * 2));
        // 保持文字和图标原尺寸，窄窗口下保留内容所需的最低卡片高度。
        ConflictOverviewCards.Width = Math.Max(0, 430 * scale * 2 + 20);
        ConflictOverviewCards.Height = Math.Max(200, 210 * scale) * 2 + 20;
    }

}
