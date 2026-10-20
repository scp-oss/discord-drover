using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DroverSwitchSetup;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DroverSwitch", "setup-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No console here either - same reasoning as DroverSwitch's own App.xaml.cs: log and show
        // instead of letting the install/update step just silently vanish.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var window = new SetupWindow();
        MainWindow = window;
        window.Show();
        _ = window.RunAsync();
    }

    private static void ReportCrash(Exception? ex)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, text);
        }
        catch
        {
            // Nothing more useful to do if even the log can't be written.
        }

        MessageBox.Show(
            $"Установка Discord Drover столкнулась с ошибкой:\n\n{ex?.Message}\n\nПодробности: {CrashLogPath}",
            "Discord Drover Setup",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
