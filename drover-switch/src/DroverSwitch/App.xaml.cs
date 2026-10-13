using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DroverSwitch.Models;
using DroverSwitch.Services;
using DroverSwitch.ViewModels;
using DroverSwitch.Views;
using H.NotifyIcon;
using Wpf.Ui.Appearance;

namespace DroverSwitch;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private TrayViewModel? _viewModel;
    private TrayPopup? _popup;
    private Mutex? _singleInstanceMutex;

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DroverSwitch", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No "Global\" prefix on purpose: this should block a second copy for the same Windows
        // user session (double-click the exe again, or autostart + a manual launch), not stop a
        // different user on a shared machine from running their own.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "DroverSwitch-SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Discord Drover уже запущен — ищите значок в системном трее.",
                "Discord Drover",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // A tray app has no console, so an unhandled exception otherwise just vanishes the
        // process with no visible sign of what happened - log and show it instead.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash(args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportCrash(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };

        try
        {
            StartTrayApp();
        }
        catch (Exception ex)
        {
            ReportCrash(ex, "OnStartup");
        }
    }

    private void StartTrayApp()
    {
        ApplicationThemeManager.ApplySystemTheme();

        _viewModel = new TrayViewModel();
        _popup = new TrayPopup(_viewModel);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Discord Drover — переключатель прокси",
            IconSource = TrayIconFactory.GetDot(ProfileStatus.Unknown),
        };
        _trayIcon.ForceCreate();
        _trayIcon.TrayLeftMouseUp += (_, _) => _popup?.ToggleNearTray();

        // Right-click gives quick access to Exit and the full uninstall without first opening the
        // popup - left-click still opens the popup for everything else (switching, adding, etc.).
        _trayIcon.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Удалить Discord Drover", Command = _viewModel.UninstallCommand },
                new Separator(),
                new MenuItem { Header = "Выход", Command = _viewModel.ExitCommand },
            },
        };

        _viewModel.StatusChanged += status =>
            Dispatcher.Invoke(() => _trayIcon!.IconSource = TrayIconFactory.GetDot(status));

        _viewModel.NotificationRequested += message =>
            Dispatcher.Invoke(() => _trayIcon!.ShowNotification("Discord Drover", message));

        _viewModel.Start();
    }

    private static void ReportCrash(Exception? ex, string source)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, text);
        }
        catch
        {
            // If we can't even write the log, the MessageBox below is the only record left.
        }

        MessageBox.Show(
            $"DroverSwitch столкнулся с ошибкой ({source}):\n\n{ex?.Message}\n\nПодробности записаны в:\n{CrashLogPath}",
            "DroverSwitch - ошибка",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _viewModel?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* never acquired (e.g. the duplicate-instance exit path) */ }
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
