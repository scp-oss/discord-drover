using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DroverSwitch.Views;

/// <summary>Accent-blue border for the active profile's card, a subtle neutral border otherwise.</summary>
public class ActiveToBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = Freeze(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush InactiveBrush = Freeze(Color.FromRgb(0x27, 0x30, 0x45));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ActiveBrush : InactiveBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
