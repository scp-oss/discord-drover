using System.Windows;
using DroverSwitch.Models;
using DroverSwitch.ViewModels;
using Wpf.Ui.Appearance;

namespace DroverSwitch.Views;

public partial class TrayPopup : Wpf.Ui.Controls.FluentWindow
{
    private readonly TrayViewModel _viewModel;

    public TrayPopup(TrayViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        ApplicationThemeManager.Apply(this);
        _viewModel.EditProfileRequested += OnEditProfileRequested;
    }

    /// <summary>Shows the popup anchored near the system tray, or hides it if it's already open.</summary>
    public void ToggleNearTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        var workArea = SystemParameters.WorkArea;
        UpdateLayout();
        Left = workArea.Right - ActualWidth - 12;
        Top = workArea.Bottom - ActualHeight - 12;

        Show();
        Activate();
    }

    private void TrayPopup_Deactivated(object sender, EventArgs e) => Hide();

    private void OnEditProfileRequested(ProxyProfile? existing)
    {
        var dialog = new ProfileEditDialog(existing) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _viewModel.AddOrUpdateProfile(dialog.Result, dialog.OriginalName);
        }
    }
}
