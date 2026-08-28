using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ReservePane.Model;
using MediaColor = System.Windows.Media.Color;

namespace ReservePane.Ui.Converters;

public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush NormalBrush = CreateFrozenBrush(0x35, 0xC4, 0x6A);
    private static readonly SolidColorBrush WarningBrush = CreateFrozenBrush(0xF0, 0xA4, 0x3A);
    private static readonly SolidColorBrush CriticalBrush = CreateFrozenBrush(0xE2, 0x4B, 0x4B);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Severity.Normal => NormalBrush,
        Severity.Warning => WarningBrush,
        Severity.Critical => CriticalBrush,
        _ => NormalBrush,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(MediaColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
