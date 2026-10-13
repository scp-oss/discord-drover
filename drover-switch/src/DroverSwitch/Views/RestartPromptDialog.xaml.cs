using System.Windows;
using Wpf.Ui.Appearance;

namespace DroverSwitch.Views;

public partial class RestartPromptDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>True if the user clicked "ОК" (restart now); false on "Закрыть".</summary>
    public bool Result { get; private set; }

    public RestartPromptDialog()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        DialogResult = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
        Close();
    }
}
