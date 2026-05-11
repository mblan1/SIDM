using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SIDM.App.Converters;

/// <summary>
/// True → Visible, false → Collapsed. Pass <c>"invert"</c> as the converter
/// parameter to flip the relationship.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var asBool = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            asBool = !asBool;
        }
        return asBool ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
