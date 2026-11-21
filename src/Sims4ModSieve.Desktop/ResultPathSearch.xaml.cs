using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sims4ModSieve.Desktop;

public partial class ResultPathSearch : UserControl
{
    private const double CollapsedWidth = 30;
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(220);
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(175) };
    private readonly DispatcherTimer collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer flashDelay = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private bool expanded;
    private double targetWidth = CollapsedWidth;

    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(nameof(Query), typeof(string),
        typeof(ResultPathSearch), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, QueryChanged));
    public static readonly DependencyProperty ResultSurfaceProperty = DependencyProperty.Register(nameof(ResultSurface),
        typeof(FrameworkElement), typeof(ResultPathSearch), new PropertyMetadata(null, ResultSurfaceChanged));

    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public FrameworkElement? ResultSurface { get => (FrameworkElement?)GetValue(ResultSurfaceProperty); set => SetValue(ResultSurfaceProperty, value); }
    public bool Compact { get; set; }

    public ResultPathSearch()
    {
        InitializeComponent();
        SearchBox.Width = CollapsedWidth;
        QueryInput.TextChanged += QueryInput_TextChanged;
        Loaded += (_, _) =>
        {
            SearchBox.Height = Compact ? 24 : 30;
            SearchBox.Width = targetWidth;
            IconColumn.Width = new GridLength(Compact ? 28 : 30);
            SearchSurface.CornerRadius = new CornerRadius(SearchBox.Height / 2);
            Margin = new Thickness(0, 0, Compact ? 4 : 8, 0);
            if (Compact)
            {
                QueryInput.Margin = new Thickness(0, 1, 2, 1);
                ClearSearch.Margin = new Thickness(0, 0, 3, 0);
            }
            RefreshExpansion();
        };
        searchDelay.Tick += (_, _) => ApplyQuery();
        collapseDelay.Tick += (_, _) =>
        {
            collapseDelay.Stop();
            DismissFocus();
            RefreshExpansion();
        };
        flashDelay.Tick += (_, _) => EndFlash();
        MouseEnter += (_, _) => { collapseDelay.Stop(); RefreshExpansion(); };
        MouseLeave += (_, _) => { if (QueryInput.Text.Length == 0) collapseDelay.Start(); };
        IsKeyboardFocusWithinChanged += (_, _) => RefreshExpansion();
        IsEnabledChanged += (_, _) =>
        {
            if (IsEnabled) return;
            searchDelay.Stop();
            collapseDelay.Stop();
            EndFlash();
            QueryInput.Text = Query;
            RefreshExpansion();
        };
        Unloaded += (_, _) => { searchDelay.Stop(); collapseDelay.Stop(); EndFlash(); };
    }

    internal void FocusInput()
    {
        collapseDelay.Stop();
        SetExpanded(true);
        QueryInput.Focus();
    }

    internal bool ClearQuery()
    {
        if (QueryInput.Text.Length == 0) return false;
        QueryInput.Clear();
        return true;
    }

    private static void QueryChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var search = (ResultPathSearch)target;
        search.searchDelay.Stop();
        search.QueryInput.Text = (string)e.NewValue;
    }

    private static void ResultSurfaceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var search = (ResultPathSearch)target;
        if (e.OldValue is FrameworkElement old) old.PreviewMouseDown -= search.ResultSurface_PreviewMouseDown;
        if (e.NewValue is FrameworkElement current) current.PreviewMouseDown += search.ResultSurface_PreviewMouseDown;
    }

    private void QueryInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchDelay.Stop();
        if (QueryInput.Text.Length == 0) ApplyQuery();
        else
        {
            collapseDelay.Stop();
            if (QueryInput.Text != Query) searchDelay.Start();
        }
        ClearSearch.Visibility = QueryInput.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshExpansion();
    }

    private void ApplyQuery()
    {
        searchDelay.Stop();
        SetCurrentValue(QueryProperty, QueryInput.Text);
    }

    private void QueryInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyQuery();
        DismissFocus();
        if (ResultSurface is { } surface)
        {
            flashDelay.Stop();
            surface.IsHitTestVisible = false;
            flashDelay.Start();
        }
        e.Handled = true;
    }

    private void EndFlash()
    {
        flashDelay.Stop();
        if (ResultSurface is not { } surface) return;
        surface.IsHitTestVisible = true;
    }

    private void ResultSurface_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        collapseDelay.Stop();
        DismissFocus();
        RefreshExpansion();
    }

    private void DismissFocus()
    {
        if (IsKeyboardFocusWithin) ResultSurface?.Focus();
    }

    private void OpenSearch_Click(object sender, RoutedEventArgs e) => FocusInput();
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { FocusInput(); ClearQuery(); }

    private void RefreshExpansion() => SetExpanded(IsEnabled && (QueryInput.Text.Length > 0 || IsMouseOver || IsKeyboardFocusWithin));

    private void SetExpanded(bool value)
    {
        var width = value ? MeasureExpandedWidth() : CollapsedWidth;
        var stateChanged = expanded != value;
        if (!stateChanged && width == targetWidth) return;
        expanded = value;
        targetWidth = width;
        if (value) QueryInput.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(SearchBox.Width, width,
            stateChanged ? ExpandDuration : TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = value && stateChanged ? new QuadraticEase { EasingMode = EasingMode.EaseIn }
                : new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) => { if (!expanded) QueryInput.Visibility = Visibility.Collapsed; };
        SearchBox.Width = width;
        SearchBox.BeginAnimation(WidthProperty, animation);
        if (!stateChanged) return;
        var fade = new DoubleAnimation(SearchSurface.Opacity, value ? 1 : 0, ExpandDuration) { FillBehavior = FillBehavior.Stop };
        SearchSurface.Opacity = fade.To!.Value;
        SearchSurface.BeginAnimation(OpacityProperty, fade);
    }

    private double MeasureExpandedWidth()
    {
        var typeface = new Typeface(QueryInput.FontFamily, QueryInput.FontStyle, QueryInput.FontWeight, QueryInput.FontStretch);
        var text = new FormattedText(QueryInput.Text, QueryInput.Language.GetEquivalentCulture(), QueryInput.FlowDirection,
            typeface, QueryInput.FontSize, QueryInput.Foreground, VisualTreeHelper.GetDpi(QueryInput).PixelsPerDip);
        var width = Math.Ceiling(text.WidthIncludingTrailingWhitespace);
        return Compact ? Math.Clamp(width + 61, 144, 236) : Math.Clamp(width + 65, 164, 260);
    }
}
