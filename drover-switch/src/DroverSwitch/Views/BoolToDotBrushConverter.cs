using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DroverSwitch.Views;

/// <summary>Colors the small status dot next to "Активен: X" - green when something is active,
/// muted gray when nothing is.</summary>
public class BoolToDotBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OnBrush = Freeze(Color.FromRgb(0x2E, 0xA4, 0x4D));
    private static readonly SolidColorBrush OffBrush = Freeze(Color.FromRgb(0x6B, 0x74, 0x88));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? OnBrush : OffBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
