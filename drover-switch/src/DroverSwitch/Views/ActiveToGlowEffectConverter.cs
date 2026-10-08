using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DroverSwitch.Views;

/// <summary>Soft blue glow behind the active profile's card; no effect at all for the rest (cheaper
/// to render than a second DropShadowEffect instance, and avoids a visible shadow on inactive rows).</summary>
public class ActiveToGlowEffectConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
            return null;

        return new DropShadowEffect
        {
            Color = Color.FromRgb(0x3B, 0x82, 0xF6),
            BlurRadius = 18,
            ShadowDepth = 0,
            Opacity = 0.45,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
