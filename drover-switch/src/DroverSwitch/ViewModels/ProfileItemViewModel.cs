using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DroverSwitch.Models;
using DroverSwitch.Services;

namespace DroverSwitch.ViewModels;

public class ProfileItemViewModel : INotifyPropertyChanged
{
    public ProxyProfile Model { get; }

    public ProfileItemViewModel(ProxyProfile model)
    {
        Model = model;
    }

    public string Name => Model.Name;
    public string ProxyUrl => Model.ProxyUrl;

    public string MaskedUrl => ParsedProxy.Parse(Model.ProxyUrl).ToDisplayString();

    private ProfileStatus _status = ProfileStatus.Unknown;
    public ProfileStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public Brush StatusBrush => Status switch
    {
        ProfileStatus.Online => Brushes.MediumSeaGreen,
        ProfileStatus.Offline => Brushes.IndianRed,
        ProfileStatus.Checking => Brushes.Goldenrod,
        _ => Brushes.Gray,
    };

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
