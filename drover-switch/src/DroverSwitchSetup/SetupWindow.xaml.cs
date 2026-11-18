using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Appearance;

namespace DroverSwitchSetup;

/// <summary>
/// Installs/updates/launches DroverSwitch. Three cases on every run:
///   1. Not installed yet -> extract the embedded exe to %AppData%\DroverSwitch and launch it.
///   2. Installed but older than the version embedded in this Setup build -> close the running
///      copy (if any, after confirming - see ShowConfirmButtons), overwrite it, relaunch.
///   3. Installed and already current -> just launch it (or say "already running" if it is).
/// Uninstalling is deliberately NOT handled here - DroverSwitch's own tray menu / popup already has
/// a fully working, tested uninstall (including cleaning up drover.ini/version.dll from Discord's
/// folder and its own settings); duplicating that into a second executable that would need to
/// delete itself mid-run was not worth the extra failure modes for a feature that already works.
/// </summary>
public partial class SetupWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    // Must match the Mutex name DroverSwitch.App.xaml.cs itself creates - this is how Setup tells
    // whether an installed copy is currently running, without needing any IPC of its own.
    private const string SingleInstanceMutexName = "DroverSwitch-SingleInstance";
    private const string EmbeddedExeResourceName = "DroverSwitchSetup.Resources.DroverSwitch.exe";

    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DroverSwitch");
    private static readonly string InstalledExePath = Path.Combine(InstallDir, "DroverSwitch.exe");

    // A plain text marker, not FileVersionInfo off the installed exe - reading the native Win32
    // VERSIONINFO resource back out of a published single-file bundle turned out unreliable in
    // practice (every run reported an update as available, even onto itself). This file is written
    // right after every successful install/update, so it always reflects what's actually on disk.
    private static readonly string VersionMarkerPath = Path.Combine(InstallDir, "version.txt");

    private TaskCompletionSource<bool>? _confirmTcs;

    public SetupWindow()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        DataContext = this;
    }

    private bool _isBusy = true;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    private string _statusText = "Подготовка...";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private bool _showConfirmButtons;
    public bool ShowConfirmButtons
    {
        get => _showConfirmButtons;
        set { _showConfirmButtons = value; OnPropertyChanged(); }
    }

    public async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            var bundledVersion = Assembly.GetExecutingAssembly().GetName().Version;

            if (!File.Exists(InstalledExePath))
            {
                StatusText = "Устанавливаю Discord Drover...";
                await ExtractEmbeddedExeAsync();
                WriteVersionMarker(bundledVersion);
                LaunchInstalled();
                Close();
                return;
            }

            var installedVersion = ReadVersionMarker();

            if (bundledVersion is not null && (installedVersion is null || bundledVersion > installedVersion))
            {
                // --auto-update means DroverSwitch itself launched this Setup after the user already
                // confirmed inside the app (the update banner's own restart prompt) - asking again
                // here would just be a dialog nobody is around to click, since DroverSwitch is about
                // to close itself regardless.
                // A running DroverSwitch (and the tg-ws-proxy.exe it started) keeps DroverSwitch.exe
                // locked - always close it right here, without asking: the update can't be applied
                // otherwise, and it is relaunched automatically a moment later anyway.
                StatusText = $"Обновляю Discord Drover до {bundledVersion}...";
                await TerminateRunningInstanceAsync();
                await ExtractEmbeddedExeAsync();
                WriteVersionMarker(bundledVersion);
                LaunchInstalled();
                Close();
                return;
            }

            if (IsAlreadyRunning())
            {
                IsBusy = false;
                StatusText = "Discord Drover уже запущен — смотрите значок в трее.";
                await Task.Delay(2000);
            }
            else
            {
                StatusText = "Запускаю Discord Drover...";
                LaunchInstalled();
            }

            Close();
        }
        catch (Exception ex)
        {
            IsBusy = false;
            StatusText = $"Ошибка установки: {ex.Message}";
            await Task.Delay(4000);
            Close();
        }
    }

    private Task<bool> AskConfirmationAsync()
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        ShowConfirmButtons = true;
        return _confirmTcs.Task;
    }

    private void OnConfirmUpdateClick(object sender, RoutedEventArgs e)
    {
        ShowConfirmButtons = false;
        _confirmTcs?.TrySetResult(true);
    }

    private void OnCancelUpdateClick(object sender, RoutedEventArgs e)
    {
        ShowConfirmButtons = false;
        _confirmTcs?.TrySetResult(false);
    }

    private static async Task ExtractEmbeddedExeAsync()
    {
        using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedExeResourceName)
            ?? throw new InvalidOperationException("Встроенная копия DroverSwitch.exe не найдена в этой сборке Setup.");

        // A just-killed process can hold the file handle open for a brief moment after Kill()
        // returns - retry instead of failing the whole install over a transient race.
        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                resourceStream.Position = 0;
                await using var file = File.Create(InstalledExePath);
                await resourceStream.CopyToAsync(file);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(500);
            }
        }
    }

    private static void WriteVersionMarker(Version? version)
    {
        try { File.WriteAllText(VersionMarkerPath, version?.ToString() ?? ""); }
        catch { /* best-effort - worst case the next run treats this as an unknown/older version */ }
    }

    private static Version? ReadVersionMarker()
    {
        try
        {
            return File.Exists(VersionMarkerPath) && Version.TryParse(File.ReadAllText(VersionMarkerPath).Trim(), out var v)
                ? v
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void LaunchInstalled()
    {
        Process.Start(new ProcessStartInfo(InstalledExePath) { UseShellExecute = true });
    }

    private static bool IsAlreadyRunning()
    {
        var found = Mutex.TryOpenExisting(SingleInstanceMutexName, out var mutex);
        mutex?.Dispose();
        return found;
    }

    private static bool IsAutoUpdateLaunch() =>
        Environment.GetCommandLineArgs().Any(a => a.Equals("--auto-update", StringComparison.OrdinalIgnoreCase));

    /// <summary>Closes every running DroverSwitch - matched by process name AND by the installed exe
    /// path (a renamed or older copy started from the install folder still locks it) - plus any
    /// helper process started from the install folder (tg-ws-proxy.exe), then waits until the files
    /// are really free.</summary>
    private static async Task TerminateRunningInstanceAsync()
    {
        static bool FromInstallDir(Process p)
        {
            try
            {
                var path = p.MainModule?.FileName;
                return path is not null && path.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        var targets = Process.GetProcessesByName("DroverSwitch")
            .Concat(Process.GetProcessesByName("tg-ws-proxy"))
            .Concat(Process.GetProcesses().Where(FromInstallDir))
            .GroupBy(p => p.Id).Select(g => g.First())
            .Where(p => p.Id != Environment.ProcessId)
            .ToList();

        foreach (var proc in targets)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch
            {
                // Best-effort - if it's already gone or inaccessible there's nothing more to do.
            }
        }

        // The single-instance mutex disappears with the process; give Windows a moment to release
        // the exe's file handle before extraction overwrites it (ExtractEmbeddedExeAsync retries too).
        for (var i = 0; i < 20 && IsAlreadyRunning(); i++)
            await Task.Delay(250);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
