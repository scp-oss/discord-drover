using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DroverSwitch.Services;

/// <summary>Registers/unregisters DroverSwitch in the per-user "Run" autostart key. No admin rights
/// needed - HKCU, not HKLM.</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DroverSwitch";

    /// <summary>Command-line flag written into the Run key so the app can tell "Windows started me"
    /// apart from a manual double-click - see <see cref="IsAutostartLaunch"/>.</summary>
    public const string AutostartArg = "--autostart";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
            return;

        if (enabled)
        {
            // Assembly.Location is always "" for a single-file-published app (the DLL lives
            // embedded in the exe, not on disk as its own file) - MainModule.FileName is the one
            // that reliably gives the actual running exe's path in that scenario. AppContext.
            // BaseDirectory + the known exe name is the fallback if MainModule is ever unavailable.
            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "DroverSwitch.exe");
            key.SetValue(ValueName, $"\"{exePath}\" {AutostartArg}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static bool IsAutostartLaunch(string[] commandLineArgs) =>
        Array.Exists(commandLineArgs, a => a.Equals(AutostartArg, StringComparison.OrdinalIgnoreCase));
}
