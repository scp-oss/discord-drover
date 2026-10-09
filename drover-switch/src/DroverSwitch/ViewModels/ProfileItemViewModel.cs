using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DroverSwitch.Models;
using DroverSwitch.Services;

namespace DroverSwitch.ViewModels;

public class ProfileItemViewModel : INotifyPropertyChanged
{
    public ProxyProfile Model { get; }

    /// <summary>Edit/Remove live here instead of being reached via ElementName from the row's
    /// ContextMenu - a ContextMenu's DataContext does inherit from its PlacementTarget across the
    /// Popup boundary, but an ElementName lookup for a window-level name did not resolve reliably
    /// from inside it (confirmed: both menu items were simply inert). Binding straight to a command
    /// that already lives on this item's own DataContext sidesteps that entirely.</summary>
    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public ProfileItemViewModel(ProxyProfile model, Action<ProfileItemViewModel> onEdit, Action<ProfileItemViewModel> onRemove)
    {
        Model = model;
        EditCommand = new RelayCommand(_ => onEdit(this));
        RemoveCommand = new RelayCommand(_ => onRemove(this));
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
            OnPropertyChanged(nameof(LatencyText));
        }
    }

    private long? _latencyMs;
    public long? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (_latencyMs == value)
                return;
            _latencyMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyText));
        }
    }

    /// <summary>What the popup shows instead of a plain status dot - the actual round-trip to
    /// discord.com when reachable, so you see how good the proxy is, not just that it's up.</summary>
    public string LatencyText => Status switch
    {
        ProfileStatus.Online => $"{LatencyMs ?? 0} мс",
        ProfileStatus.Offline => "N/A",
        ProfileStatus.Checking => "проверка…",
        _ => "—",
    };

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
