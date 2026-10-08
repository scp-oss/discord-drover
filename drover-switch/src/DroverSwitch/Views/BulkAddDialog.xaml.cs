using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DroverSwitch.Models;
using DroverSwitch.Services;
using Wpf.Ui.Appearance;

namespace DroverSwitch.Views;

public partial class BulkAddDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>The successfully parsed profiles when the user clicks "Добавить"; empty on cancel.</summary>
    public List<ProxyProfile> Result { get; private set; } = new();

    public BulkAddDialog()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var profiles = new List<ProxyProfile>();
        var badLines = new List<string>();

        foreach (var rawLine in ListBox.Text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // Optional "name url", separated by the first run of whitespace - if the first token
            // doesn't parse as a proxy on its own, treat the whole line as just the URL instead.
            string? name = null;
            var urlPart = line;
            var spaceIndex = line.IndexOfAny(new[] { ' ', '\t' });
            if (spaceIndex > 0)
            {
                var candidateUrl = line[(spaceIndex + 1)..].Trim();
                if (ParsedProxy.Parse(candidateUrl).IsSpecified)
                {
                    name = line[..spaceIndex].Trim();
                    urlPart = candidateUrl;
                }
            }

            var parsed = ParsedProxy.Parse(urlPart);
            if (!parsed.IsSpecified)
            {
                badLines.Add(line);
                continue;
            }

            name = string.IsNullOrWhiteSpace(name) ? $"{parsed.Host}:{parsed.Port}" : name;
            profiles.Add(new ProxyProfile { Name = name, ProxyUrl = urlPart });
        }

        if (profiles.Count == 0)
        {
            ShowError("Не найдено ни одной распознанной строки. Формат: [имя] protocol://host:port");
            return;
        }

        if (badLines.Count > 0)
        {
            var preview = string.Join("\n", badLines.Take(5));
            if (badLines.Count > 5)
                preview += "\n…";

            var proceed = MessageBox.Show(
                $"Будет добавлено {profiles.Count}. Не распознано {badLines.Count}:\n{preview}\n\nПродолжить?",
                "Discord Drover",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes)
                return;
        }

        Result = profiles;
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
