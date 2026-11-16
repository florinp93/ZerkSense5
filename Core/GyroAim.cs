using System.Buffers.Binary;

namespace ZerkSense5;

enum GyroMode { Off, Mouse, Stick }

enum GyroActivation { Always, HoldToAim, HoldToPause, Toggle }

enum GyroAxis { Yaw, Roll, Both }

sealed class GyroAim
{
    const double FallbackScale = 1 / 16.384;
    const double Tighten = 1.5;
    const double StillGyro = 1.5, StillAccel = 0.03, StillSeconds = 1.0, BiasSeconds = 2.0;
    const double StickAntiDeadzone = 0.15;
    const double StickRange = 600;

    readonly Config _cfg;
    readonly Action<int, int> _moveMouse;
    readonly double[] _bias = new double[3], _scale = [FallbackScale, FallbackScale, FallbackScale];
    readonly double[] _drift = new double[3];
    readonly double[] _accelAvg = new double[3];
    IController? _calibratedFor;
    uint _lastStamp;
    bool _haveStamp, _toggled, _wasHeld, _haveAccel;
    double _still, _fracX, _fracY;

    public GyroAim(Config cfg, Action<int, int>? moveMouse = null)
    {
        _cfg = cfg;
        _moveMouse = moveMouse ?? NativeInput.MoveMouse;
    }

    public GyroMode Mode => _cfg.Now.GyroMode;
    public bool Engaged { get; private set; }
    public bool Calibrated { get; private set; }

    public void Reset(IController? bt)
    {
        _calibratedFor = null;
        if (bt != null) LoadCalibration(bt);
        _haveStamp = _haveAccel = _toggled = _wasHeld = false;
        _still = _fracX = _fracY = 0;
        Array.Clear(_drift);
        Engaged = false;
    }

    void LoadCalibration(IController bt)
    {
        _calibratedFor = bt;
        Array.Clear(_bias);
        Array.Fill(_scale, FallbackScale);
        Calibrated = false;
        var c = bt.GetFeatureCached(0x05);
        if (c is not { Length: >= 22 }) return;
        short S(int o) => BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(o));
        double speed = S(18) + S(20);
        double[] span = [S(6) - S(8), S(10) - S(12), S(14) - S(16)];
        if (speed <= 0 || span.Any(x => Math.Abs(x) < 1000)) return;
        for (int i = 0; i < 3; i++)
        {
            _bias[i] = S(i * 2);
            _scale[i] = speed / Math.Abs(span[i]);
        }
        Calibrated = true;
    }

    public bool Process(Span<byte> s, uint held, IController? bt)
    {
        var mode = _cfg.Now.GyroMode;
        if (mode == GyroMode.Off)
        {
            if (Engaged) Engaged = false;
            _haveStamp = false;
            return false;
        }
        if (bt != null && !ReferenceEquals(bt, _calibratedFor)) LoadCalibration(bt);

        uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(s[27..]);
        double dt = _haveStamp ? Math.Clamp(unchecked(stamp - _lastStamp) / 3e6, 0, 0.05) : 0;
        _lastStamp = stamp;
        _haveStamp = true;

        Span<double> rate = stackalloc double[3];
        for (int i = 0; i < 3; i++)
            rate[i] = (BinaryPrimitives.ReadInt16LittleEndian(s[(15 + i * 2)..]) - _bias[i]) * _scale[i];
        TrackDrift(s, rate, dt);
        for (int i = 0; i < 3; i++) rate[i] -= _drift[i];

        bool active = Active(held);
        Engaged = active;
        if (!active || dt == 0)
        {
            _fracX = _fracY = 0;
            return false;
        }

        double pitch = rate[0], yaw = rate[1], roll = rate[2];
        double h = _cfg.GyroAxis switch
        {
            GyroAxis.Roll => -roll,
            GyroAxis.Both => -yaw - roll,
            _ => -yaw,
        };
        double v = _cfg.GyroInvertY ? -pitch : pitch;
        double mag = Math.Sqrt(h * h + v * v);
        if (mag < Tighten)
        {
            double k = mag / Tighten;
            h *= k;
            v *= k;
        }

        double sens = _cfg.Now.GyroSensitivity;
        if (mode == GyroMode.Mouse)
        {
            _fracX += h * dt * sens;
            _fracY -= v * dt * sens;
            int mx = (int)_fracX, my = (int)_fracY;
            _fracX -= mx;
            _fracY -= my;
            if (mx != 0 || my != 0) _moveMouse(mx, my);
            return false;
        }

        double gx = h * sens / StickRange, gy = v * sens / StickRange;
        double g = Math.Sqrt(gx * gx + gy * gy);
        if (g < 1e-4) return false;
        double outMag = StickAntiDeadzone + (1 - StickAntiDeadzone) * Math.Min(1, g);
        gx *= outMag / g;
        gy *= outMag / g;
        s[2] = Stick((s[2] - 128) / 127.0 + gx);
        s[3] = Stick((s[3] - 128) / 127.0 - gy);
        return true;
    }

    static byte Stick(double x) => (byte)Math.Clamp(Math.Round(128 + Math.Clamp(x, -1, 1) * 127), 0, 255);

    bool Active(uint held)
    {
        bool down = (held & (1u << (int)_cfg.GyroButton)) != 0;
        bool pressed = down && !_wasHeld;
        _wasHeld = down;
        return _cfg.GyroActivation switch
        {
            GyroActivation.HoldToAim => down,
            GyroActivation.HoldToPause => !down,
            GyroActivation.Toggle => pressed ? _toggled = !_toggled : _toggled,
            _ => true,
        };
    }

    void TrackDrift(ReadOnlySpan<byte> s, ReadOnlySpan<double> rate, double dt)
    {
        Span<double> accel = stackalloc double[3];
        for (int i = 0; i < 3; i++) accel[i] = BinaryPrimitives.ReadInt16LittleEndian(s[(21 + i * 2)..]);
        if (!_haveAccel)
        {
            accel.CopyTo(_accelAvg);
            _haveAccel = true;
            return;
        }
        double g = Math.Sqrt(_accelAvg[0] * _accelAvg[0] + _accelAvg[1] * _accelAvg[1] + _accelAvg[2] * _accelAvg[2]);
        bool steady = g > 1;
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(accel[i] - _accelAvg[i]) > StillAccel * g) steady = false;
            if (Math.Abs(rate[i] - _drift[i]) > StillGyro) steady = false;
            _accelAvg[i] += (accel[i] - _accelAvg[i]) * Math.Min(1, dt * 4);
        }
        _still = steady ? _still + dt : 0;
        if (_still < StillSeconds) return;
        double k = Math.Min(1, dt / BiasSeconds);
        for (int i = 0; i < 3; i++) _drift[i] += (rate[i] - _drift[i]) * k;
    }
}
