namespace DroverSwitch.Models;

/// <summary>Live reachability of a profile's proxy, as last measured by <see cref="Services.ProxyHealthChecker"/>.</summary>
public enum ProfileStatus
{
    Unknown,
    Checking,
    Online,
    Offline,
}
