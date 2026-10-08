using System.Text;
using DroverSwitch.Models;

namespace DroverSwitch.Services;

/// <summary>
/// drover.ini can only hold ONE active "proxy =" line - that's drover's own format (Options.pas),
/// not something this app can change. So the full pool of configured proxies (the ones NOT currently
/// active) lives in a companion file written right next to drover.ini: drover-switch.ini.
///
/// It's plain text and safe to hand-edit, the same way drover.ini itself is meant to be hand-edited -
/// DroverSwitch re-reads it on its own (see TrayViewModel's companion-file watch), no restart needed.
/// </summary>
public static class ProfilePoolFile
{
    public const string FileName = "drover-switch.ini";

    public static void Write(string discordDir, IEnumerable<ProxyProfile> profiles, string? activeName)
    {
        var sb = new StringBuilder();
        sb.Append("; DroverSwitch - pool of proxy configs for Discord Drover.\r\n");
        sb.Append("; drover.ini only stores ONE active proxy, so the rest of the list lives here.\r\n");
        sb.Append("; Safe to edit by hand: DroverSwitch re-reads this file automatically.\r\n");
        sb.Append("\r\n");
        sb.Append("[active]\r\n");
        sb.Append($"proxy = {activeName ?? ""}\r\n");
        sb.Append("\r\n");
        sb.Append("[profiles]\r\n");
        foreach (var p in profiles)
            sb.Append($"{p.Name} = {p.ProxyUrl}\r\n");

        File.WriteAllText(Path.Combine(discordDir, FileName), sb.ToString(), new UTF8Encoding(false));
    }

    public static ProfilePool? TryRead(string discordDir)
    {
        var path = Path.Combine(discordDir, FileName);
        if (!File.Exists(path))
            return null;

        string? section = null;
        string? activeName = null;
        var profiles = new List<ProxyProfile>();

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (section == "active" && key.Equals("proxy", StringComparison.OrdinalIgnoreCase))
                activeName = value.Length > 0 ? value : null;
            else if (section == "profiles" && key.Length > 0)
                profiles.Add(new ProxyProfile { Name = key, ProxyUrl = value });
        }

        return new ProfilePool(profiles, activeName, File.GetLastWriteTimeUtc(path));
    }
}

public record ProfilePool(List<ProxyProfile> Profiles, string? ActiveName, DateTime WrittenAtUtc);
