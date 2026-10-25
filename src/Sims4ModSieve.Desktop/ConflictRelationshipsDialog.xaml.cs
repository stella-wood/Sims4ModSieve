using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public sealed record ConflictRelationshipDisplayRow(ConflictRelationshipRow? Partner = null,
    ConflictFile? Copy = null, ConflictGroupLink? CopyLink = null, bool IsExpanded = false)
{
    public bool IsCopy => Copy is not null;
    public bool HasCopyLink => CopyLink is not null;
    public string CopiesLabel => Partner?.CopiesLabel ?? string.Empty;
    public IReadOnlyList<ConflictGroupLink> Links => Partner?.Relationships.Select(item => item.Link).Distinct().ToArray() ?? [];
}

public partial class ConflictRelationshipsDialog : UserControl
{
    private MainWindowViewModel? model;
    private ConflictRelationshipsViewModel? relationships;
    private readonly HashSet<string> expandedCopies = new(StringComparer.OrdinalIgnoreCase);
    private IInputElement? previousFocus;
    private bool closing;

    public ConflictRelationshipsDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Model_PropertyChanged; model = null; };
        SizeChanged += (_, _) => FitPane();
    }

    private void Attach()
    {
        if (model is not null) model.PropertyChanged -= Model_PropertyChanged;
        model = DataContext as MainWindowViewModel;
        if (model is not null) model.PropertyChanged += Model_PropertyChanged;
        Synchronize();
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ConflictRelationships)) Synchronize();
    }

    private void Synchronize()
    {
        if (model?.ConflictRelationships is not { } next)
        {
            if (Visibility == Visibility.Visible && !closing) CloseAnimated(() => { });
            return;
        }
        previousFocus = Keyboard.FocusedElement;
        relationships = next;
        Pane.DataContext = next;
        expandedCopies.Clear();
        closing = false;
        RebuildRows();
        Visibility = Visibility.Visible;
        FitPane();
        Animate(open: true);
        Dispatcher.BeginInvoke(() => CloseButton.Focus());
    }

    private void FitPane()
    {
        var window = Window.GetWindow(this);
        var widthScale = (window?.ActualWidth ?? 1180) / 1180;
        var heightScale = (window?.ActualHeight ?? 780) / 780;
        Pane.MaxWidth = Math.Max(1, ActualWidth - 48);
        Pane.Width = Math.Min(620 * widthScale, Pane.MaxWidth);
        Pane.MaxHeight = Math.Max(1, Math.Min(440 * heightScale, ActualHeight - 56));
        Pane.Margin = new Thickness(0, 0, 0, Math.Min(70, ActualHeight * 0.08));
    }

    private void RebuildRows()
    {
        if (relationships is null) return;
        var rows = new List<ConflictRelationshipDisplayRow>();
        void Copies(IReadOnlyList<ConflictFile> copies, ConflictGroupLink? link)
        {
            for (var index = 0; index < copies.Count; index++) rows.Add(new(Copy: copies[index], CopyLink: index == 0 ? link : null));
        }
        var ownExpanded = expandedCopies.Contains(relationships.File.Path);
        OwnCopiesArrow.RenderTransform = new RotateTransform(ownExpanded ? 90 : 0);
        OwnCopiesSeparator.Visibility = ownExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (ownExpanded) Copies(relationships.Copies, relationships.DuplicateLink);
        foreach (var partner in relationships.Related)
        {
            var expanded = expandedCopies.Contains(partner.File.Path);
            rows.Add(new(Partner: partner, IsExpanded: expanded));
            if (expanded) Copies(partner.Copies, partner.DuplicateLink);
        }
        RelationRowsList.ItemsSource = rows;
    }

    private void ToggleCopies(string path)
    {
        if (!expandedCopies.Remove(path)) expandedCopies.Add(path);
        RebuildRows();
    }

    private void OwnCopiesToggle_Click(object sender, RoutedEventArgs e)
    {
        if (relationships is not null) ToggleCopies(relationships.File.Path);
    }
    private void CopiesToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConflictRelationshipDisplayRow { Partner: { } partner }) ToggleCopies(partner.File.Path);
    }
    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConflictGroupLink link)
            CloseAnimated(() => model?.JumpToConflictGroupCommand.Execute(link));
    }
    private void CopyJump_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConflictRelationshipDisplayRow { CopyLink: { } link })
            CloseAnimated(() => model?.JumpToConflictGroupCommand.Execute(link));
    }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated(() => model?.CloseConflictRelationshipsCommand.Execute(null));
    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Backdrop)) CloseAnimated(() => model?.CloseConflictRelationshipsCommand.Execute(null));
    }
    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseAnimated(() => model?.CloseConflictRelationshipsCommand.Execute(null));
    }

    private void Animate(bool open, Action? completed = null)
    {
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? open ? 240 : 180 : 1);
        var easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
        var transform = (TransformGroup)Pane.RenderTransform;
        ((RotateTransform)transform.Children[0]).BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(open ? -4 : 0, open ? 0 : -4, duration) { EasingFunction = easing });
        var scale = (ScaleTransform)transform.Children[1];
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(open ? 0.9 : 1, open ? 1 : 0.9, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(open ? 0.9 : 1, open ? 1 : 0.9, duration) { EasingFunction = easing });
        var opacity = new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, duration) { EasingFunction = easing };
        if (completed is not null) opacity.Completed += (_, _) => completed();
        Pane.BeginAnimation(OpacityProperty, opacity);
    }

    private void CloseAnimated(Action after)
    {
        if (closing) return;
        closing = true;
        Animate(open: false, () =>
        {
            Visibility = Visibility.Collapsed;
            after();
            previousFocus?.Focus();
            relationships = null;
            closing = false;
        });
    }
}
