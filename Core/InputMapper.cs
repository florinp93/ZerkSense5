using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ZerkSense5;

enum PadButton
{
    Cross, Circle, Square, Triangle,
    Up, Down, Left, Right,
    L1, R1, L2, R2, L3, R3,
    Create, Options, PS, Touchpad, Mic,
    FnLeft, FnRight, PaddleLeft, PaddleRight,
}

enum BindingAction
{
    Key,
    MouseLeft, MouseRight, MouseMiddle, MouseBack, MouseForward,
    WheelUp, WheelDown,
    TurnOffController,
    ToggleTouchpadMouse,
    Nothing,
}

sealed class ButtonBinding
{
    public List<PadButton> Buttons { get; set; } = new();
    public BindingAction Action { get; set; }
    public int Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool PassThrough { get; set; }

    public ButtonBinding Clone() => new()
    {
        Buttons = new List<PadButton>(Buttons),
        Action = Action,
        Key = Key,
        Ctrl = Ctrl,
        Shift = Shift,
        Alt = Alt,
        PassThrough = PassThrough,
    };

    [JsonIgnore] public string ButtonsText => Buttons.Count == 0 ? "No buttons" : string.Join(" + ", Buttons.Select(Pad.Name));

    [JsonIgnore]
    public string ActionText => Action switch
    {
        BindingAction.Key => Key == 0 ? "Pick a key" : KeyText,
        BindingAction.MouseLeft => "Left click",
        BindingAction.MouseRight => "Right click",
        BindingAction.MouseMiddle => "Middle click",
        BindingAction.MouseBack => "Mouse back",
        BindingAction.MouseForward => "Mouse forward",
        BindingAction.WheelUp => "Scroll up",
        BindingAction.WheelDown => "Scroll down",
        BindingAction.TurnOffController => "Turn off the controller",
        BindingAction.ToggleTouchpadMouse => "Turn touchpad mouse on/off",
        BindingAction.Nothing => "Disable the button",
        _ => "",
    };

    [JsonIgnore]
    public string KeyText => string.Join(" + ",
        new[] { Ctrl ? "Ctrl" : null, Shift ? "Shift" : null, Alt ? "Alt" : null, Key != 0 ? NativeInput.KeyName(Key) : null }.Where(s => s != null));
}

static class Pad
{
    public static readonly PadButton[] All = Enum.GetValues<PadButton>();

    public static string Name(PadButton b) => b switch
    {
        PadButton.Up => "D-pad up",
        PadButton.Down => "D-pad down",
        PadButton.Left => "D-pad left",
        PadButton.Right => "D-pad right",
        PadButton.FnLeft => "Edge Fn left",
        PadButton.FnRight => "Edge Fn right",
        PadButton.PaddleLeft => "Edge left paddle",
        PadButton.PaddleRight => "Edge right paddle",
        PadButton.Touchpad => "Touchpad click",
        _ => b.ToString(),
    };

    // Byte offset in the 63-byte input state (after the 2-byte Bluetooth header) and bit mask.
    static (int offset, byte mask) Bit(PadButton b) => b switch
    {
        PadButton.Square => (7, 0x10),
        PadButton.Cross => (7, 0x20),
        PadButton.Circle => (7, 0x40),
        PadButton.Triangle => (7, 0x80),
        PadButton.L1 => (8, 0x01),
        PadButton.R1 => (8, 0x02),
        PadButton.L2 => (8, 0x04),
        PadButton.R2 => (8, 0x08),
        PadButton.Create => (8, 0x10),
        PadButton.Options => (8, 0x20),
        PadButton.L3 => (8, 0x40),
        PadButton.R3 => (8, 0x80),
        PadButton.PS => (9, 0x01),
        PadButton.Touchpad => (9, 0x02),
        PadButton.Mic => (9, 0x04),
        PadButton.FnLeft => (9, 0x10),
        PadButton.FnRight => (9, 0x20),
        PadButton.PaddleLeft => (9, 0x40),
        PadButton.PaddleRight => (9, 0x80),
        _ => (-1, 0),
    };

    public static uint Read(ReadOnlySpan<byte> s)
    {
        uint mask = 0;
        foreach (var b in All)
        {
            var (o, m) = Bit(b);
            if (o >= 0 && (s[o] & m) != 0) mask |= 1u << (int)b;
        }
        int hat = s[7] & 0x0F;
        if (hat is 7 or 0 or 1) mask |= 1u << (int)PadButton.Up;
        if (hat is 1 or 2 or 3) mask |= 1u << (int)PadButton.Right;
        if (hat is 3 or 4 or 5) mask |= 1u << (int)PadButton.Down;
        if (hat is 5 or 6 or 7) mask |= 1u << (int)PadButton.Left;
        return mask;
    }

    public static void Clear(Span<byte> s, uint mask)
    {
        if (mask == 0) return;
        foreach (var b in All)
        {
            if ((mask & (1u << (int)b)) == 0) continue;
            var (o, m) = Bit(b);
            if (o >= 0) s[o] &= (byte)~m;
            if (b == PadButton.L2) s[4] = 0;
            if (b == PadButton.R2) s[5] = 0;
        }
        const uint dpad = 1u << (int)PadButton.Up | 1u << (int)PadButton.Down | 1u << (int)PadButton.Left | 1u << (int)PadButton.Right;
        if ((mask & dpad) == 0) return;
        uint held = Read(s) & dpad & ~mask;
        bool up = (held & 1u << (int)PadButton.Up) != 0, down = (held & 1u << (int)PadButton.Down) != 0;
        bool left = (held & 1u << (int)PadButton.Left) != 0, right = (held & 1u << (int)PadButton.Right) != 0;
        int hat = (up, right, down, left) switch
        {
            (true, false, false, false) => 0,
            (true, true, false, false) => 1,
            (false, true, false, false) => 2,
            (false, true, true, false) => 3,
            (false, false, true, false) => 4,
            (false, false, true, true) => 5,
            (false, false, false, true) => 6,
            (true, false, false, true) => 7,
            _ => 8,
        };
        s[7] = (byte)((s[7] & 0xF0) | hat);
    }

    public static uint Mask(IEnumerable<PadButton> buttons) => buttons.Aggregate(0u, (m, b) => m | 1u << (int)b);
}

sealed class InputMapper : IController
{
    const int TouchWidth = 1920;
    const int TapMs = 180, TapSlop = 40;
    const double ScrollStep = 70;
    static readonly List<ButtonBinding> NoBindings = new();

    readonly Config _cfg;
    readonly Action<IController> _turnOff;
    readonly byte[] _out = new byte[DualSenseBt.BtInputLength];
    readonly Dictionary<ButtonBinding, ButtonBinding> _active = new(ReferenceEqualityComparer.Instance);
    readonly object _lock = new();
    volatile IController? _bt;
    volatile bool _touchToggled;

    bool _touching;
    int _lastId = -1, _lastX, _lastY, _fingers, _maxFingers;
    long _touchStart, _lastTicks;
    double _fracX, _fracY, _scroll, _travel;
    bool _clicked;
    MouseButton? _padButton;
    bool _wasPadMouse;

    public InputMapper(Config cfg, Action<IController> turnOff)
    {
        _cfg = cfg;
        _turnOff = turnOff;
    }

    public event Action<byte[], int>? InputReport;
    public event Action? Changed;

    public bool TouchpadMouseOn => _cfg.Now.TouchpadMouse ^ _touchToggled;

    public void Attach(IController bt)
    {
        _bt = bt;
        bt.InputReport += OnInput;
    }

    public void Detach()
    {
        if (_bt is { } bt) bt.InputReport -= OnInput;
        _bt = null;
        lock (_lock) ReleaseAll();
    }

    public void ResetToggle()
    {
        if (!_touchToggled) return;
        _touchToggled = false;
        Changed?.Invoke();
    }

    public byte[]? GetFeatureLive(byte id) => _bt?.GetFeatureLive(id);
    public byte[]? GetFeatureCached(byte id) => _bt?.GetFeatureCached(id);
    public bool SetFeature(byte id, ReadOnlySpan<byte> body) => _bt?.SetFeature(id, body) ?? false;
    public void SendState(byte[] report) => _bt?.SendState(report);
    public void SendAudio(byte[] report) => _bt?.SendAudio(report);

    void OnInput(byte[] buf, int len)
    {
        if (len < 2 + 63 || buf[0] != 0x31 || (buf[1] & 0x02) != 0)
        {
            InputReport?.Invoke(buf, len);
            return;
        }

        List<ButtonBinding> bindings = _cfg.Now.BindingsEnabled ? _cfg.Bindings : NoBindings;
        bool pad = TouchpadMouseOn;
        lock (_lock)
        {
            if (bindings.Count == 0 && !pad && _active.Count == 0 && !_wasPadMouse)
            {
                InputReport?.Invoke(buf, len);
                return;
            }

            int n = Math.Min(len, _out.Length);
            Buffer.BlockCopy(buf, 0, _out, 0, n);
            var s = _out.AsSpan(2, 63);
            uint held = Pad.Read(s);
            uint consumed = Bindings(bindings, held);

            if (pad) Touchpad(s);
            else if (_wasPadMouse) StopTouchpad();
            _wasPadMouse = pad;
            if (pad)
            {
                consumed |= 1u << (int)PadButton.Touchpad;
                s[32] |= 0x80;
                s[36] |= 0x80;
            }

            Pad.Clear(s, consumed);
            InputReport?.Invoke(_out, n);
        }
    }

    uint Bindings(List<ButtonBinding> bindings, uint held)
    {
        uint consumed = 0;
        foreach (var (live, fired) in _active.ToList())
        {
            uint need = Pad.Mask(fired.Buttons);
            if (!bindings.Contains(live) || (held & need) != need)
            {
                Fire(fired, down: false);
                _active.Remove(live);
            }
        }
        foreach (var b in bindings)
        {
            if (b.Buttons.Count == 0) continue;
            uint need = Pad.Mask(b.Buttons);
            if ((held & need) != need) continue;
            if (!_active.TryGetValue(b, out var fired))
            {
                fired = b.Clone();
                _active[b] = fired;
                Fire(fired, down: true);
            }
            if (!fired.PassThrough) consumed |= need;
        }
        return consumed;
    }

    void Fire(ButtonBinding b, bool down)
    {
        switch (b.Action)
        {
            case BindingAction.Key when b.Key != 0:
                if (down)
                {
                    if (b.Ctrl) NativeInput.KeyDown(0xA2);
                    if (b.Shift) NativeInput.KeyDown(0xA0);
                    if (b.Alt) NativeInput.KeyDown(0xA4);
                    NativeInput.KeyDown(b.Key);
                }
                else
                {
                    NativeInput.KeyUp(b.Key);
                    if (b.Alt) NativeInput.KeyUp(0xA4);
                    if (b.Shift) NativeInput.KeyUp(0xA0);
                    if (b.Ctrl) NativeInput.KeyUp(0xA2);
                }
                break;
            case BindingAction.MouseLeft: NativeInput.Mouse(MouseButton.Left, down); break;
            case BindingAction.MouseRight: NativeInput.Mouse(MouseButton.Right, down); break;
            case BindingAction.MouseMiddle: NativeInput.Mouse(MouseButton.Middle, down); break;
            case BindingAction.MouseBack: NativeInput.Mouse(MouseButton.Back, down); break;
            case BindingAction.MouseForward: NativeInput.Mouse(MouseButton.Forward, down); break;
            case BindingAction.WheelUp when down: NativeInput.Wheel(NativeInput.WheelDelta); break;
            case BindingAction.WheelDown when down: NativeInput.Wheel(-NativeInput.WheelDelta); break;
            case BindingAction.ToggleTouchpadMouse when down:
                _touchToggled = !_touchToggled;
                Log.Info(TouchpadMouseOn ? "Touchpad mouse on" : "Touchpad mouse off");
                Changed?.Invoke();
                break;
            case BindingAction.TurnOffController when down:
                ThreadPool.QueueUserWorkItem(_ => _turnOff(this));
                break;
        }
    }

    void Touchpad(ReadOnlySpan<byte> s)
    {
        bool t0 = (s[32] & 0x80) == 0, t1 = (s[36] & 0x80) == 0;
        int fingers = (t0 ? 1 : 0) + (t1 ? 1 : 0);
        int o = t0 ? 32 : 36;
        int id = s[o] & 0x7F;
        int x = s[o + 1] | (s[o + 2] & 0x0F) << 8, y = s[o + 2] >> 4 | s[o + 3] << 4;
        long now = Stopwatch.GetTimestamp();
        bool click = (s[9] & 0x02) != 0;

        if (click && _padButton == null)
        {
            _padButton = fingers >= 2 || _cfg.TouchpadRightClickZone && fingers == 1 && x >= TouchWidth / 2 ? MouseButton.Right : MouseButton.Left;
            NativeInput.Mouse(_padButton.Value, true);
            _clicked = true;
        }
        else if (!click && _padButton is { } pressed)
        {
            NativeInput.Mouse(pressed, false);
            _padButton = null;
        }

        if (fingers == 0)
        {
            if (_touching && _cfg.TouchpadTapToClick && !_clicked && _travel < TapSlop
                && Stopwatch.GetElapsedTime(_touchStart, now).TotalMilliseconds < TapMs)
            {
                var b = _maxFingers >= 2 ? MouseButton.Right : MouseButton.Left;
                NativeInput.Mouse(b, true);
                NativeInput.Mouse(b, false);
            }
            _touching = false;
            _lastId = -1;
            _fingers = 0;
            _scroll = 0;
            return;
        }

        if (!_touching)
        {
            _touching = true;
            _touchStart = now;
            _maxFingers = 0;
            _travel = 0;
            _clicked = click;
        }
        _maxFingers = Math.Max(_maxFingers, fingers);

        if (id != _lastId || fingers != _fingers)
        {
            _lastId = id;
            _fingers = fingers;
            _lastX = x;
            _lastY = y;
            _lastTicks = now;
            _fracX = _fracY = 0;
            return;
        }

        int dx = x - _lastX, dy = y - _lastY;
        double ms = Math.Max(0.5, Stopwatch.GetElapsedTime(_lastTicks, now).TotalMilliseconds);
        _lastX = x;
        _lastY = y;
        _lastTicks = now;
        _travel += Math.Abs(dx) + Math.Abs(dy);
        if (dx == 0 && dy == 0) return;

        if (fingers >= 2)
        {
            _scroll -= dy;
            int notches = (int)(_scroll / ScrollStep);
            if (notches != 0)
            {
                _scroll -= notches * ScrollStep;
                NativeInput.Wheel(notches * NativeInput.WheelDelta);
            }
            return;
        }

        double speed = Math.Sqrt(dx * dx + dy * dy) / ms;
        double gain = 0.35 * Math.Clamp(_cfg.TouchpadSpeed, 1, 10) / 5.0 * (1 + Math.Clamp(speed / 3.0, 0, 2));
        _fracX += dx * gain;
        _fracY += dy * gain;
        int mx = (int)_fracX, my = (int)_fracY;
        _fracX -= mx;
        _fracY -= my;
        NativeInput.MoveMouse(mx, my);
    }

    void StopTouchpad()
    {
        if (_padButton is { } b) NativeInput.Mouse(b, false);
        _padButton = null;
        _touching = false;
        _lastId = -1;
        _fingers = 0;
    }

    void ReleaseAll()
    {
        foreach (var fired in _active.Values) Fire(fired, down: false);
        _active.Clear();
        StopTouchpad();
        _wasPadMouse = false;
    }
}
