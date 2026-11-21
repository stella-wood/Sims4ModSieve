using System.Globalization;
using System.Windows.Data;

namespace Sims4ModSieve.Desktop.Infrastructure;

public sealed class SearchResultCountConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[1] is IReadOnlyList<object> { Count: > 0 } rows && ReferenceEquals(values[0], rows[0]) ? values[2] : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
