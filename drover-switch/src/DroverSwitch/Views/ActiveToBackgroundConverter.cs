using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DroverSwitch.Views;

/// <summary>Row background: a saturated blue-tinted surface for the active profile, a plain dark
/// surface for the rest - a direct Background binding renders reliably regardless of how
/// ui:Button's own Appearance states happen to be styled internally.</summary>
public class ActiveToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = Freeze(Color.FromRgb(0x1F, 0x3B, 0x7A));
    private static readonly SolidColorBrush InactiveBrush = Freeze(Color.FromRgb(0x14, 0x1B, 0x2E));

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
