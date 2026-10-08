using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using DroverSwitch.Models;
using DroverSwitch.Services;

namespace DroverSwitch.ViewModels;

/// <summary>
/// Owns the profile list, the periodic health-check/auto-switch loop, and every tray-popup command.
/// A couple of spots call into MessageBox directly instead of going through yet another indirection
/// layer - acceptable for an app this size, not worth a dialog-service abstraction.
/// </summary>
public class TrayViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private int _consecutiveActiveFailures;
    private DateTime _lastAllOfflineNotification = DateTime.MinValue;
    private DateTime _lastMirrorWriteUtc = DateTime.MinValue;
    private bool _isChecking;

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();

    public event Action<ProfileStatus>? StatusChanged;
    public event Action<string>? NotificationRequested;
    public event Action<ProxyProfile?>? EditProfileRequested;

    public RelayCommand ActivateProfileCommand { get; }
    public RelayCommand AddProfileCommand { get; }
    public RelayCommand EditProfileCommand { get; }
    public RelayCommand RemoveProfileCommand { get; }
    public RelayCommand RefreshNowCommand { get; }
    public RelayCommand RestartDiscordCommand { get; }
    public RelayCommand ExitCommand { get; }

    public TrayViewModel()
    {
        _settings = SettingsStore.Load();
        ImportFromCompanionFileIfPresent();
        RebuildProfilesCollection();
        SaveAndMirror();

        ActivateProfileCommand = new RelayCommand(p =>
        {
            if (p is ProfileItemViewModel item)
                ActivateInternal(item, notifyAuto: false);
        });
        AddProfileCommand = new RelayCommand(_ => EditProfileRequested?.Invoke(null));
        EditProfileCommand = new RelayCommand(p =>
        {
            if (p is ProfileItemViewModel item)
                EditProfileRequested?.Invoke(item.Model);
        });
        RemoveProfileCommand = new RelayCommand(p =>
        {
            if (p is ProfileItemViewModel item)
                RemoveProfile(item);
        });
        RefreshNowCommand = new RelayCommand(async _ => await RunCheckCycleAsync());
        RestartDiscordCommand = new RelayCommand(_ => RestartDiscord());
        ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.CheckIntervalSeconds)),
        };
        _timer.Tick += async (_, _) => await RunCheckCycleAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = RunCheckCycleAsync();
    }

    public bool AutoModeEnabled
    {
        get => _settings.AutoModeEnabled;
        set
        {
            if (_settings.AutoModeEnabled == value)
                return;
            _settings.AutoModeEnabled = value;
            _consecutiveActiveFailures = 0;
            SaveAndMirror();
            OnPropertyChanged();
        }
    }

    private bool _isDiscordRunning;
    public bool IsDiscordRunning
    {
        get => _isDiscordRunning;
        private set
        {
            if (_isDiscordRunning == value)
                return;
            _isDiscordRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusHint));
        }
    }

    public string StatusHint => IsDiscordRunning
        ? "Discord запущен — новый прокси применится после перезапуска."
        : "Изменения применяются при следующем запуске Discord.";

    /// <summary>Called by the dialog. <paramref name="originalName"/> is null when adding a brand-new profile.</summary>
    public void AddOrUpdateProfile(ProxyProfile profile, string? originalName)
    {
        var isRename = originalName is not null &&
                        !originalName.Equals(profile.Name, StringComparison.OrdinalIgnoreCase);
        var nameTaken = _settings.Profiles.Any(p =>
            p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase) &&
            !p.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));

        if ((originalName is null || isRename) && nameTaken)
        {
            MessageBox.Show($"Конфиг с именем «{profile.Name}» уже существует.", "Discord Drover",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (originalName is null)
        {
            _settings.Profiles.Add(profile);
        }
        else
        {
            var existing = _settings.Profiles.FirstOrDefault(p =>
                p.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Name = profile.Name;
                existing.ProxyUrl = profile.ProxyUrl;
            }

            if (_settings.ActiveProfileName?.Equals(originalName, StringComparison.OrdinalIgnoreCase) == true)
                _settings.ActiveProfileName = profile.Name;
        }

        RebuildProfilesCollection();
        SaveAndMirror();
        _ = RunCheckCycleAsync();
    }

    private void RemoveProfile(ProfileItemViewModel item)
    {
        _settings.Profiles.RemoveAll(p => p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        if (_settings.ActiveProfileName?.Equals(item.Name, StringComparison.OrdinalIgnoreCase) == true)
            _settings.ActiveProfileName = null;

        RebuildProfilesCollection();
        SaveAndMirror();
    }

    private void RestartDiscord()
    {
        var running = DiscordLocator.GetRunningDiscordProcesses().ToList();
        if (running.Count == 0)
            return;

        var confirm = MessageBox.Show(
            "Discord будет закрыт и перезапущен, чтобы применить новый прокси. Продолжить?",
            "Discord Drover",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        string? exePath;
        try { exePath = running[0].MainModule?.FileName; }
        catch { exePath = null; }

        foreach (var proc in running)
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(3000))
                    proc.Kill();
            }
            catch
            {
                // Best-effort: a process that's already gone or inaccessible just gets skipped.
            }
        }

        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            Task.Delay(1000).ContinueWith(_ =>
            {
                try { System.Diagnostics.Process.Start(exePath); }
                catch { /* user can relaunch Discord themselves if this fails */ }
            });
        }
    }

    private void ActivateInternal(ProfileItemViewModel item, bool notifyAuto)
    {
        var installedDirs = DiscordLocator.FindDroverInstalledDirs(DiscordLocator.FindDiscordDirs());

        if (installedDirs.Count == 0)
        {
            MessageBox.Show(
                "Не найдена папка Discord с установленным Discord Drover (version.dll). " +
                "Сначала установите Drover через drover.exe.",
                "Discord Drover",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DroverIniService.WriteProxyToAllDirs(installedDirs, item.ProxyUrl);

        _settings.ActiveProfileName = item.Name;
        SaveAndMirror(installedDirs);

        foreach (var p in Profiles)
            p.IsActive = p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase);

        if (notifyAuto)
            NotificationRequested?.Invoke($"Автоматически переключено на «{item.Name}».");
    }

    private async Task RunCheckCycleAsync()
    {
        if (_isChecking)
            return;
        _isChecking = true;

        try
        {
            ReloadCompanionFileIfChangedByHand();

            IsDiscordRunning = DiscordLocator.IsAnyDiscordRunning();

            var snapshot = Profiles.ToList();
            foreach (var p in snapshot)
                p.Status = ProfileStatus.Checking;

            var results = await Task.WhenAll(
                snapshot.Select(p => ProxyHealthChecker.IsReachableAsync(p.ProxyUrl)));

            for (var i = 0; i < snapshot.Count; i++)
                snapshot[i].Status = results[i] ? ProfileStatus.Online : ProfileStatus.Offline;

            var active = snapshot.FirstOrDefault(p =>
                p.Name.Equals(_settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
            StatusChanged?.Invoke(active?.Status ?? ProfileStatus.Unknown);

            if (!AutoModeEnabled || active is null)
            {
                _consecutiveActiveFailures = 0;
                return;
            }

            var activeIndex = snapshot.IndexOf(active);
            if (results[activeIndex])
            {
                _consecutiveActiveFailures = 0;
                return;
            }

            // Require two consecutive failed checks before failing over, so one flaky probe doesn't flap it.
            _consecutiveActiveFailures++;
            if (_consecutiveActiveFailures < 2)
                return;

            var candidate = Enumerable.Range(0, snapshot.Count)
                .Where(i => !snapshot[i].Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase) && results[i])
                .Select(i => snapshot[i])
                .FirstOrDefault();

            if (candidate is not null)
            {
                _consecutiveActiveFailures = 0;
                ActivateInternal(candidate, notifyAuto: true);
            }
            else if (DateTime.UtcNow - _lastAllOfflineNotification > TimeSpan.FromMinutes(5))
            {
                _lastAllOfflineNotification = DateTime.UtcNow;
                NotificationRequested?.Invoke("Все прокси недоступны.");
            }
        }
        finally
        {
            _isChecking = false;
        }
    }

    /// <summary>
    /// drover-switch.ini is meant to be hand-editable, same spirit as drover.ini/drover-packet.bin.
    /// If its on-disk timestamp moved since we last wrote it ourselves, someone edited it by hand -
    /// reload from it. Best-effort: only the first installed dir found is watched.
    /// </summary>
    private void ReloadCompanionFileIfChangedByHand()
    {
        var dir = DiscordLocator.FindDroverInstalledDirs(DiscordLocator.FindDiscordDirs()).FirstOrDefault();
        if (dir is null)
            return;

        var pool = ProfilePoolFile.TryRead(dir);
        if (pool is null)
            return;

        if (pool.WrittenAtUtc <= _lastMirrorWriteUtc.AddSeconds(1))
            return; // this is (very likely) a file we wrote ourselves, not a manual edit.

        _settings.Profiles = pool.Profiles;
        _settings.ActiveProfileName = pool.ActiveName;
        RebuildProfilesCollection();
        SettingsStore.Save(_settings);
    }

    private void ImportFromCompanionFileIfPresent()
    {
        var installedDirs = DiscordLocator.FindDroverInstalledDirs(DiscordLocator.FindDiscordDirs());

        foreach (var dir in installedDirs)
        {
            var pool = ProfilePoolFile.TryRead(dir);
            if (pool is null || pool.Profiles.Count == 0)
                continue;

            _settings.Profiles = pool.Profiles;
            _settings.ActiveProfileName = pool.ActiveName;
            return;
        }

        // First run: no drover-switch.ini yet anywhere, so we're still sitting on the
        // CreateDefault() seed ("Прямое соединение"). If drover.ini already has a proxy
        // configured - set up by hand before the switcher existed - import that as the active
        // profile instead of silently hiding what's actually in effect.
        foreach (var dir in installedDirs)
        {
            var existingProxy = DroverIniService.ReadProxy(dir);
            if (string.IsNullOrWhiteSpace(existingProxy))
                continue;

            var imported = new ProxyProfile { Name = "Текущий", ProxyUrl = existingProxy };
            _settings.Profiles = new List<ProxyProfile> { imported };
            _settings.ActiveProfileName = imported.Name;
            return;
        }
    }

    private void RebuildProfilesCollection()
    {
        Profiles.Clear();
        foreach (var profile in _settings.Profiles)
        {
            Profiles.Add(new ProfileItemViewModel(profile)
            {
                IsActive = profile.Name.Equals(_settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private void SaveAndMirror(List<string>? installedDirs = null)
    {
        SettingsStore.Save(_settings);

        var dirs = installedDirs ?? DiscordLocator.FindDroverInstalledDirs(DiscordLocator.FindDiscordDirs());
        foreach (var dir in dirs)
            ProfilePoolFile.Write(dir, _settings.Profiles, _settings.ActiveProfileName);

        if (dirs.Count > 0)
            _lastMirrorWriteUtc = DateTime.UtcNow;
    }

    public void Dispose()
    {
        _timer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
