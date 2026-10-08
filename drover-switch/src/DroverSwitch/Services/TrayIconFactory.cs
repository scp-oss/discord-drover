using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DroverSwitch.Models;

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

        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(color);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 1.5);
            dc.DrawEllipse(brush, pen, new Point(size / 2.0, size / 2.0), size / 2.0 - 2, size / 2.0 - 2);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        Cache[status] = bitmap;
        return bitmap;
    }
}
