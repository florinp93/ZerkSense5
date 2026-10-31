using System.Windows.Threading;

namespace ZerkSense5.UI;

partial class MainViewModel
{
    public TriggerLabViewModel TriggerLab { get; private set; } = null!;
    DispatcherTimer? _meterTimer;

    void InitEffects()
    {
        TriggerLab = new TriggerLabViewModel(_cfg, _service.Triggers, () => SelectedProfile, () => EditProfile, () => _selected?.Name, ProfileChanged, () =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        });
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _meterTimer.Tick += (_, _) =>
        {
            Notify(nameof(AudioLevel));
            Notify(nameof(AudioHapticsStatus));
        };
        _meterTimer.Start();
    }

    public int AudioHapticsMode
    {
        get => _cfg.AudioHapticsMode;
        set { _cfg.AudioHapticsMode = value; Saved(); _games.UpdateAudioHaptics(); }
    }

    public double AudioHapticsIntensity
    {
        get => _cfg.AudioHapticsIntensity;
        set { _cfg.AudioHapticsIntensity = (int)value; Saved(); Notify(nameof(AudioHapticsIntensityText)); }
    }
    public string AudioHapticsIntensityText => $"{_cfg.AudioHapticsIntensity}%";

    public double AudioHapticsCutoff
    {
        get => _cfg.AudioHapticsCutoff;
        set { _cfg.AudioHapticsCutoff = (int)value; Saved(); Notify(nameof(AudioHapticsCutoffText)); }
    }
    public string AudioHapticsCutoffText => $"{_cfg.AudioHapticsCutoff} Hz";

    public double AudioHapticsGate
    {
        get => _cfg.AudioHapticsGate;
        set { _cfg.AudioHapticsGate = (int)value; Saved(); Notify(nameof(AudioHapticsGateText)); }
    }
    public string AudioHapticsGateText => $"{_cfg.AudioHapticsGate}%";

    public double AudioHapticsSmoothness
    {
        get => _cfg.AudioHapticsSmoothness;
        set { _cfg.AudioHapticsSmoothness = (int)value; Saved(); Notify(nameof(AudioHapticsSmoothnessText)); }
    }
    public string AudioHapticsSmoothnessText => $"{_cfg.AudioHapticsSmoothness} ms";

    public double AudioLevel => _service.AudioHaptics.Enabled ? Math.Min(1, _service.AudioHaptics.Level) * 240 : 0;

    public string AudioHapticsStatus =>
        _service.AudioHaptics.Enabled && _games.Current is { } s ? $"Live from {s.Game.Name}"
        : _cfg.AudioHapticsMode == 0 ? "Off"
        : _games.Current != null ? "Not used for this game (it has native haptics)"
        : "Starts when a game runs";

    public int LightingMode
    {
        get => _cfg.LightingMode;
        set
        {
            _cfg.LightingMode = value;
            Saved();
            Notify(nameof(ShowLightingSpeed));
            Notify(nameof(LightingHint));
            _service.LightingChanged();
        }
    }

    public double LightingSpeed
    {
        get => _cfg.LightingSpeed;
        set { _cfg.LightingSpeed = (int)value; Saved(); }
    }

    public bool ShowLightingSpeed => _cfg.LightingMode is 1 or 2;

    public string LightingHint => (LightingMode)_cfg.LightingMode switch
    {
        ZerkSense5.LightingMode.Breathing => "Fades the colour below in and out.",
        ZerkSense5.LightingMode.Rainbow => "Cycles through every colour.",
        ZerkSense5.LightingMode.Battery => "Green when full, through yellow, to red when low. Pulses while charging.",
        ZerkSense5.LightingMode.AudioReactive => "Flashes the colour below with the game's bass. Uses the audio haptics capture, so it works while that is live.",
        _ => "A single colour.",
    };

    public int ProfileAudioHaptics
    {
        get => SelectedProfile?.AudioHaptics switch { true => 1, false => 2, _ => 0 };
        set
        {
            if (EditProfile is not { } profile) return;
            profile.AudioHaptics = value switch { 1 => true, 2 => false, _ => null };
            ProfileChanged();
            _games.UpdateAudioHaptics();
        }
    }
}
