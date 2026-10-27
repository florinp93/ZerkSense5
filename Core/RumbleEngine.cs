using System.Diagnostics;

namespace ZerkSense5;

sealed class RumbleEngine : IHapticsSource, IDisposable
{
    const double HeavyHz = 60, LightHz = 160;
    const double CrossFeed = 0.3;

    readonly Config _cfg;
    readonly Func<IController?> _bt;
    readonly HapticsMixer _mixer;
    volatile int _heavy, _light;
    double _ampHeavy, _ampLight, _phaseHeavy, _phaseLight;
    long _lastClassicTicks;
    (int, int) _lastClassic = (-1, -1);
    readonly Timer _classicFlush;

    public event Action<byte, byte>? Changed;

    public RumbleEngine(Config cfg, Func<IController?> bt, HapticsMixer mixer)
    {
        _cfg = cfg;
        _bt = bt;
        _mixer = mixer;
        _classicFlush = new Timer(_ => SendClassic(force: false), null, Timeout.Infinite, Timeout.Infinite);
        mixer.Add(this);
    }

    bool Hd => _cfg.RumbleStyle == 0;
    double Strength => Math.Clamp(_cfg.RumbleStrength, 0, 200) / 100.0;

    public void Set(byte heavy, byte light)
    {
        _heavy = heavy;
        _light = light;
        Changed?.Invoke(heavy, light);
        if (Hd) _mixer.Wake();
        else SendClassic(force: false);
    }

    public void Stop()
    {
        _heavy = _light = 0;
        Changed?.Invoke(0, 0);
        SendClassic(force: true);
    }

    public bool Active => Hd && (_heavy > 0 || _light > 0 || _ampHeavy > 0.002 || _ampLight > 0.002);

    public void Render(Span<float> left, Span<float> right)
    {
        double k = Strength;
        double targetHeavy = _heavy / 255.0, targetLight = _light / 255.0;
        for (int i = 0; i < left.Length; i++)
        {
            _ampHeavy += (targetHeavy - _ampHeavy) * 0.07;
            _ampLight += (targetLight - _ampLight) * 0.07;
            double h = Math.Sin(_phaseHeavy) * _ampHeavy, l = Math.Sin(_phaseLight) * _ampLight;
            _phaseHeavy = (_phaseHeavy + 2 * Math.PI * HeavyHz / HapticsMixer.Rate) % (2 * Math.PI);
            _phaseLight = (_phaseLight + 2 * Math.PI * LightHz / HapticsMixer.Rate) % (2 * Math.PI);
            left[i] += (float)((h + l * CrossFeed) * k);
            right[i] += (float)((l + h * CrossFeed) * k);
        }
        if (targetHeavy == 0 && _ampHeavy < 0.002) _ampHeavy = 0;
        if (targetLight == 0 && _ampLight < 0.002) _ampLight = 0;
    }

    void SendClassic(bool force)
    {
        var bt = _bt();
        if (bt == null) return;
        int heavy = (int)Math.Min(255, _heavy * Strength), light = (int)Math.Min(255, _light * Strength);
        if (Hd && !force) return;
        if (!force && (heavy, light) == _lastClassic) return;

        long now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastClassicTicks, now).TotalMilliseconds < 10)
        {
            _classicFlush.Change(12, Timeout.Infinite);
            return;
        }
        _lastClassicTicks = now;
        _lastClassic = (heavy, light);
        var s = new StateData
        {
            EnableRumbleEmulation = true,
            UseRumbleNotHaptics = true,
            EnableImprovedRumbleEmulation = true,
            RumbleLeft = (byte)heavy,
            RumbleRight = (byte)light,
        };
        bt.SendState(s.ToReport31());
    }

    public void Dispose() => _classicFlush.Dispose();
}
