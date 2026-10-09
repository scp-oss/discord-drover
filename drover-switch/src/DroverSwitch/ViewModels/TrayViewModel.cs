using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly DispatcherTimer _updateCheckTimer;
    private int _consecutiveActiveFailures;
    private DateTime _lastAllOfflineNotification = DateTime.MinValue;
    private DateTime _lastMirrorWriteUtc = DateTime.MinValue;
    private bool _droverJustAutoInstalled;
    private string? _latestReleaseUrl;

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();

    public event Action<ProfileStatus>? StatusChanged;
    public event Action<string>? NotificationRequested;
    public event Action<ProxyProfile?>? EditProfileRequested;
    public event Action? AddProfilesRequested;

    public RelayCommand ActivateProfileCommand { get; }
    public RelayCommand AddProfileCommand { get; }
    public RelayCommand RefreshNowCommand { get; }
    public RelayCommand StartDiscordCommand { get; }
    public RelayCommand CloseDiscordCommand { get; }
    public RelayCommand RestartDiscordCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand OpenReleasePageCommand { get; }
    public RelayCommand ExitCommand { get; }

    public TrayViewModel()
    {
        _settings = SettingsStore.Load();
        EnsureDroverInstalledEverywhere();
        ImportFromCompanionFileIfPresent();
        RebuildProfilesCollection();
        SaveAndMirror();

        ActivateProfileCommand = new RelayCommand(p =>
        {
            if (p is ProfileItemViewModel item)
                ActivateInternal(item, notifyAuto: false);
        });
        AddProfileCommand = new RelayCommand(_ => AddProfilesRequested?.Invoke());
        RefreshNowCommand = new RelayCommand(async _ => await RunCheckCycleAsync());
        StartDiscordCommand = new RelayCommand(_ => StartDiscord());
        CloseDiscordCommand = new RelayCommand(_ => CloseDiscord());
        RestartDiscordCommand = new RelayCommand(_ => RestartDiscord());
        UninstallCommand = new RelayCommand(_ => UninstallEverything());
        OpenReleasePageCommand = new RelayCommand(_ => OpenReleasePage());
        ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.CheckIntervalSeconds)),
        };
        _timer.Tick += async (_, _) => await RunCheckCycleAsync();

        // Separate, much less frequent timer - checking GitHub every 20s alongside the proxy
        // health loop would be pointless and could trip API rate limits on a long-running session.
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
    }

    public void Start()
    {
        if (_droverJustAutoInstalled)
            NotificationRequested?.Invoke("Discord Drover установлен автоматически.");

        _timer.Start();
        _ = RunCheckCycleAsync();

        _updateCheckTimer.Start();
        _ = CheckForUpdatesAsync();
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

    /// <summary>Off by default - only ever turned on if the user flips it themselves.</summary>
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
                return;
            _settings.StartWithWindows = value;
            AutostartService.SetEnabled(value);
            SaveAndMirror();
            OnPropertyChanged();
        }
    }

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (_isChecking == value)
                return;
            _isChecking = value;
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
            OnPropertyChanged(nameof(IsDiscordNotRunning));
            OnPropertyChanged(nameof(StatusHint));
            OnPropertyChanged(nameof(DiscordStatusText));
        }
    }

    /// <summary>Just the negation, for XAML visibility bindings - the popup shows either the single
    /// "Запустить" button or the "Перезапустить"/"Закрыть" pair, never both.</summary>
    public bool IsDiscordNotRunning => !IsDiscordRunning;

    public string StatusHint => IsDiscordRunning
        ? "Discord запущен — новый прокси применится после перезапуска."
        : "Изменения применяются при следующем запуске Discord.";

    public string DiscordStatusText => IsDiscordRunning ? "Discord сейчас запущен." : "Discord сейчас закрыт.";

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (_updateAvailable == value)
                return;
            _updateAvailable = value;
            OnPropertyChanged();
        }
    }

    private string? _latestVersionTag;
    public string? LatestVersionTag
    {
        get => _latestVersionTag;
        private set
        {
            if (_latestVersionTag == value)
                return;
            _latestVersionTag = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateBannerText));
        }
    }

    public string UpdateBannerText => $"Доступна новая версия: {LatestVersionTag}";

    /// <summary>Always-visible "which one is actually active" line - independent of whether the
    /// row highlighting itself is noticeable, so it's never ambiguous which proxy is in drover.ini.</summary>
    public string ActiveProfileDisplay => _settings.ActiveProfileName is { Length: > 0 } name
        ? $"Активен: {name}"
        : "Активный профиль не выбран";

    /// <summary>Presentation-only flag for the status dot next to <see cref="ActiveProfileDisplay"/> -
    /// doesn't change any stored state, just lets the UI color the dot without re-parsing the text.</summary>
    public bool HasActiveProfile => !string.IsNullOrEmpty(_settings.ActiveProfileName);

    private void SetActiveProfileName(string? name)
    {
        _settings.ActiveProfileName = name;
        OnPropertyChanged(nameof(ActiveProfileDisplay));
        OnPropertyChanged(nameof(HasActiveProfile));
    }

    /// <summary>Called by the bulk-add dialog, one or many at once. A name collision with an
    /// existing profile (or another entry in the same batch) gets auto-suffixed "(2)", "(3)", ...
    /// rather than rejecting the whole batch over one clash.</summary>
    public void AddProfiles(IEnumerable<ProxyProfile> profiles)
    {
        var addedAny = false;

        foreach (var profile in profiles)
        {
            var name = profile.Name;
            var suffix = 2;
            while (_settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = $"{profile.Name} ({suffix++})";

            _settings.Profiles.Add(new ProxyProfile { Name = name, ProxyUrl = profile.ProxyUrl });
            addedAny = true;
        }

        if (!addedAny)
            return;

        RebuildProfilesCollection();
        SaveAndMirror();
        _ = RunCheckCycleAsync();
    }

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
                SetActiveProfileName(profile.Name);
        }

        RebuildProfilesCollection();
        SaveAndMirror();
        _ = RunCheckCycleAsync();
    }

    private void RemoveProfile(ProfileItemViewModel item)
    {
        _settings.Profiles.RemoveAll(p => p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        if (_settings.ActiveProfileName?.Equals(item.Name, StringComparison.OrdinalIgnoreCase) == true)
            SetActiveProfileName(null);

        RebuildProfilesCollection();
        SaveAndMirror();
    }

    /// <summary>The Discord status card has one button whose action depends on current state -
    /// close it if it's running, launch it if it isn't - instead of a single "restart" action.</summary>
    private void CloseDiscord()
    {
        var running = DiscordLocator.GetRunningDiscordProcesses().ToList();
        if (running.Count == 0)
            return;

        var confirm = MessageBox.Show(
            "Закрыть Discord?",
            "Discord Drover",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        KillDiscordProcesses(running);

        // The periodic health-check loop would catch this within ~20s anyway, but updating right
        // away is what actually fixes "the button didn't change" - no reason to make the user wait
        // out a poll interval to see a state they just caused themselves.
        IsDiscordRunning = DiscordLocator.IsAnyDiscordRunning();
    }

    private void StartDiscord()
    {
        if (TryStartDiscord())
            IsDiscordRunning = DiscordLocator.IsAnyDiscordRunning();
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

        KillDiscordProcesses(running);
        IsDiscordRunning = DiscordLocator.IsAnyDiscordRunning();

        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            Task.Delay(1000).ContinueWith(_ =>
            {
                try { System.Diagnostics.Process.Start(exePath); }
                catch { /* user can relaunch Discord themselves if this fails */ }

                Application.Current?.Dispatcher.Invoke(() =>
                    IsDiscordRunning = DiscordLocator.IsAnyDiscordRunning());
            });
        }
    }

    private static void KillDiscordProcesses(IEnumerable<Process> processes)
    {
        foreach (var proc in processes)
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
    }

    private bool TryStartDiscord()
    {
        var exePath = DiscordLocator.FindDiscordDirs()
            .Select(DiscordLocator.GetExecutableIn)
            .FirstOrDefault(p => p is not null);

        if (exePath is null)
        {
            MessageBox.Show("Не найден Discord.exe.", "Discord Drover", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(exePath);
            return true;
        }
        catch
        {
            MessageBox.Show("Не удалось запустить Discord.", "Discord Drover", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckAsync();
        if (!result.HasUpdate)
            return;

        _latestReleaseUrl = result.ReleaseUrl;
        LatestVersionTag = result.Tag;

        var wasAlreadyKnown = UpdateAvailable;
        UpdateAvailable = true;

        if (!wasAlreadyKnown)
            NotificationRequested?.Invoke($"Доступна новая версия DroverSwitch: {result.Tag}. Нажмите, чтобы открыть релиз.");
    }

    private void OpenReleasePage()
    {
        if (_latestReleaseUrl is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_latestReleaseUrl)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Nothing more useful to do if the OS can't hand off a URL to a browser.
        }
    }

    /// <summary>Full uninstall: removes drover (version.dll, drover-packet.bin, drover.ini) and
    /// DroverSwitch's own companion file from every discovered Discord folder, plus this app's own
    /// saved settings. version.dll is locked while Discord runs, same as during a switch, so this
    /// needs Discord closed first - same reason, not a new rule.</summary>
    private void UninstallEverything()
    {
        if (DiscordLocator.IsAnyDiscordRunning())
        {
            MessageBox.Show(
                "Сначала закройте Discord — version.dll нельзя удалить, пока он запущен.",
                "Discord Drover",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            "Будут удалены drover.ini, version.dll, drover-packet.bin и drover-switch.ini из всех " +
            "найденных папок Discord, а также настройки самого DroverSwitch. Discord вернётся к " +
            "прямому соединению без прокси. Это необратимо. Продолжить?",
            "Удалить Discord Drover",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        foreach (var dir in DiscordLocator.FindDiscordDirs())
        {
            TryDelete(Path.Combine(dir, DiscordLocator.DllFileName));
            TryDelete(Path.Combine(dir, DiscordLocator.OptionsFileName));
            TryDelete(Path.Combine(dir, DiscordLocator.PacketFileName));
            TryDelete(Path.Combine(dir, ProfilePoolFile.FileName));
        }

        AutostartService.SetEnabled(false);
        SettingsStore.Delete();

        MessageBox.Show("Discord Drover удалён.", "Discord Drover", MessageBoxButton.OK, MessageBoxImage.Information);
        Application.Current.Shutdown();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup - nothing more we can usefully do about e.g. a permissions error here.
        }
    }

    private void ActivateInternal(ProfileItemViewModel item, bool notifyAuto)
    {
        var allDirs = DiscordLocator.FindDiscordDirs();
        if (allDirs.Count == 0)
        {
            MessageBox.Show(
                "Discord не найден на этом компьютере.",
                "Discord Drover",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // version.dll is locked by Discord for as long as it's running (Windows won't let it be
        // deleted or overwritten), and swapping it out from under a live process is exactly what
        // was hanging the client/voice before - only reinstall it when Discord is confirmed
        // closed. drover.ini itself is a plain file drover only reads once at its own startup, so
        // rewriting it is always safe and is what makes the switch "ready for next launch" even
        // while Discord is still open right now.
        var discordClosed = !DiscordLocator.IsAnyDiscordRunning();

        foreach (var dir in allDirs)
            DroverInstaller.EnsureInstalled(dir);

        if (discordClosed)
        {
            foreach (var dir in allDirs)
                DroverInstaller.Uninstall(dir);
        }

        DroverIniService.WriteProxyToAllDirs(allDirs, item.ProxyUrl);

        if (discordClosed)
        {
            foreach (var dir in allDirs)
                DroverInstaller.Reinstall(dir);
        }

        SetActiveProfileName(item.Name);
        SaveAndMirror(allDirs);

        foreach (var p in Profiles)
            p.IsActive = p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase);

        if (notifyAuto)
            NotificationRequested?.Invoke($"Автоматически переключено на «{item.Name}».");
    }

    /// <summary>Installs version.dll into every discovered Discord folder that doesn't have it yet -
    /// called once at startup so the user never has to run drover.exe's own installer by hand.
    /// Only ever creates missing files, so it's safe even while Discord is running. Runs from the
    /// constructor (before anyone could've subscribed to NotificationRequested yet), so it just
    /// records whether anything was installed and Start() raises the toast once a subscriber exists.</summary>
    private void EnsureDroverInstalledEverywhere()
    {
        foreach (var dir in DiscordLocator.FindDiscordDirs())
        {
            var alreadyHadIt = File.Exists(Path.Combine(dir, DiscordLocator.DllFileName));
            if (DroverInstaller.EnsureInstalled(dir) && !alreadyHadIt)
                _droverJustAutoInstalled = true;
        }
    }

    private async Task RunCheckCycleAsync()
    {
        if (IsChecking)
            return;
        IsChecking = true;

        try
        {
            ReloadCompanionFileIfChangedByHand();

            IsDiscordRunning = DiscordLocator.IsAnyDiscordRunning();

            // Rows keep showing their last known ping while a new cycle runs instead of flashing
            // to "checking" - the header spinner (bound to IsChecking) is the one "refreshing" cue.
            var snapshot = Profiles.ToList();

            var checks = await Task.WhenAll(
                snapshot.Select(p => ProxyHealthChecker.CheckAsync(p.ProxyUrl)));
            var results = checks.Select(c => c.Reachable).ToArray();

            for (var i = 0; i < snapshot.Count; i++)
            {
                snapshot[i].LatencyMs = checks[i].LatencyMs;
                snapshot[i].Status = results[i] ? ProfileStatus.Online : ProfileStatus.Offline;
            }

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
            IsChecking = false;
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
        SetActiveProfileName(pool.ActiveName);
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
            SetActiveProfileName(pool.ActiveName);
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
            SetActiveProfileName(imported.Name);
            return;
        }
    }

    private void RebuildProfilesCollection()
    {
        Profiles.Clear();
        foreach (var profile in _settings.Profiles)
        {
            Profiles.Add(new ProfileItemViewModel(
                profile,
                item => EditProfileRequested?.Invoke(item.Model),
                item => RemoveProfile(item))
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
        _updateCheckTimer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
