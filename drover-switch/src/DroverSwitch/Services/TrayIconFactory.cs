using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DroverSwitch.Models;
using H.NotifyIcon;

namespace DroverSwitch.Services;

/// <summary>
/// Draws the tray icon as a small solid-color dot entirely at runtime (no shipped .ico asset needed):
/// green = the active profile currently has Discord access, red = it doesn't, gray = still checking.
/// </summary>
public static class TrayIconFactory
{
    private static readonly Dictionary<ProfileStatus, ImageSource> Cache = new();

    public static ImageSource GetDot(ProfileStatus status)
    {
        if (Cache.TryGetValue(status, out var cached))
            return cached;

        var color = status switch
        {
            ProfileStatus.Online => Color.FromRgb(0x2E, 0xA4, 0x4D),
            ProfileStatus.Offline => Color.FromRgb(0xD1, 0x3B, 0x3B),
            ProfileStatus.Checking => Color.FromRgb(0xC8, 0xA8, 0x2A),
            _ => Color.FromRgb(0x8A, 0x8A, 0x8A),
        };

        const double size = 32;

        // H.NotifyIcon's generic ImageSource -> Win32 icon conversion (ToIconAsync) only
        // special-cases GeneratedIconSource; every other ImageSource falls through to code that
        // needs a real backing file/pack Uri, which a bitmap rendered in memory never has
        // (confirmed from crash.log on two different attempts: NotImplementedException on a bare
        // RenderTargetBitmap, then ArgumentNullException/UriFormatException trying to treat a
        // BitmapImage/BitmapFrame as if it had one). GeneratedIconSource is the library's own
        // supported way to draw a dynamic icon, so use that instead of hand-rolled bitmaps.
        var icon = new GeneratedIconSource
        {
            Size = (int)size,
            Text = "",
            Background = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            BorderThickness = 1.5f,
            CornerRadius = new CornerRadius(size / 2),
        };

        Cache[status] = icon;
        return icon;
    }
}
