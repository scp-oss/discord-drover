using System.Collections.Generic;
namespace DroverSwitch.Models;

public class AppSettings
{
    public List<ProxyProfile> Profiles { get; set; } = new();

    public string? ActiveProfileName { get; set; }

    public bool AutoModeEnabled { get; set; }

    public int CheckIntervalSeconds { get; set; } = 20;

    public static AppSettings CreateDefault() => new()
    {
        Profiles = new List<ProxyProfile>
        {
            new() { Name = "Прямое соединение", ProxyUrl = "" },
        },
    };
}
