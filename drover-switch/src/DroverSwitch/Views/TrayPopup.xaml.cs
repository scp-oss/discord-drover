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
        _viewModel.AddProfilesRequested += OnAddProfilesRequested;
        _viewModel.RestartPromptRequested += OnRestartPromptRequested;
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

        // ActualHeight is only trustworthy once the window has actually gone through a real layout
        // pass - before the very first Show() of a session, SizeToContent hasn't done that yet, so
        // it reads 0 here and this would park the window with its top edge pinned to the very
        // bottom pixel of the screen (almost the whole window rendered off-screen, which is exactly
        // "appears somewhere far in the bottom-right corner" on a user's very first open). Position
        // once with the best guess available, then reposition for real right after Show() once the
        // size is actually known.
        Left = workArea.Right - ActualWidth - 12;
        Top = workArea.Bottom - ActualHeight - 12;

        Show();

        Left = workArea.Right - ActualWidth - 12;
        Top = workArea.Bottom - ActualHeight - 12;

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

    private void OnAddProfilesRequested()
    {
        var dialog = new BulkAddDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result.Count > 0)
        {
            _viewModel.AddProfiles(dialog.Result);
        }
    }

    private void OnRestartPromptRequested()
    {
        var dialog = new RestartPromptDialog { Owner = this };
        dialog.ShowDialog();
        if (dialog.Result)
        {
            _viewModel.ConfirmRestartAfterSwitch();
        }
    }
}
