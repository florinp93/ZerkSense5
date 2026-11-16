namespace ZerkSense5.UI;

partial class MainViewModel
{
    static readonly PadButton[] GyroButtons = [PadButton.L2, PadButton.L1, PadButton.R1, PadButton.L3, PadButton.R3];

    public int GyroModeIndex
    {
        get => (int)_cfg.GyroMode;
        set { _cfg.GyroMode = (GyroMode)value; Saved(); NotifyGyro(); }
    }

    public int GyroActivationIndex
    {
        get => (int)_cfg.GyroActivation;
        set { _cfg.GyroActivation = (GyroActivation)value; Saved(); NotifyGyro(); }
    }

    public int GyroButtonIndex
    {
        get => Math.Max(0, Array.IndexOf(GyroButtons, _cfg.GyroButton));
        set { _cfg.GyroButton = GyroButtons[Math.Clamp(value, 0, GyroButtons.Length - 1)]; Saved(); NotifyGyro(); }
    }

    public int GyroAxisIndex
    {
        get => (int)_cfg.GyroAxis;
        set { _cfg.GyroAxis = (GyroAxis)value; Saved(); }
    }

    public double GyroSensitivity
    {
        get => _cfg.GyroSensitivity;
        set { _cfg.GyroSensitivity = (int)Math.Round(value); Saved(); Notify(nameof(GyroSensitivityText)); }
    }

    public string GyroSensitivityText => _cfg.GyroSensitivity.ToString();

    public bool GyroInvertY
    {
        get => _cfg.GyroInvertY;
        set { _cfg.GyroInvertY = value; Saved(); }
    }

    public bool GyroOn => _cfg.GyroMode != GyroMode.Off;
    public bool GyroNeedsButton => _cfg.GyroActivation != GyroActivation.Always;
    public bool GyroEngaged => _service.Input.GyroEngaged;

    public string GyroSensitivityHint => _cfg.GyroMode == GyroMode.Stick
        ? "Higher reaches full stick with a smaller turn."
        : "Higher moves the mouse further for the same turn. Games with low mouse sensitivity need more.";

    public string GyroState
    {
        get
        {
            var mode = _cfg.Now.GyroMode;
            if (_cfg.Now.Profile is { GyroMode: null } game && _cfg.Now.NativeGyro && _cfg.GyroMode != GyroMode.Off)
                return $"Off for {game.Name}: the game has its own gyro aiming";
            if (_cfg.Now.Profile?.GyroMode is { } p && p != _cfg.GyroMode)
                return p == GyroMode.Off ? $"Off for {_cfg.Now.Profile.Name} (game setting)" : $"{ModeName(p)} for {_cfg.Now.Profile.Name} (game setting)";
            if (mode == GyroMode.Off) return "Off";
            string button = Pad.Name(_cfg.GyroButton);
            return _cfg.GyroActivation switch
            {
                GyroActivation.HoldToAim => $"{ModeName(mode)}: aims while {button} is held",
                GyroActivation.HoldToPause => $"{ModeName(mode)}: always on, {button} pauses it so you can re-centre",
                GyroActivation.Toggle => $"{ModeName(mode)}: {button} turns it on and off",
                _ => $"{ModeName(mode)}: always on",
            };
        }
    }

    static string ModeName(GyroMode m) => m == GyroMode.Stick ? "Right stick" : "Mouse";

    void NotifyGyro()
    {
        foreach (var p in new[]
                 {
                     nameof(GyroModeIndex), nameof(GyroActivationIndex), nameof(GyroButtonIndex), nameof(GyroOn),
                     nameof(GyroNeedsButton), nameof(GyroState), nameof(GyroSensitivityHint), nameof(GyroEngaged),
                 })
            Notify(p);
    }

    public int ProfileGyroMode
    {
        get => SelectedProfile?.GyroMode is { } m ? (int)m + 1 : 0;
        set
        {
            if (EditProfile is not { } profile) return;
            profile.GyroMode = value == 0 ? null : (GyroMode)(value - 1);
            ProfileChanged();
            Notify(nameof(GyroState));
        }
    }

    public bool OverrideGyroSensitivity
    {
        get => SelectedProfile?.GyroSensitivity != null;
        set
        {
            if (EditProfile is not { } profile) return;
            profile.GyroSensitivity = value ? _cfg.GyroSensitivity : null;
            ProfileChanged();
        }
    }

    public double ProfileGyroSensitivity
    {
        get => SelectedProfile?.GyroSensitivity ?? _cfg.GyroSensitivity;
        set
        {
            if (SelectedProfile?.GyroSensitivity == null) return;
            SelectedProfile.GyroSensitivity = (int)Math.Round(value);
            ProfileChanged();
        }
    }

    public string ProfileGyroSensitivityText => ((int)ProfileGyroSensitivity).ToString();
}
