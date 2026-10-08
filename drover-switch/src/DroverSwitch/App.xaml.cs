using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        _viewModel.StatusChanged += status =>
            Dispatcher.Invoke(() => _trayIcon!.IconSource = TrayIconFactory.GetDot(status));

        _viewModel.NotificationRequested += message =>
            Dispatcher.Invoke(() => _trayIcon!.ShowNotification("Discord Drover", message));

        _viewModel.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _viewModel?.Dispose();
        base.OnExit(e);
    }
}
