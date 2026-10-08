using System.Windows;
using DroverSwitch.Models;
using DroverSwitch.Services;
using Wpf.Ui.Appearance;

namespace DroverSwitch.Views;

public partial class ProfileEditDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>Name the profile had when the dialog opened; null when adding a brand-new one.</summary>
    public string? OriginalName { get; }

    /// <summary>Set to the saved values when the user clicks "Сохранить"; left untouched on cancel.</summary>
    public ProxyProfile? Result { get; private set; }

    public ProfileEditDialog(ProxyProfile? existing)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        OriginalName = existing?.Name;

        NameBox.Text = existing?.Name ?? "";
        UrlBox.Text = existing?.ProxyUrl ?? "";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var url = UrlBox.Text.Trim();

        if (name.Length == 0)
        {
            ShowError("Укажите название конфига.");
            return;
        }

        if (url.Length > 0 && !ParsedProxy.Parse(url).IsSpecified)
        {
            ShowError("Не удалось разобрать адрес прокси. Проверьте формат.");
            return;
        }

        Result = new ProxyProfile { Name = name, ProxyUrl = url };

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
