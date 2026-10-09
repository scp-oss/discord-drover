using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace DroverSwitch.Services;

/// <summary>Registers/unregisters DroverSwitch in the per-user "Run" autostart key. No admin rights
/// needed - HKCU, not HKLM.</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DroverSwitch";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
            return;

        if (enabled)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Assembly.GetExecutingAssembly().Location;
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
