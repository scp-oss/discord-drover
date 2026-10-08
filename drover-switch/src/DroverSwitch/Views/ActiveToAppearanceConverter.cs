using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace DroverSwitch.Views;

/// <summary>Gives the active profile's row a filled accent background instead of relying on a
/// small checkmark glyph alone to show "this one is current" in a compact list.</summary>
public class ActiveToAppearanceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ControlAppearance.Primary : ControlAppearance.Secondary;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
