using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SIDM.App.Converters;

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Collapsed,
            string s when string.IsNullOrEmpty(s) => Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
