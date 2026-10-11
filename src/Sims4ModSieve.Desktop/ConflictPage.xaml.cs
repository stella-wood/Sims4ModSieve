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
}
