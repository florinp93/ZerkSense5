using System.Windows;
using ZerkSense5.Games;

namespace ZerkSense5.UI;

sealed class App : Application
{
    readonly BridgeService _service;
    readonly bool _startHidden;
    MainWindow? _window;
    TrayIcon? _tray;
    GameManager? _games;
    OverlayWindow? _overlay;
    Hotkey? _hotkey;
    bool _exiting, _hintShown;

    readonly bool _demo;

    public App(BridgeService service, bool startHidden, bool demo = false)
    {
        _service = service;
        _startHidden = startHidden;
        _demo = demo;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ZerkSense5;component/UI/Theme.xaml"),
        });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (await OfferUpdateAsync())
        {
            Shutdown();
            return;
        }
        if (!_demo) _service.Start();

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZerkSense5");
        _games = _demo
            ? new GameManager(_service, Path.Combine(Path.GetTempPath(), "ZerkSense5", "demo"), Demo.Library)
            : new GameManager(_service, dataDir);
        _overlay = new OverlayWindow();
        _games.GameLaunched += s => Dispatcher.BeginInvoke(() =>
        {
            if (_service.Config.ShowOverlay) ShowOverlay(s);
        });
        _hotkey = new Hotkey(Hotkey.Control | Hotkey.Alt, 'Z', () => ShowOverlay(_games.Current));

        var vm = new MainViewModel(_service, _games, () => ShowOverlay(_games.Current));
        vm.SetupRequested += ShowSetup;
        vm.UpdateFound += u =>
        {
            if (!Updater.CanInstall) _tray?.ShowInfo("Update available", $"Zerk Sense 5 {u.Version} is available. Click to open the app.");
        };
        vm.UpdateReady += u => _tray?.ShowInfo("Update ready", $"Zerk Sense 5 {u.Version} is downloaded. Click to open the app and restart to update.");
        _window = new MainWindow { DataContext = vm };
        _window.Closing += (_, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            _window.Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _tray?.ShowInfo("Zerk Sense 5 is still running", "The bridge keeps working in the background. Right-click the tray icon to exit.");
            }
        };

        _tray = new TrayIcon(_service);
        _tray.OpenRequested += ShowWindow;
        _tray.ExitRequested += ExitApp;
        _service.Changed += () => Dispatcher.BeginInvoke(() => _tray?.Update());
        SingleInstance.Listen(() => Dispatcher.BeginInvoke(ShowWindow));

        if (!_startHidden) ShowWindow();
        if (_demo)
        {
            _service.EnterDemo(Demo.Controller);
            _games.StartDemo(Demo.Running);
        }
        else if (!_service.Config.SkipSetupPrompt && SetupWindow.AnyMissing(_service.Config))
        {
            if (_startHidden) _tray.ShowInfo("Zerk Sense 5 needs a driver", "Open Zerk Sense 5 to install usbip-win2 and HidHide.");
            else Dispatcher.BeginInvoke(ShowSetup, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    async Task<bool> OfferUpdateAsync()
    {
        var cfg = _service.Config;
        if (_demo || !cfg.AutoCheckUpdates || !Updater.CanInstall) return false;
        var check = Updater.CheckAsync(cfg.IncludePrereleases ?? Updater.Current.IsPrerelease);
        if (await Task.WhenAny(check, Task.Delay(5000)) != check || !check.IsCompletedSuccessfully) return false;
        var update = check.Result;
        if (update?.AssetUrl == null || update.Version.ToString() == cfg.SkippedVersion) return false;
        Log.Info($"Update available: {update.Version}");
        var window = new UpdateWindow(cfg, update);
        window.ShowDialog();
        return window.Installing;
    }

    async void ShowOverlay(GameSession? session)
    {
        var monitor = session != null ? GameDetector.MonitorOf(session.Game.ProcessId) : null;
        var icon = session == null ? null : await GameArt.IconAsync(session.Game.ExePath)
                   ?? (session.Game.SteamAppId is int appId ? await GameArt.CoverAsync(appId, null) : null);
        _overlay?.ShowFor(OverlayModel.Build(session, _service.Controller, _service.Config, icon), monitor, _service.Config.OverlaySeconds);
    }

    void ShowSetup()
    {
        ShowWindow();
        new SetupWindow(_service.Config) { Owner = _window }.ShowDialog();
    }

    void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        _hotkey?.Dispose();
        _games?.Dispose();
        _overlay?.Close();
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }
}
