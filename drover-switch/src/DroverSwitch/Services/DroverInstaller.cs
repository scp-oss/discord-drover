using System.IO;

namespace DroverSwitch.Services;

/// <summary>
/// Auto-installs drover's version.dll (and a default drover-packet.bin) into a Discord folder the
/// first time DroverSwitch runs there, the same way drover.exe's own installer does
/// (installer/Main.pas: btnInstallClick copies DLL_FILENAME plus GetExtraFilenames into every
/// discovered app-* dir) - so the user never has to run that installer by hand first. Both files
/// are bundled as embedded resources at build time, fetched straight from the upstream
/// hdrover/discord-drover release (see the GitHub Actions workflow); DroverSwitch never modifies
/// drover's own source, it just ships the same binaries drover.exe would have installed.
/// </summary>
public static class DroverInstaller
{
    private const string DllResourceName = "DroverSwitch.Resources.version.dll";
    private const string PacketResourceName = "DroverSwitch.Resources.drover-packet.bin";

    /// <summary>True if version.dll ends up present in discordDir - already there, or just installed.
    /// Only ever creates files that are missing, never touches an existing one, so it's safe to call
    /// regardless of whether Discord is currently running.
    ///
    /// drover-packet.bin is seeded here too, but only when entirely absent: per drover's own README
    /// it's meant to be hand-edited/replaced by the user to work around voice blocking on their
    /// network ("re-read before every connection, no restart needed") - unlike version.dll, a
    /// switch must never overwrite it, or it would silently discard that customization.</summary>
    public static bool EnsureInstalled(string discordDir)
    {
        var dllPath = Path.Combine(discordDir, DiscordLocator.DllFileName);
        if (!File.Exists(dllPath))
            WriteFromResource(DllResourceName, dllPath);

        var packetPath = Path.Combine(discordDir, DiscordLocator.PacketFileName);
        if (!File.Exists(packetPath))
            WriteFromResource(PacketResourceName, packetPath);

        return File.Exists(dllPath);
    }

    /// <summary>Deletes version.dll if present. Discord locks the DLL for as long as it's running,
    /// so this (and <see cref="Reinstall"/>) must only be called while Discord is confirmed closed -
    /// callers check DiscordLocator.IsAnyDiscordRunning() first. Never touches drover-packet.bin.</summary>
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
    /// reason as <see cref="Uninstall"/>. Never touches drover-packet.bin.</summary>
    public static bool Reinstall(string discordDir)
    {
        Uninstall(discordDir);
        return WriteFromResource(DllResourceName, Path.Combine(discordDir, DiscordLocator.DllFileName));
    }

    private static bool WriteFromResource(string resourceName, string destPath)
    {
        using var resourceStream = typeof(DroverInstaller).Assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
            return false; // local/dev build without the fetched files - nothing to install from.

        try
        {
            using var file = File.Create(destPath);
            resourceStream.CopyTo(file);
            return true;
        }
        catch
        {
            return false; // e.g. permissions - caller just treats this dir as not installed.
        }
    }
}
