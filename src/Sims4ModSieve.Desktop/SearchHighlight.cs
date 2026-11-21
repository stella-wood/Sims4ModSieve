using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Sims4ModSieve.Core.Reporting;

namespace Sims4ModSieve.Desktop;

public static class SearchHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string),
        typeof(SearchHighlight), new PropertyMetadata("", Update));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached("Query", typeof(PathSearch),
        typeof(SearchHighlight), new PropertyMetadata(null, Update));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached("Brush", typeof(Brush),
        typeof(SearchHighlight), new PropertyMetadata(null, Update));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
    public static PathSearch? GetQuery(DependencyObject element) => (PathSearch?)element.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject element, PathSearch? value) => element.SetValue(QueryProperty, value);
    public static Brush? GetBrush(DependencyObject element) => (Brush?)element.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject element, Brush? value) => element.SetValue(BrushProperty, value);

    private static void Update(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var label = (TextBlock)target;
        var value = GetText(label);
        label.Inlines.Clear();
        var index = 0;
        foreach (var range in GetQuery(label)?.FindMatches(value) ?? [])
        {
            var (start, length) = range.GetOffsetAndLength(value.Length);
            if (index < start) label.Inlines.Add(new Run(value[index..start]));
            label.Inlines.Add(new Run(value.Substring(start, length)) { Background = GetBrush(label) });
            index = start + length;
        }
        if (index < value.Length) label.Inlines.Add(new Run(value[index..]));
    }
}
