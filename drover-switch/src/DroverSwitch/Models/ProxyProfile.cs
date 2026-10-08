namespace DroverSwitch.Models;

/// <summary>
/// One saved proxy config. Empty <see cref="ProxyUrl"/> means "Direct" (no proxy),
/// matching drover's own "Direct mode" (UDP-only bypass, no TCP proxy).
/// Name is the unique key (drover-switch.ini keys profiles by name, so there is no separate id).
/// </summary>
public class ProxyProfile
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Raw value written verbatim into drover.ini's "proxy" key.
    /// Formats accepted by drover: "http://host:port", "socks5://host:port",
    /// "http://login:password@host:port" (auth is HTTP-only).
    /// </summary>
    public string ProxyUrl { get; set; } = "";
}
