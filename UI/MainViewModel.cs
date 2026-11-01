using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZerkSense5.UI;

sealed partial class MainViewModel : INotifyPropertyChanged
{
    readonly BridgeService _service;
    readonly Config _cfg;
    readonly DispatcherTimer _saveTimer;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<LogLine> LogLines { get; } = new();

    public MainViewModel(BridgeService service, Games.GameManager games, Action showOverlay)
    {
        _service = service;
        _games = games;
        _cfg = service.Config;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _cfg.Save();
        };
        service.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Log.Message += (time, tag, msg) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(new LogLine(time.ToString("HH:mm:ss"), tag, msg));
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
        });
        OpenUrl = new Relay(p => Process.Start(new ProcessStartInfo((string)p!) { UseShellExecute = true }));
        OpenSetup = new Relay(_ => SetupRequested?.Invoke());
        SetColor = new Relay(p =>
        {
            var c = (Color)ColorConverter.ConvertFromString((string)p!);
            _cfg.LightbarColor = [c.R, c.G, c.B];
            LightbarChanged();
        });
        BrowseUsbip = new Relay(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "usbip.exe|usbip.exe", Title = "Locate usbip.exe" };
            if (dlg.ShowDialog() == true) UsbipPath = dlg.FileName;
        });
        InitGames(showOverlay);
        InitEffects();
        InitLibrary();
        InitUpdates();
        Refresh();
    }

    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    void Saved([CallerMemberName] string? name = null)
    {
        Notify(name);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Refresh()
    {
        foreach (var p in new[]
                 {
                     nameof(IsConnected), nameof(ControllerName), nameof(StatusTitle), nameof(StatusDetail),
                     nameof(StatusBrush), nameof(BatteryPercent), nameof(BatteryText), nameof(PowerText),
                     nameof(IsCharging), nameof(VirtualText), nameof(VirtualBrush), nameof(HidHideText),
                     nameof(HidHideBrush), nameof(HeadsetText), nameof(RateText), nameof(StreamsText),
                     nameof(ShowUsbipMissing), nameof(ShowHidHideMissing), nameof(MicMutedText), nameof(DriversText), nameof(DsxText), nameof(DsxState),
                     nameof(MicMuted), nameof(ModeText), nameof(TriggerLText), nameof(TriggerRText),
                 })
            Notify(p);
    }

    ControllerStatus? C => _service.Controller;
    public bool IsConnected => C != null;
    public string ControllerName => C == null ? "No controller" : C.IsEdge ? "DualSense Edge" : "DualSense";

    public string StatusTitle => _service.State switch
    {
        BridgeState.Starting => "Starting…",
        BridgeState.PortInUse => "USB/IP port in use",
        BridgeState.WaitingForController => "Waiting for controller",
        BridgeState.Connected => _service.UsbipInstalled ? "Connected · plugging in…" : "Connected over Bluetooth",
        BridgeState.Attached => _service.ActiveMode == Games.ControllerMode.Xbox360 ? "Connected · Xbox 360 mode" : "Connected · wired mode active",
        _ => "",
    };

    public string StatusDetail => _service.State switch
    {
        BridgeState.PortInUse => $"Another program is using port {_cfg.UsbipPort}. Change it in Settings and restart.",
        BridgeState.WaitingForController => "Turn on your DualSense (press PS). Pair it in Windows Bluetooth settings first if you haven't.",
        BridgeState.Connected when !_service.UsbipInstalled => "Install usbip-win2 so games see a wired DualSense with haptics and audio.",
        BridgeState.Connected => "Waiting for Windows to attach the virtual controller.",
        BridgeState.Attached => _service.ActiveMode == Games.ControllerMode.Xbox360
            ? "Games see an Xbox 360 controller. Rumble is turned into DualSense feedback."
            : "Games see a wired DualSense: HD haptics, speaker, mic and adaptive triggers work.",
        _ => "",
    };

    public Brush StatusBrush => (Brush)Application.Current.Resources[_service.State switch
    {
        BridgeState.Attached => "Success",
        BridgeState.Connected => "Accent",
        BridgeState.PortInUse => "Danger",
        _ => "Subtext",
    }];

    public int BatteryPercent => C?.BatteryPercent ?? 0;
    public string BatteryText => C == null ? "—" : $"{C.BatteryPercent}%";
    public bool IsCharging => C?.Power is PowerState.Charging;

    public string PowerText => C?.Power switch
    {
        null => "Not connected",
        PowerState.Charging => "Charging",
        PowerState.Full => "Fully charged",
        PowerState.Error => "Charging error",
        _ => C.BatteryPercent <= 10 ? "Low battery" : "On battery",
    };

    public string VirtualText => _service.State == BridgeState.Attached
        ? (_service.ActiveMode == Games.ControllerMode.Xbox360 ? "Xbox 360 controller: plugged in" : "Wired DualSense: plugged in")
        : _service.UsbipInstalled ? "Wired DualSense: not attached" : "Wired DualSense: usbip-win2 missing";
    public Brush VirtualBrush => Res(_service.State == BridgeState.Attached ? "Success" : "Warning");

    public string HidHideText => !_service.HidHideInstalled ? "HidHide: not installed"
        : _service.ControllerHidden ? "HidHide: Bluetooth pad hidden" : "HidHide: off";
    public Brush HidHideBrush => Res(_service.ControllerHidden ? "Success" : "Subtext");

    public string HeadsetText => C == null ? "Headset: —" : C.Headphones ? (C.HeadsetMic ? "Headset with mic" : "Headphones plugged") : "No headset";
    public string MicMutedText => C?.MicMuted == true ? "Mic muted" : "Mic live";
    public string RateText => C == null ? "—" : $"{C.ReportRate} Hz";

    public string StreamsText => _service.State != BridgeState.Attached ? "Audio streams idle"
        : (_service.SpeakerStreamOpen, _service.MicStreamOpen) switch
        {
            (true, true) => "Haptics/speaker + mic streaming",
            (true, false) => "Haptics/speaker streaming",
            (false, true) => "Mic streaming",
            _ => "Audio streams idle",
        };

    public bool ShowUsbipMissing => !_service.UsbipInstalled && _service.State != BridgeState.Starting;
    public bool ShowHidHideMissing => !_service.HidHideInstalled && _cfg.UseHidHide && _service.State != BridgeState.Starting;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public double HapticsGain
    {
        get => _cfg.HapticsGain;
        set { _cfg.HapticsGain = (float)Math.Round(value, 2); Saved(); Notify(nameof(HapticsGainText)); }
    }
    public string HapticsGainText => $"{_cfg.HapticsGain * 100:F0}%";

    public int SpeakerSelect
    {
        get => _cfg.SpeakerSelect;
        set { _cfg.SpeakerSelect = value; Saved(); }
    }

    public int MicSelect
    {
        get => _cfg.MicSelect;
        set { _cfg.MicSelect = value; Saved(); _service.ApplyAudioControl(); }
    }

    public int PacketMode
    {
        get => _cfg.PacketMode;
        set { _cfg.PacketMode = value; Saved(); }
    }

    public double AudioBufferLength
    {
        get => _cfg.AudioBufferLength;
        set { _cfg.AudioBufferLength = (int)value; Saved(); }
    }

    public double SpeakerGain
    {
        get => _cfg.SpeakerGain;
        set { _cfg.SpeakerGain = (int)value; Saved(); Notify(nameof(SpeakerGainText)); _service.ApplyAudioControl(); }
    }
    public string SpeakerGainText => _cfg.SpeakerGain == 0 ? "Game" : $"+{_cfg.SpeakerGain}";

    public bool LockVolume
    {
        get => _cfg.LockVolume;
        set { _cfg.LockVolume = value; Saved(); }
    }

    public double TriggerReduce
    {
        get => _cfg.TriggerReduce;
        set { _cfg.TriggerReduce = (int)value; Saved(); Notify(nameof(TriggerReduceText)); _service.ApplyTriggerReduce(); }
    }
    public string TriggerReduceText => _cfg.TriggerReduce == 0 ? "Full" : $"−{_cfg.TriggerReduce * 10}%";

    public double LedR { get => _cfg.LightbarColor[0]; set { _cfg.LightbarColor[0] = (byte)value; LightbarChanged(); } }
    public double LedG { get => _cfg.LightbarColor[1]; set { _cfg.LightbarColor[1] = (byte)value; LightbarChanged(); } }
    public double LedB { get => _cfg.LightbarColor[2]; set { _cfg.LightbarColor[2] = (byte)value; LightbarChanged(); } }
    public Brush LightbarBrush => new SolidColorBrush(Color.FromRgb(_cfg.LightbarColor[0], _cfg.LightbarColor[1], _cfg.LightbarColor[2]));
    public string LightbarHex => $"#{_cfg.LightbarColor[0]:X2}{_cfg.LightbarColor[1]:X2}{_cfg.LightbarColor[2]:X2}";

    void LightbarChanged()
    {
        foreach (var p in new[] { nameof(LedR), nameof(LedG), nameof(LedB), nameof(LightbarBrush), nameof(LightbarHex) }) Notify(p);
        Saved(nameof(LightbarBrush));
        _service.LightingChanged();
    }

    public bool AutoXboxMode
    {
        get => _cfg.AutoXboxMode;
        set { _cfg.AutoXboxMode = value; Saved(); _games.Refresh(); }
    }

    public int RumbleStyle
    {
        get => _cfg.RumbleStyle;
        set { _cfg.RumbleStyle = value; Saved(); }
    }

    public double RumbleStrength
    {
        get => _cfg.RumbleStrength;
        set { _cfg.RumbleStrength = (int)value; Saved(); Notify(nameof(RumbleStrengthText)); }
    }
    public string RumbleStrengthText => $"{_cfg.RumbleStrength}%";

    public ICommand TestRumble => _testRumble ??= new Relay(_ => _service.TestRumble());
    ICommand? _testRumble;

    public Func<byte[]?> LiveInput => () => _service.LastInput;
    public bool MicMuted => _service.Controller?.MicMuted ?? false;
    public string ModeText => _service.State != BridgeState.Attached ? "Not plugged in"
        : _service.ActiveMode == Games.ControllerMode.Xbox360 ? "Xbox 360" : "Wired DualSense";
    public string TriggerLText => TriggerText(0);
    public string TriggerRText => TriggerText(1);
    string TriggerText(int side) => _service.Triggers.Effective(side)?.Summary is { Length: > 0 } t
        ? (_service.Triggers.ModActive ? "DSX mod · " : "") + t
        : "Game controlled";

    public bool DsxMods
    {
        get => _cfg.DsxMods;
        set { _cfg.DsxMods = value; Saved(); _service.Dsx.Apply(); Notify(nameof(DsxText)); Notify(nameof(DsxState)); }
    }

    public string DsxState => !_cfg.DsxMods ? "Off" : _service.Dsx.PortBusy ? "Port busy" : _service.Dsx.ModActive ? "Active" : "Listening";

    public string DsxText => !_cfg.DsxMods ? "Off"
        : _service.Dsx.PortBusy ? $"Port {_cfg.DsxPort} is in use. Close DSX and toggle this again."
        : _service.Dsx.ModActive ? "A DSX mod is controlling the triggers"
        : $"Waiting for a mod on port {_cfg.DsxPort}";

    public bool UseHidHide
    {
        get => _cfg.UseHidHide;
        set { _cfg.UseHidHide = value; Saved(); Notify(nameof(ShowHidHideMissing)); }
    }

    public string UsbipPath
    {
        get => _cfg.UsbipPath;
        set { _cfg.UsbipPath = value.Trim(); Saved(); }
    }

    public string UsbipPort
    {
        get => _cfg.UsbipPort.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, out int p) && p is > 0 and < 65536) _cfg.UsbipPort = p;
            Saved();
        }
    }

    public bool StartMinimized
    {
        get => _cfg.StartMinimized;
        set { _cfg.StartMinimized = value; Saved(); }
    }

    public bool LowBatteryAlert
    {
        get => _cfg.LowBatteryAlert;
        set { _cfg.LowBatteryAlert = value; Saved(); }
    }

    public bool StartWithWindows
    {
        get => StartupTask.IsEnabled();
        set
        {
            StartupTask.Set(value);
            Notify();
        }
    }

    public string VersionText => $"Zerk Sense 5 · v{Updater.Current}";

    public ICommand OpenUrl { get; }
    public ICommand OpenSetup { get; }
    public event Action? SetupRequested;
    public ICommand SetColor { get; }
    public ICommand BrowseUsbip { get; }
}

sealed record LogLine(string Time, string Tag, string Message)
{
    public Brush TagBrush => (Brush)Application.Current.Resources[Tag switch
    {
        "warn" => "Warning",
        "fail" => "Danger",
        _ => "AccentLight",
    }];
}

sealed class Relay(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run(parameter);
}

sealed class OptionConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) =>
        value is true ? int.Parse((string)parameter, CultureInfo.InvariantCulture) : Binding.DoNothing;
}

sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => throw new NotSupportedException();
}
