using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DroverSwitch.Views;

/// <summary>Gives the active profile's row a visibly tinted background - a direct Background
/// binding is guaranteed to render regardless of how ui:Button's own Appearance states are
/// styled, unlike binding Appearance itself (which showed no visible difference in practice).</summary>
public class ActiveToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = CreateFrozenBrush();

    private static SolidColorBrush CreateFrozenBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ActiveBrush : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
