using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DroverSwitch.Views;

/// <summary>Fill for the small status circle on each row: solid accent blue when this profile is
/// active, transparent (just an outline, via ActiveToBorderBrushConverter on Stroke) otherwise.</summary>
public class ActiveToAccentOrTransparentConverter : IValueConverter
{
    private static readonly SolidColorBrush AccentBrush = Freeze(Color.FromRgb(0x3B, 0x82, 0xF6));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? AccentBrush : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
