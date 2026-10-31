using System.Windows.Input;
using System.Windows.Threading;

namespace ZerkSense5.UI;

partial class MainViewModel
{
    UpdateInfo? _update;
    string _updateStatus = "";
    bool _updating;
    DispatcherTimer? _updateTimer;

    public ICommand CheckUpdates { get; private set; } = null!;
    public ICommand InstallUpdate { get; private set; } = null!;
    public ICommand SkipUpdate { get; private set; } = null!;
    public ICommand OpenReleaseNotes { get; private set; } = null!;
    public event Action<UpdateInfo>? UpdateFound;

    void InitUpdates()
    {
        CheckUpdates = new Relay(async _ => await CheckForUpdatesAsync(manual: true));
        InstallUpdate = new Relay(async _ => await InstallUpdateAsync());
        SkipUpdate = new Relay(_ =>
        {
            if (_update == null) return;
            _cfg.SkippedVersion = _update.Version.ToString();
            _cfg.Save();
            _update = null;
            RefreshUpdates();
        });
        OpenReleaseNotes = new Relay(_ => OpenUrl.Execute(_update?.PageUrl ?? $"https://github.com/{Updater.Repo}/releases"));

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (_cfg.AutoCheckUpdates) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        UpdateStatus = "Checking for updates…";
        try
        {
            var found = await Updater.CheckAsync(IncludePrereleases);
            if (found != null && !manual && found.Version.ToString() == _cfg.SkippedVersion) found = null;
            bool isNew = found != null && found.Version.ToString() != _update?.Version.ToString();
            _update = found;
            UpdateStatus = found == null ? $"You're up to date (checked {DateTime.Now:HH:mm})." : $"Version {found.Version} is available.";
            if (isNew && found != null)
            {
                Log.Info($"Update available: {found.Version}");
                UpdateFound?.Invoke(found);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            UpdateStatus = "Couldn't check for updates: " + e.Message;
        }
        RefreshUpdates();
    }

    async Task InstallUpdateAsync()
    {
        if (_update == null || _updating) return;
        if (!Updater.CanInstall)
        {
            OpenReleaseNotes.Execute(null);
            return;
        }
        _updating = true;
        RefreshUpdates();
        try
        {
            await Updater.DownloadAndInstallAsync(_update, new Progress<string>(s => UpdateStatus = s));
            Log.Info($"Installing {_update.Version} and restarting");
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus = "Update failed: " + e.Message;
            Log.Warn(UpdateStatus);
            _updating = false;
            RefreshUpdates();
        }
    }

    void RefreshUpdates()
    {
        foreach (var p in new[] { nameof(UpdateAvailable), nameof(UpdateTitle), nameof(UpdateStatus), nameof(CanInstallUpdate), nameof(InstallUpdateText) })
            Notify(p);
    }

    public string CurrentVersionText => $"Version {Updater.Current}";
    public bool UpdateAvailable => _update != null;
    public string UpdateTitle => _update == null ? "" : $"Zerk Sense 5 {_update.Version} is available";
    public bool CanInstallUpdate => _update != null && !_updating;
    public string InstallUpdateText => Updater.CanInstall ? "Update now" : "Download";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            _updateStatus = value;
            Notify();
        }
    }

    public bool AutoCheckUpdates
    {
        get => _cfg.AutoCheckUpdates;
        set { _cfg.AutoCheckUpdates = value; Saved(); }
    }

    public bool IncludePrereleases
    {
        get => _cfg.IncludePrereleases ?? Updater.Current.IsPrerelease;
        set { _cfg.IncludePrereleases = value; Saved(); }
    }

    public string DriversText =>
        $"usbip-win2: {(_service.UsbipInstalled ? "installed" : "not installed")} · HidHide: {(_service.HidHideInstalled ? "installed" : "not installed")}";
}
