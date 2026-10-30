namespace ZerkSense5;

enum LightingMode { Static, Breathing, Rainbow, Battery, AudioReactive }

sealed class LightingEngine : IDisposable
{
    const int IntervalMs = 40;

    readonly Config _cfg;
    readonly Func<IController?> _bt;
    readonly Func<ControllerStatus?> _status;
    readonly Func<float> _audioLevel;
    readonly Timer _timer;
    readonly DateTime _start = DateTime.UtcNow;
    long _gameLightbarUntil;
    (byte, byte, byte) _last = (1, 2, 3);
    float _smoothedAudio;

    public LightingEngine(Config cfg, Func<IController?> bt, Func<ControllerStatus?> status, Func<float> audioLevel)
    {
        _cfg = cfg;
        _bt = bt;
        _status = status;
        _audioLevel = audioLevel;
        _timer = new Timer(_ => Tick(), null, IntervalMs, IntervalMs);
    }

    public void NoteGameLightbar() => Interlocked.Exchange(ref _gameLightbarUntil, Environment.TickCount64 + 3000);

    public void Invalidate() => _last = (1, 2, 3);

    void Tick()
    {
        var mode = (LightingMode)_cfg.LightingMode;
        var bt = _bt();
        if (bt == null || mode == LightingMode.Static) return;
        if (Environment.TickCount64 < Interlocked.Read(ref _gameLightbarUntil)) return;

        var c = Color(mode, (DateTime.UtcNow - _start).TotalSeconds);
        if (c == _last) return;
        _last = c;
        bt.SendState(new StateData { AllowLedColor = true, LedRed = c.Item1, LedGreen = c.Item2, LedBlue = c.Item3 }.ToReport32());
    }

    (byte, byte, byte) Color(LightingMode mode, double t)
    {
        var baseColor = _cfg.Now.LightbarColor;
        double speed = Math.Clamp(_cfg.LightingSpeed, 1, 10) / 5.0;
        switch (mode)
        {
            case LightingMode.Breathing:
            {
                double k = 0.08 + 0.92 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t * speed / 4));
                return Scale(baseColor, k);
            }
            case LightingMode.Rainbow:
                return Hsv(t * 40 * speed % 360, 1, 1);
            case LightingMode.Battery:
            {
                var st = _status();
                if (st == null) return (0, 0, 0);
                double hue = Math.Clamp(st.BatteryPercent, 0, 100) * 1.2;
                double k = st.Power == PowerState.Charging ? 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 2)) : 1;
                return Hsv(hue, 1, k);
            }
            case LightingMode.AudioReactive:
            {
                float level = _audioLevel();
                _smoothedAudio = level > _smoothedAudio ? level : _smoothedAudio * 0.85f;
                return Scale(baseColor, 0.06 + 0.94 * Math.Min(1, _smoothedAudio * 1.5));
            }
        }
        return ((byte)baseColor[0], (byte)baseColor[1], (byte)baseColor[2]);
    }

    static (byte, byte, byte) Scale(byte[] c, double k) =>
        ((byte)(c[0] * k), (byte)(c[1] * k), (byte)(c[2] * k));

    static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public void Dispose() => _timer.Dispose();
}
