using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiStatus.Ui.Converters;

public sealed class PercentToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = value is double number && double.IsFinite(number)
            ? Math.Clamp(number, 0, 100)
            : 0;
        double width = string.Equals(parameter as string, "Remaining", StringComparison.OrdinalIgnoreCase)
            ? 100 - percent
            : percent;
        return new GridLength(width, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
