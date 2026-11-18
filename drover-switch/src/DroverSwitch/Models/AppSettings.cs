using System.Collections.Generic;
namespace DroverSwitch.Models;

public class AppSettings
{
    public List<ProxyProfile> Profiles { get; set; } = new();

    public string? ActiveProfileName { get; set; }

    public bool AutoModeEnabled { get; set; }

    public bool StartWithWindows { get; set; }

    public int CheckIntervalSeconds { get; set; } = 20;

    /// <summary>Telegram WS proxy (tg-ws-proxy) for Telegram Desktop on this PC and phones on the same Wi-Fi.</summary>
    public bool TgWsEnabled { get; set; }

    public int TgWsPort { get; set; } = 1443;

    /// <summary>32 hex chars, generated once on first enable - changing it would break proxies already
    /// added to Telegram on the PC and phones.</summary>
    public string? TgWsSecret { get; set; }

    public static AppSettings CreateDefault() => new()
    {
        Profiles = new List<ProxyProfile>
        {
            new() { Name = "Прямое соединение", ProxyUrl = "" },
        },
    };
}
