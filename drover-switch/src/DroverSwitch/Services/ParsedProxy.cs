using System.Text.RegularExpressions;

namespace DroverSwitch.Services;

/// <summary>
/// Mirrors drover's own TProxyValue.ParseFromString (Options.pas) exactly, so a URL that drover
/// accepts is parsed by the switcher the same way, and vice versa.
/// </summary>
public readonly record struct ParsedProxy(
    bool IsSpecified,
    string Protocol,
    string Login,
    string Password,
    string Host,
    int Port,
    bool IsHttp,
    bool IsSocks5,
    bool IsAuth)
{
    private static readonly Regex Pattern = new(
        @"\A(?:([a-z\d]+)://)?(?:(.+):(.+)@)?(.+):(\d+)\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedProxy Parse(string? url)
    {
        url = (url ?? "").Trim();
        var match = Pattern.Match(url);
        if (!match.Success)
            return default;

        var protocol = match.Groups[1].Value.Trim().ToLowerInvariant();
        if (protocol is "" or "https")
            protocol = "http";

        var login = match.Groups[2].Value.Trim();
        var password = match.Groups[3].Value.Trim();
        var host = match.Groups[4].Value.Trim();
        var port = int.TryParse(match.Groups[5].Value, out var p) ? p : 0;

        var isHttp = protocol == "http";
        var isSocks5 = protocol == "socks5";
        var isAuth = login.Length > 0 && password.Length > 0;

        return new ParsedProxy(true, protocol, login, password, host, port, isHttp, isSocks5, isAuth);
    }

    /// <summary>Host:port with any credentials hidden, for display in the UI.</summary>
    public string ToDisplayString()
    {
        if (!IsSpecified)
            return "Прямое соединение";
        return $"{Protocol}://{Host}:{Port}";
    }
}
