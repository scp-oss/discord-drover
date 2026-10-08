using System.IO;

namespace DroverSwitch.Services;

/// <summary>
/// Auto-installs drover's version.dll into a Discord folder the first time DroverSwitch runs
/// there, the same way drover.exe's own installer does (installer/Main.pas: btnInstallClick copies
/// DLL_FILENAME into every discovered app-* dir) - so the user never has to run that installer by
/// hand first. The DLL is bundled as an embedded resource at build time, fetched straight from the
/// upstream hdrover/discord-drover release (see the GitHub Actions workflow); DroverSwitch never
/// modifies drover's own source, it just ships the same binary drover.exe would have installed.
/// </summary>
public static class DroverInstaller
{
    private const string ResourceName = "DroverSwitch.Resources.version.dll";

    /// <summary>True if version.dll ends up present in discordDir - already there, or just installed.
    /// Only ever creates a missing file, never touches an existing one, so it's safe to call
    /// regardless of whether Discord is currently running.</summary>
    public static bool EnsureInstalled(string discordDir)
    {
        var dllPath = Path.Combine(discordDir, DiscordLocator.DllFileName);
        if (File.Exists(dllPath))
            return true;

        return WriteFromResource(dllPath);
    }

    /// <summary>Deletes version.dll if present. Discord locks the DLL for as long as it's running,
    /// so this (and <see cref="Reinstall"/>) must only be called while Discord is confirmed closed -
    /// callers check DiscordLocator.IsAnyDiscordRunning() first.</summary>
    public static bool Uninstall(string discordDir)
    {
        var dllPath = Path.Combine(discordDir, DiscordLocator.DllFileName);
        try
        {
            if (File.Exists(dllPath))
                File.Delete(dllPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Delete-then-recreate, so a switch always leaves behind a known-fresh copy of the DLL
    /// rather than assuming whatever was already there was still intact. Discord-closed only, same
    /// reason as <see cref="Uninstall"/>.</summary>
    public static bool Reinstall(string discordDir)
    {
        Uninstall(discordDir);
        return WriteFromResource(Path.Combine(discordDir, DiscordLocator.DllFileName));
    }

    private static bool WriteFromResource(string dllPath)
    {
        using var resourceStream = typeof(DroverInstaller).Assembly.GetManifestResourceStream(ResourceName);
        if (resourceStream is null)
            return false; // local/dev build without the fetched DLL - nothing to install from.

        try
        {
            using var file = File.Create(dllPath);
            resourceStream.CopyTo(file);
            return true;
        }
        catch
        {
            return false; // e.g. permissions - caller just treats this dir as not installed.
        }
    }
}
