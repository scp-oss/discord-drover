using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DroverSwitch.Services;

/// <summary>
/// Finds every installed Discord variant's live "app-X.Y.Z" folder, the same way drover's own
/// installer does it (installer/Main.pas: FindDiscordBaseDirs / FindDiscordDirs / GetNewestDiscordDir).
/// Kept independent from drover's Delphi code - this only reads the registry and the filesystem.
/// </summary>
public static class DiscordLocator
{
    public const string DllFileName = "version.dll";
    public const string OptionsFileName = "drover.ini";
    public const string PacketFileName = "drover-packet.bin";

    private static readonly string[] AppNames = { "Discord", "DiscordCanary", "DiscordPTB" };

    private static readonly string[] ExeNames =
    {
        "Discord.exe", "DiscordCanary.exe", "DiscordPTB.exe",
    };

    public static bool IsDiscordExecutable(string fileName) =>
        ExeNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every "...\app-*\" folder that actually contains a Discord executable, across all variants.</summary>
    public static List<string> FindDiscordDirs()
    {
        var result = new List<string>();

        foreach (var baseDir in FindDiscordBaseDirs())
        {
            if (!Directory.Exists(baseDir))
                continue;

            foreach (var subfolder in Directory.GetDirectories(baseDir, "app-*", SearchOption.TopDirectoryOnly))
            {
                var dir = EnsureTrailingSlash(subfolder);
                if (ExeNames.Any(exe => File.Exists(Path.Combine(dir, exe))))
                    result.Add(dir);
            }
        }

        return result;
    }

    /// <summary>Among the found dirs, the ones where drover (version.dll) is actually installed.</summary>
    public static List<string> FindDroverInstalledDirs(IEnumerable<string> dirs) =>
        dirs.Where(d => File.Exists(Path.Combine(d, DllFileName))).ToList();

    public static string? GetExecutableIn(string dir) =>
        ExeNames.Select(exe => Path.Combine(dir, exe)).FirstOrDefault(File.Exists);

    public static bool IsAnyDiscordRunning() =>
        AppNames.Any(name => Process.GetProcessesByName(name).Length > 0);

    public static IEnumerable<Process> GetRunningDiscordProcesses() =>
        AppNames.SelectMany(name => Process.GetProcessesByName(name));

    private static List<string> FindDiscordBaseDirs()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in AppNames)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{app}");
                var installLocation = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    var dir = EnsureTrailingSlash(installLocation);
                    if (Directory.Exists(dir))
                        dirs.Add(dir);
                }
            }
            catch
            {
                // Registry access can fail for all sorts of harmless reasons; just skip this source.
            }
        }

        try
        {
            using var shellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Discord\shell\open\command");
            var command = shellKey?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(command))
            {
                var match = Regex.Match(command, @"\A""(.+\\)app-");
                if (match.Success)
                {
                    var dir = match.Groups[1].Value;
                    if (Directory.Exists(dir))
                        dirs.Add(dir);
                }
            }
        }
        catch
        {
            // ignore
        }

        return dirs.ToList();
    }

    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
