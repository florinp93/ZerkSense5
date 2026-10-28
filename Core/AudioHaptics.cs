namespace ZerkSense5;

sealed class AudioHaptics : IHapticsSource
{
    const int Decimation = LoopbackCapture.SampleRate / HapticsMixer.Rate;
    const double CarrierHz = 90;
    const int RingSize = HapticsMixer.Rate / 5;
    const int TargetLatency = HapticsMixer.Rate * 30 / 1000;

    readonly Config _cfg;
    readonly HapticsMixer _mixer;
    readonly Biquad[] _lowL = [new(), new()], _lowR = [new(), new()];
    double _envL, _envR, _carrier;
    int _decimate;
    float _cutoff;

    readonly float[] _ringL = new float[RingSize], _ringR = new float[RingSize];
    int _read, _write, _count;
    readonly object _lock = new();

    public AudioHaptics(Config cfg, HapticsMixer mixer)
    {
        _cfg = cfg;
        _mixer = mixer;
        mixer.Add(this);
    }

    public volatile bool Enabled;

    public float Level { get; private set; }

    public bool Active
    {
        get
        {
            if (!Enabled) return false;
            lock (_lock) return _count > 0;
        }
    }

    public void Feed(ReadOnlySpan<float> frames)
    {
        if (!Enabled) return;
        UpdateFilters();
        double attack = Coef(3), release = Coef(Math.Clamp(_cfg.AudioHapticsSmoothness, 20, 400));
        double gate = _cfg.AudioHapticsGate / 100.0 * 0.1;
        double gain = _cfg.AudioHapticsIntensity / 100.0 * 6;
        int n = frames.Length / 2;
        float level = 0;
        lock (_lock)
        {
            for (int i = 0; i < n; i++)
            {
                double l = _lowL[1].Run(_lowL[0].Run(frames[i * 2]));
                double r = _lowR[1].Run(_lowR[0].Run(frames[i * 2 + 1]));
                _envL = Follow(_envL, Math.Abs(l), attack, release);
                _envR = Follow(_envR, Math.Abs(r), attack, release);
                if (++_decimate < Decimation) continue;
                _decimate = 0;

                double carrier = Math.Sin(_carrier);
                _carrier = (_carrier + 2 * Math.PI * CarrierHz / HapticsMixer.Rate) % (2 * Math.PI);
                float outL = (float)Math.Min(1.0, Gate(_envL, gate) * gain);
                float outR = (float)Math.Min(1.0, Gate(_envR, gate) * gain);
                level = Math.Max(level, Math.Max(outL, outR));
                Write((float)(outL * carrier), (float)(outR * carrier));
            }
            while (_count > TargetLatency * 2)
            {
                _read = (_read + 1) % RingSize;
                _count--;
            }
        }
        Level = level;
        if (level > 0) _mixer.Wake();
    }

    static double Gate(double env, double threshold) => env <= threshold ? 0 : env - threshold;

    static double Follow(double env, double x, double attack, double release) =>
        x > env ? env + (x - env) * attack : env + (x - env) * release;

    static double Coef(double ms) => 1 - Math.Exp(-1000.0 / (ms * LoopbackCapture.SampleRate));

    void Write(float l, float r)
    {
        _ringL[_write] = l;
        _ringR[_write] = r;
        _write = (_write + 1) % RingSize;
        if (_count < RingSize) _count++;
        else _read = (_read + 1) % RingSize;
    }

    public void Render(Span<float> left, Span<float> right)
    {
        lock (_lock)
        {
            for (int i = 0; i < left.Length && _count > 0; i++)
            {
                left[i] += _ringL[_read];
                right[i] += _ringR[_read];
                _read = (_read + 1) % RingSize;
                _count--;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _count = _read = _write = 0;
            _envL = _envR = 0;
        }
        Level = 0;
    }

    void UpdateFilters()
    {
        float cutoff = Math.Clamp(_cfg.AudioHapticsCutoff, 30, 250);
        if (cutoff == _cutoff) return;
        _cutoff = cutoff;
        foreach (var b in _lowL.Concat(_lowR)) b.LowPass(cutoff, LoopbackCapture.SampleRate);
    }

    sealed class Biquad
    {
        double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public void LowPass(double hz, double rate)
        {
            double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            double a0 = 1 + alpha;
            _b0 = (1 - cos) / 2 / a0;
            _b1 = (1 - cos) / a0;
            _b2 = _b0;
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }

        public double Run(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
