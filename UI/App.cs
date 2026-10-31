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

    public App(BridgeService service, bool startHidden)
    {
        _service = service;
        _startHidden = startHidden;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ZerkSense5;component/UI/Theme.xaml"),
        });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZerkSense5");
        _games = new GameManager(_service, dataDir);
        _overlay = new OverlayWindow();
        _games.GameLaunched += s => Dispatcher.BeginInvoke(() =>
        {
            if (_service.Config.ShowOverlay) ShowOverlay(s);
        });
        _hotkey = new Hotkey(Hotkey.Control | Hotkey.Alt, 'Z', () => ShowOverlay(_games.Current));

        var vm = new MainViewModel(_service, _games, () => ShowOverlay(_games.Current));
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
    }

    async void ShowOverlay(GameSession? session)
    {
        var monitor = session != null ? GameDetector.MonitorOf(session.Game.ProcessId) : null;
        var icon = session == null ? null : await GameArt.IconAsync(session.Game.ExePath);
        _overlay?.ShowFor(OverlayModel.Build(session, _service.Controller, _service.Config, icon), monitor, _service.Config.OverlaySeconds);
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
