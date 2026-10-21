using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
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
    private bool _droverJustAutoInstalled;
    private bool _isDownloadingUpdate;

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();

    public event Action<ProfileStatus>? StatusChanged;
    public event Action<string>? NotificationRequested;
    public event Action<ProxyProfile?>? EditProfileRequested;
    public event Action? AddProfilesRequested;
    public event Action? RestartPromptRequested;

    public RelayCommand ActivateProfileCommand { get; }
    public RelayCommand AddProfileCommand { get; }
    public RelayCommand RefreshNowCommand { get; }
    public RelayCommand StartDiscordCommand { get; }
    public RelayCommand CloseDiscordCommand { get; }
    public RelayCommand RestartDiscordCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand ExitCommand { get; }

    public TrayViewModel()
    {
        var isFirstRun = !SettingsStore.Exists();
        _settings = SettingsStore.Load();
        EnsureDroverInstalledEverywhere();
        if (isFirstRun)
            ImportExistingDroverIniProxyIfAny();
        RebuildProfilesCollection();
        Save();

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
        // A tray app has no visible window at all, so a manual launch gives zero confirmation it
        // actually started - say so once. Skipped for an autostart launch (Windows itself starting
        // it silently at login is the expected, unremarkable case).
        if (!AutostartService.IsAutostartLaunch(Environment.GetCommandLineArgs()))
            NotificationRequested?.Invoke("Discord Drover запущен — ищите его значок в трее.");

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
            Save();
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
            Save();
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

    /// <summary>Purely informational now - updates install automatically and mandatorily, there's
    /// nothing left for the user to click here.</summary>
    public string UpdateBannerText => $"Устанавливается обновление: {LatestVersionTag}";

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
        var addedNames = new List<string>();

        foreach (var profile in profiles)
        {
            var name = profile.Name;
            var suffix = 2;
            while (_settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = $"{profile.Name} ({suffix++})";

            _settings.Profiles.Add(new ProxyProfile { Name = name, ProxyUrl = profile.ProxyUrl });
            addedNames.Add(name);
            addedAny = true;
        }

        if (!addedAny)
            return;

        RebuildProfilesCollection();
        Save();
        _ = RunCheckCycleAsync();
        _ = AutoActivateFirstReachableIfNoneActiveAsync(addedNames);
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

        var isNewProfile = originalName is null;

        if (isNewProfile)
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
        Save();
        _ = RunCheckCycleAsync();

        if (isNewProfile)
            _ = AutoActivateFirstReachableIfNoneActiveAsync(new List<string> { profile.Name });
    }

    /// <summary>If nothing is active yet (first-ever add, or every profile got removed), picks the
    /// first of the just-added profiles that's actually reachable and activates it - so adding
    /// configs doesn't leave drover.ini on "direct" indefinitely until the user picks one by hand.
    /// Skips silently if none of them are reachable, or if something else already set an active
    /// profile by the time a check comes back.</summary>
    private async Task AutoActivateFirstReachableIfNoneActiveAsync(List<string> addedNames)
    {
        if (!string.IsNullOrEmpty(_settings.ActiveProfileName))
            return;

        foreach (var name in addedNames)
        {
            var item = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                continue;

            var (reachable, _) = await ProxyHealthChecker.CheckAsync(item.ProxyUrl);
            if (!reachable || !string.IsNullOrEmpty(_settings.ActiveProfileName))
                continue;

            ActivateInternal(item, notifyAuto: true);
            return;
        }
    }

    private void RemoveProfile(ProfileItemViewModel item)
    {
        _settings.Profiles.RemoveAll(p => p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        if (_settings.ActiveProfileName?.Equals(item.Name, StringComparison.OrdinalIgnoreCase) == true)
            SetActiveProfileName(null);

        RebuildProfilesCollection();
        Save();
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

    /// <summary><paramref name="skipConfirm"/> is for when the caller already asked (the
    /// post-switch restart prompt) - avoids asking the user to confirm the same restart twice.</summary>
    private void RestartDiscord(bool skipConfirm = false)
    {
        var running = DiscordLocator.GetRunningDiscordProcesses().ToList();
        if (running.Count == 0)
            return;

        if (!skipConfirm)
        {
            var confirm = MessageBox.Show(
                "Discord будет закрыт и перезапущен, чтобы применить новый прокси. Продолжить?",
                "Discord Drover",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

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

        LatestVersionTag = result.Tag;
        UpdateAvailable = true;

        NotificationRequested?.Invoke($"Найдено обновление DroverSwitch {result.Tag} - скачивается и будет установлено автоматически.");

        // Updates are mandatory, not opt-in - there's no "click to install" step to wait on.
        await DownloadAndApplyUpdateAsync(result.SetupDownloadUrl, result.Tag);
    }

    /// <summary>Downloads the new DroverSwitchSetup.exe and, once it's on disk, launches it and
    /// shuts this copy down - Setup takes it from there (overwrite + relaunch). No confirmation
    /// dialog: updates are mandatory, matching what the user asked for. Best-effort on failure (no
    /// network, GitHub down, disk full) - just leaves the update for the next periodic check to
    /// retry, same as every other UpdateChecker failure mode.</summary>
    private async Task DownloadAndApplyUpdateAsync(string? setupUrl, string? tag)
    {
        if (setupUrl is null || tag is null || _isDownloadingUpdate)
            return;

        _isDownloadingUpdate = true;
        string path;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DroverSwitch-UpdateChecker");

            var bytes = await http.GetByteArrayAsync(setupUrl);

            // Named after the tag so a second, newer check in the same session doesn't collide
            // with - or get mistaken for - an already-downloaded older one.
            path = Path.Combine(Path.GetTempPath(), $"DroverSwitchSetup-{tag}.exe");
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch
        {
            _isDownloadingUpdate = false;
            return;
        }

        try
        {
            // --auto-update tells Setup this launch already knows DroverSwitch is running and
            // intends to close it - no confirmation prompt there either, since nobody would be
            // around to answer it (this process is about to shut itself down regardless).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                Arguments = "--auto-update",
                UseShellExecute = true,
            });
            Application.Current.Shutdown();
        }
        catch
        {
            _isDownloadingUpdate = false;
        }
    }

    /// <summary>Full uninstall: removes drover (version.dll, drover-packet.bin, drover.ini) and
    /// DroverSwitch's own companion file from every discovered Discord folder, plus this app's own
    /// saved settings. version.dll is locked while Discord runs, same as during a switch, so a
    /// running Discord is offered to be closed right here instead of just refusing.</summary>
    private void UninstallEverything()
    {
        var running = DiscordLocator.GetRunningDiscordProcesses().ToList();
        if (running.Count > 0)
        {
            var closeConfirm = MessageBox.Show(
                "Discord сейчас запущен — version.dll нельзя удалить, пока он работает. Закрыть Discord и продолжить удаление?",
                "Удалить Discord Drover",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (closeConfirm != MessageBoxResult.Yes)
                return;

            KillDiscordProcesses(running);
            IsDiscordRunning = DiscordLocator.IsAnyDiscordRunning();

            if (DiscordLocator.IsAnyDiscordRunning())
            {
                MessageBox.Show(
                    "Не удалось закрыть Discord. Закройте его вручную и повторите удаление.",
                    "Discord Drover",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
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

        var lockedFiles = new List<string>();
        foreach (var dir in DiscordLocator.FindDiscordDirs())
        {
            TryDelete(Path.Combine(dir, DiscordLocator.DllFileName), lockedFiles);
            TryDelete(Path.Combine(dir, DiscordLocator.OptionsFileName), lockedFiles);
            TryDelete(Path.Combine(dir, DiscordLocator.PacketFileName), lockedFiles);
            // Older versions mirrored a drover-switch.ini companion file here - no longer written,
            // but still cleaned up on uninstall in case a leftover one is still sitting around.
            TryDelete(Path.Combine(dir, "drover-switch.ini"), lockedFiles);
        }

        AutostartService.SetEnabled(false);
        SettingsStore.Delete();

        if (lockedFiles.Count > 0)
        {
            MessageBox.Show(
                "Discord Drover удалён, но не удалось удалить файлы (возможно, заняты другим процессом):\n\n" +
                string.Join("\n", lockedFiles),
                "Discord Drover",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show("Discord Drover удалён.", "Discord Drover", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        Application.Current.Shutdown();
    }

    private static void TryDelete(string path, List<string> lockedFiles)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Most likely still locked by a lingering process holding version.dll open - surface
            // this to the caller instead of swallowing it, so the user knows uninstall was partial.
            lockedFiles.Add(path);
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
        Save();

        foreach (var p in Profiles)
            p.IsActive = p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase);

        if (notifyAuto)
        {
            // A background action popping up a blocking dialog would be jarring - the toast is
            // enough, and the Discord status card's own Restart button is still right there.
            NotificationRequested?.Invoke($"Автоматически переключено на «{item.Name}».");
        }
        else if (!discordClosed)
        {
            // Wpf.Ui.Controls.MessageBox (tried first, for custom "ОК"/"Закрыть" button text)
            // rendered as an empty box in practice - route through TrayPopup instead, same as
            // every other dialog in this app, with a small hand-built FluentWindow that's already
            // proven to actually show its content.
            RestartPromptRequested?.Invoke();
        }
    }

    /// <summary>Called back by TrayPopup after the user clicks "ОК" on the restart prompt.</summary>
    public void ConfirmRestartAfterSwitch() => RestartDiscord(skipConfirm: true);

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

    /// <summary>First-run only (gated by the caller): if drover.ini already has a proxy configured -
    /// set up by hand before the switcher existed - import it as the active profile instead of
    /// silently hiding what's actually in effect behind the default "Прямое соединение" seed.</summary>
    private void ImportExistingDroverIniProxyIfAny()
    {
        foreach (var dir in DiscordLocator.FindDroverInstalledDirs(DiscordLocator.FindDiscordDirs()))
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

    /// <summary>The only persistence DroverSwitch's own state has - %AppData%\DroverSwitch\
    /// settings.json. No longer mirrored into Discord's own (version-numbered, auto-update-churned)
    /// install folder; that mirroring was fragile by construction and this app already installs
    /// itself under %AppData% now too, so keeping all of its own state there is simpler and more
    /// robust than spreading it across two places.</summary>
    private void Save() => SettingsStore.Save(_settings);

    public void Dispose()
    {
        _timer.Stop();
        _updateCheckTimer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
