using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using DroverSwitch.Services;
using QRCoder;
using Wpf.Ui.Appearance;

namespace DroverSwitch.Views;

/// <summary>QR with the LAN tg://proxy link, for a phone on the same Wi-Fi.</summary>
public partial class TgQrDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly Func<string, string> _linkFor;
    private readonly Action<string> _picked;
    private string _link = "";

    /// <param name="candidates">LAN addresses of this PC, best first (several when there are several networks).</param>
    /// <param name="selected">The address to show first.</param>
    /// <param name="linkFor">tg://proxy link for an address.</param>
    /// <param name="picked">Called when the user picks another address (remembered for next time).</param>
    public TgQrDialog(IReadOnlyList<TgWsService.LanCandidate> candidates, string selected,
        Func<string, string> linkFor, Action<string> picked)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        _linkFor = linkFor;
        _picked = picked;
        if (candidates.Count > 1)
        {
            IpPanel.Visibility = Visibility.Visible;
            IpBox.ItemsSource = candidates;
            IpBox.SelectedItem = candidates.FirstOrDefault(c => c.Address == selected) ?? candidates[0];
        }
        Render(selected);
    }

    private void OnIpChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || IpBox.SelectedItem is not TgWsService.LanCandidate c)
            return;
        Render(c.Address);
        _picked(c.Address);
    }

    private void Render(string ip)
    {
        _link = _linkFor(ip);
        LinkBox.Text = _link;
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(_link, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        QrImage.Source = img;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_link); } catch { /* clipboard busy - user can select the text */ }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
