using ZerkSense5.Games;

namespace ZerkSense5;

enum BridgeState
{
    Starting,
    PortInUse,
    WaitingForController,
    Connected,
    Attached,
}

enum PowerState
{
    Discharging = 0,
    Charging = 1,
    Full = 2,
    Error = 15,
}

sealed record ControllerStatus(
    bool IsEdge,
    int BatteryPercent,
    PowerState Power,
    bool Headphones,
    bool HeadsetMic,
    bool MicMuted,
    int ReportRate);

sealed class BridgeService : IDisposable
{
    readonly UsbIpServer _server;
    readonly AudioBridge _audio;
    readonly HapticsMixer _mixer;
    readonly RumbleEngine _rumble;
    readonly LightingEngine _lighting;
    readonly ManualResetEventSlim _quit = new();
    readonly AutoResetEvent _modeChanged = new(false);
    readonly Thread _thread;
    readonly object _captureLock = new();

    volatile DualSenseBt? _bt;
    volatile VirtualDualSense? _device;
    volatile VirtualXbox360? _xbox;
    volatile ControllerMode _wantedMode = ControllerMode.DualSense;
    HidHide? _hidHide;
    LoopbackCapture? _capture;
    int? _captureTarget;
    int _reports;

    public BridgeService(Config config)
    {
        Config = config;
        _server = new UsbIpServer(config.UsbipPort);
        _server.SessionChanged += attached =>
        {
            if (_bt != null) State = attached ? BridgeState.Attached : BridgeState.Connected;
            Raise();
        };
        _audio = new AudioBridge(config, () => _bt);
        _mixer = new HapticsMixer(config, () => _bt, _audio);
        _rumble = new RumbleEngine(config, () => _bt, _mixer);
        AudioHaptics = new AudioHaptics(config, _mixer);
        Triggers = new TriggerManager(config, () => _bt);
        _rumble.Changed += Triggers.OnRumble;
        _lighting = new LightingEngine(config, () => _bt, () => Controller, () => AudioHaptics.Level);
        _thread = new Thread(Run) { IsBackground = true, Name = "Bridge" };
    }

    public event Action? Changed;

    public Config Config { get; }
    public AudioHaptics AudioHaptics { get; }
    public TriggerManager Triggers { get; }
    public BridgeState State { get; private set; } = BridgeState.Starting;
    public ControllerMode ActiveMode { get; private set; } = ControllerMode.DualSense;
    public ControllerStatus? Controller { get; private set; }
    public bool UsbipInstalled { get; private set; }
    public bool HidHideInstalled { get; private set; }
    public bool ControllerHidden { get; private set; }
    public bool SpeakerStreamOpen => _device?.SpeakerStreamOpen ?? false;
    public bool MicStreamOpen => _device?.MicStreamOpen ?? false;

    public void Start() => _thread.Start();

    public void SetMode(ControllerMode mode)
    {
        if (mode == ControllerMode.Auto) mode = ControllerMode.DualSense;
        if (_wantedMode == mode) return;
        _wantedMode = mode;
        _modeChanged.Set();
    }

    public void SetAudioHapticsTarget(int? pid)
    {
        lock (_captureLock)
        {
            if (pid == _captureTarget && (_capture?.IsRunning ?? pid == null)) return;
            _capture?.Dispose();
            _capture = null;
            _captureTarget = pid;
            AudioHaptics.Reset();
            AudioHaptics.Enabled = pid != null;
            if (pid is not int p) return;
            var started = LoopbackCapture.Start(p, AudioHaptics.Feed);
            _capture = started;
            new Timer(t =>
            {
                ((Timer)t!).Dispose();
                lock (_captureLock)
                {
                    if (_capture != started || started.IsRunning) return;
                    _capture = LoopbackCapture.Start(null, AudioHaptics.Feed);
                }
            }).Change(1500, Timeout.Infinite);
        }
    }

    public void LightingChanged()
    {
        _lighting.Invalidate();
        if (Config.LightingMode == (int)LightingMode.Static) ApplyLightbar();
    }

    public void ApplyLightbar()
    {
        var color = Config.Now.LightbarColor;
        _bt?.SendState(new StateData { AllowLedColor = true, LedRed = color[0], LedGreen = color[1], LedBlue = color[2] }.ToReport32());
    }

    public void ApplyAudioControl()
    {
        var bt = _bt;
        if (bt == null) return;
        var s = new StateData { AllowAudioControl = true, MicSelect = Config.MicSelect, NoiseCancelEnable = true };
        if (Config.SpeakerGain > 0)
        {
            s.AllowAudioControl2 = true;
            s.SpeakerCompPreGain = Config.SpeakerGain;
        }
        bt.SendState(s.ToReport32());
        bt.SendState(_audio.MicStatusReport());
    }

    public void ApplyTriggerReduce()
    {
        var s = new StateData { AllowMotorPowerLevel = true, TriggerMotorPowerReduction = Config.Now.TriggerReduce };
        _bt?.SendState(s.ToReport32());
    }

    public void TestRumble()
    {
        _rumble.Set(220, 140);
        new Timer(t =>
        {
            _rumble.Set(0, 0);
            ((Timer)t!).Dispose();
        }).Change(700, Timeout.Infinite);
    }

    void Raise() => Changed?.Invoke();

    void Run()
    {
        try
        {
            _server.Start();
        }
        catch (System.Net.Sockets.SocketException e)
        {
            Log.Error($"Cannot listen on 127.0.0.1:{Config.UsbipPort} ({e.Message}). Is another USB/IP server running?");
            State = BridgeState.PortInUse;
            Raise();
            return;
        }

        using var rateTimer = new Timer(_ =>
        {
            int n = Interlocked.Exchange(ref _reports, 0);
            if (Controller is { } c && c.ReportRate != n)
            {
                Controller = c with { ReportRate = n };
                Raise();
            }
        }, null, 1000, 1000);

        State = BridgeState.WaitingForController;
        Log.Info("Waiting for a DualSense connected over Bluetooth");
        Raise();

        while (!_quit.IsSet)
        {
            UsbipInstalled = Usbip.Locate(Config) != null;
            _hidHide ??= HidHide.Locate();
            HidHideInstalled = _hidHide != null;

            var info = DualSenseBt.Find();
            if (info == null)
            {
                _quit.Wait(1500);
                continue;
            }

            using var bt = DualSenseBt.Open(info);
            if (bt == null)
            {
                Log.Warn("Found a DualSense but could not open it. Another app may be holding it exclusively.");
                _quit.Wait(3000);
                continue;
            }
            RunSession(info, bt);
        }
        _hidHide?.UnhideAll();
    }

    void RunSession(HidDeviceInfo info, DualSenseBt bt)
    {
        var gone = new ManualResetEventSlim();
        bt.Disconnected += gone.Set;
        bt.InputReport += OnInput;
        bt.Start();
        bt.InitFeatures();
        if (!bt.Alive)
        {
            _quit.Wait(1000);
            return;
        }

        _bt = bt;
        Controller = new ControllerStatus(bt.IsEdge, 0, PowerState.Discharging, false, false, false, 0);
        State = BridgeState.Connected;
        Log.Info($"Connected: DualSense{(bt.IsEdge ? " Edge" : "")} over Bluetooth");
        Raise();

        bt.SendState(InitialState().ToReport32());
        bt.SendState(_audio.MicStatusReport());

        if (Config.UseHidHide && _hidHide != null)
        {
            _hidHide.Hide(info.InstanceId);
            ControllerHidden = true;
        }

        var usbip = Usbip.Locate(Config);
        if (usbip == null)
            Log.Warn("usbip-win2 is not installed. Battery and settings work, but Windows can't see a virtual controller.");

        while (true)
        {
            var mode = _wantedMode;
            PlugIn(bt, mode, usbip);
            int which;
            do which = WaitHandle.WaitAny([gone.WaitHandle, _quit.WaitHandle, _modeChanged]);
            while (which == 2 && _wantedMode == mode);
            if (which != 2) break;
            Log.Info(_wantedMode == ControllerMode.Xbox360 ? "Switching to Xbox 360 mode" : "Switching to DualSense mode");
            Unplug(usbip);
            Thread.Sleep(400);
        }

        Unplug(usbip);
        _bt = null;
        _audio.MicActive = false;
        _audio.ResetMic();
        Controller = null;
        State = BridgeState.WaitingForController;
        if (!_quit.IsSet) Log.Info("Controller disconnected");
        Raise();
    }

    void PlugIn(DualSenseBt bt, ControllerMode mode, Usbip? usbip)
    {
        ActiveMode = mode;
        IUsbDevice device;
        if (mode == ControllerMode.Xbox360)
        {
            var x = new VirtualXbox360(bt);
            x.Rumble += _rumble.Set;
            x.PlayerChanged += SetPlayerLeds;
            _xbox = x;
            device = x;
        }
        else
        {
            var d = new VirtualDualSense(bt, _audio, Config, OnGameReport);
            d.StreamsChanged += Raise;
            _device = d;
            device = d;
        }
        _server.Device = device;
        Triggers.Reset();
        Triggers.Apply();
        if (usbip != null && usbip.Attach())
            Log.Info(mode == ControllerMode.Xbox360 ? "Virtual Xbox 360 controller plugged in" : "Virtual wired DualSense plugged in");
        Raise();
    }

    void Unplug(Usbip? usbip)
    {
        usbip?.Detach();
        _server.Device = null;
        _device?.Release();
        _xbox?.Release();
        _device = null;
        _xbox = null;
        _rumble.Stop();
        SetPlayerLeds(0);
    }

    void OnGameReport(StateData state)
    {
        Triggers.FilterGameReport(state);
        if (state.AllowLedColor) _lighting.NoteGameLightbar();
    }

    void SetPlayerLeds(int player)
    {
        byte pattern = player switch { 1 => 0x04, 2 => 0x0A, 3 => 0x15, 4 => 0x1B, _ => 0 };
        _bt?.SendState(new StateData { AllowPlayerIndicators = true, PlayerLights = pattern }.ToReport32());
    }

    void OnInput(byte[] buf, int len)
    {
        if (len < 2 + 63 || buf[0] != 0x31 || (buf[1] & 0x02) != 0) return;
        Interlocked.Increment(ref _reports);
        if (Controller is not { } c) return;

        byte power = buf[2 + 52], plugged = buf[2 + 53];
        var state = (PowerState)(power >> 4);
        int percent = state == PowerState.Full ? 100 : Math.Min((power & 0x0F) * 10 + 5, 100);
        var next = c with
        {
            BatteryPercent = percent,
            Power = state is PowerState.Discharging or PowerState.Charging or PowerState.Full ? state : PowerState.Error,
            Headphones = (plugged & 0x01) != 0,
            HeadsetMic = (plugged & 0x02) != 0,
            MicMuted = (plugged & 0x04) != 0,
        };
        if (next == c) return;
        Controller = next;
        Raise();
    }

    StateData InitialState() => new()
    {
        AllowAudioControl = true,
        AllowLedColor = true,
        MicSelect = Config.MicSelect,
        NoiseCancelEnable = true,
        AllowLightBrightnessChange = true,
        AllowColorLightFadeAnimation = true,
        LightFadeAnimation = 2,
        LightBrightness = 0,
        LedRed = Config.Now.LightbarColor[0],
        LedGreen = Config.Now.LightbarColor[1],
        LedBlue = Config.Now.LightbarColor[2],
    };

    public void Dispose()
    {
        _quit.Set();
        _thread.Join(3000);
        _rumble.Dispose();
        _lighting.Dispose();
        lock (_captureLock) _capture?.Dispose();
        _mixer.Dispose();
        _server.Dispose();
    }
}
