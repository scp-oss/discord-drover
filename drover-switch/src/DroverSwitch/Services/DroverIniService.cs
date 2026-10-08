using System.Text;
using System.Text.RegularExpressions;

namespace DroverSwitch.Services;

/// <summary>
/// Reads/writes drover.ini in the exact format drover's own Options.pas (SaveOptions/LoadOptions) uses:
///   [drover]
///   proxy = &lt;value&gt;
/// Writing is the only thing this app does to drover's install - it never touches version.dll.
/// </summary>
public static class DroverIniService
{
    public static void WriteProxyToAllDirs(IEnumerable<string> discordDirs, string proxyUrl)
    {
        foreach (var dir in discordDirs)
        {
            var path = Path.Combine(dir, DiscordLocator.OptionsFileName);
            var content = $"[drover]\r\nproxy = {proxyUrl.Trim()}\r\n";
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public static string? ReadProxy(string discordDir)
    {
        var path = Path.Combine(discordDir, DiscordLocator.OptionsFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                var match = Regex.Match(line, @"\A proxy \s* = \s* (.*) \z",
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // A locked or malformed file just means "unknown" - not fatal for the switcher.
        }

        return null;
    }
}
