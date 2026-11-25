using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class SettingsPage : UserControl
{
    private static readonly TimeSpan UpdateFailureToastFadeDuration = TimeSpan.FromMilliseconds(150);
    private readonly DispatcherTimer updateFailureToastTimer = new(DispatcherPriority.Normal)
    {
        Interval = TimeSpan.FromSeconds(2),
    };

    public SettingsPage()
    {
        InitializeComponent();
        updateFailureToastTimer.Tick += (_, _) => HideAboutUpdateFailureToast();
        var popupGap = (double)FindResource("AboutUpdatePopupGap");
        AboutUpdatePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, -popupSize.Height - popupGap), PopupPrimaryAxis.Horizontal)];
    }

    private void AboutContent_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded || ((UIElement)sender).IsVisible) return;
        HideAboutUpdateFailureToast();
        AboutUpdatePopup.SetCurrentValue(Popup.IsOpenProperty, false);
        if (DataContext is MainWindowViewModel model) model.ResetManualUpdateCheck();
    }

    private void AboutUpdateFailureToast_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (!IsLoaded) return;
        HideAboutUpdateFailureToast();
        if (!AboutContent.IsVisible || ((TextBlock)sender).Text.Length == 0) return;
        AboutUpdateFailureToast.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            var fade = new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(UpdateFailureToastFadeDuration)),
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(updateFailureToastTimer.Interval - UpdateFailureToastFadeDuration)),
                    new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(updateFailureToastTimer.Interval)),
                },
            };
            AboutUpdateFailureToast.BeginAnimation(OpacityProperty, fade);
        }
        updateFailureToastTimer.Start();
    }

    private void HideAboutUpdateFailureToast()
    {
        updateFailureToastTimer.Stop();
        AboutUpdateFailureToast.Visibility = Visibility.Collapsed;
        AboutUpdateFailureToast.BeginAnimation(OpacityProperty, null);
    }

    private void AboutUpdatePopup_Opened(object? sender, EventArgs e) =>
        (AboutUpdateDownloadButton.IsVisible ? AboutUpdateDownloadButton :
            AboutUpdateRestartButton.IsVisible ? AboutUpdateRestartButton : AboutUpdateCancelButton).Focus();

    private void AboutUpdatePopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        AboutUpdatePopup.SetCurrentValue(Popup.IsOpenProperty, false);
        CheckUpdateButton.Focus();
        e.Handled = true;
    }

    private void CacheClearContent_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var content = (UIElement)sender;
        if (content.IsVisible && SystemParameters.ClientAreaAnimation)
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            { FillBehavior = FillBehavior.Stop });
    }

    private void SettingsSpinner_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var spinner = (UIElement)sender;
        var rotation = (RotateTransform)spinner.RenderTransform;
        if (rotation.IsFrozen)
            spinner.RenderTransform = rotation = rotation.Clone();
        var animation = spinner.IsVisible && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700)) { RepeatBehavior = RepeatBehavior.Forever }
            : null;
        rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void ManagementList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ListBox list && FindScroll(list) is { } scroll)
        {
            if (e.Delta > 0) scroll.LineUp(); else scroll.LineDown();
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }
}
