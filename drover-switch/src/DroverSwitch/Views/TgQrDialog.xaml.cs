using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using Wpf.Ui.Appearance;

namespace DroverSwitch.Views;

/// <summary>QR with the LAN tg://proxy link, for a phone on the same Wi-Fi.</summary>
public partial class TgQrDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _link;

    public TgQrDialog(string link)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        _link = link;
        LinkBox.Text = link;
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
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
