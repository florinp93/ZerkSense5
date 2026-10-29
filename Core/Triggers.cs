using System.Text.Json.Serialization;

namespace ZerkSense5;

enum TriggerEffectType { Off, Resistance, Slope, Weapon, Bow, Vibration, Captured }

sealed class TriggerEffect
{
    public TriggerEffectType Type { get; set; } = TriggerEffectType.Resistance;
    public int Start { get; set; } = 2;
    public int End { get; set; } = 6;
    public int Strength { get; set; } = 5;
    public int EndStrength { get; set; } = 8;
    public int Frequency { get; set; } = 30;
    public byte[]? Raw { get; set; }
    public string? Name { get; set; }

    public TriggerEffect Clone() => (TriggerEffect)MemberwiseClone();

    public byte[] Encode()
    {
        var e = new byte[11];
        int s = Math.Clamp(Strength, 1, 8), start = Math.Clamp(Start, 0, 9);
        switch (Type)
        {
            case TriggerEffectType.Off:
                e[0] = 0x05;
                break;
            case TriggerEffectType.Resistance:
                return Zones(0x21, Enumerable.Range(0, 10).Select(i => i >= start ? s : 0).ToArray());
            case TriggerEffectType.Slope:
                return Zones(0x21, SlopeCurve());
            case TriggerEffectType.Weapon:
            {
                start = Math.Clamp(Start, 2, 7);
                int end = Math.Clamp(End, start + 1, 8);
                int zones = (1 << start) | (1 << end);
                e[0] = 0x25;
                e[1] = (byte)zones;
                e[2] = (byte)(zones >> 8);
                e[3] = (byte)(s - 1);
                break;
            }
            case TriggerEffectType.Bow:
            {
                start = Math.Clamp(Start, 0, 8);
                int end = Math.Clamp(End, start + 1, 9);
                int zones = (1 << start) | (1 << end);
                int force = (s - 1) | ((Math.Clamp(EndStrength, 1, 8) - 1) << 3);
                e[0] = 0x22;
                e[1] = (byte)zones;
                e[2] = (byte)(zones >> 8);
                e[3] = (byte)force;
                e[4] = (byte)(force >> 8);
                break;
            }
            case TriggerEffectType.Vibration:
            {
                var z = Zones(0x26, Enumerable.Range(0, 10).Select(i => i >= start ? s : 0).ToArray());
                z[9] = (byte)Math.Clamp(Frequency, 1, 255);
                return z;
            }
            case TriggerEffectType.Captured when Raw is { Length: 11 }:
                return (byte[])Raw.Clone();
            default:
                e[0] = 0x05;
                break;
        }
        return e;
    }

    public int[] Curve() => Type switch
    {
        TriggerEffectType.Resistance or TriggerEffectType.Vibration =>
            Enumerable.Range(0, 10).Select(i => i >= Math.Clamp(Start, 0, 9) ? Math.Clamp(Strength, 1, 8) : 0).ToArray(),
        TriggerEffectType.Slope => SlopeCurve(),
        TriggerEffectType.Weapon or TriggerEffectType.Bow =>
            Enumerable.Range(0, 10).Select(i => i >= Start && i <= End ? Math.Clamp(Strength, 1, 8) : 0).ToArray(),
        TriggerEffectType.Captured when Raw is { Length: 11 } => CapturedCurve(Raw),
        _ => new int[10],
    };

    int[] SlopeCurve()
    {
        int start = Math.Clamp(Start, 0, 8), end = Math.Clamp(End, start + 1, 9);
        int a = Math.Clamp(Strength, 1, 8), b = Math.Clamp(EndStrength, 1, 8);
        var c = new int[10];
        for (int i = 0; i < 10; i++)
            c[i] = i < start ? 0 : i >= end ? b : (int)Math.Round(a + (b - a) * (i - start) / (double)(end - start));
        return c;
    }

    static byte[] Zones(byte mode, int[] strengths)
    {
        int active = 0;
        uint values = 0;
        for (int i = 0; i < 10; i++)
        {
            if (strengths[i] <= 0) continue;
            active |= 1 << i;
            values |= (uint)((strengths[i] - 1) & 7) << (3 * i);
        }
        var e = new byte[11];
        e[0] = mode;
        e[1] = (byte)active;
        e[2] = (byte)(active >> 8);
        e[3] = (byte)values;
        e[4] = (byte)(values >> 8);
        e[5] = (byte)(values >> 16);
        e[6] = (byte)(values >> 24);
        return e;
    }

    static int[] CapturedCurve(byte[] raw)
    {
        var c = new int[10];
        int active = raw[1] | raw[2] << 8;
        if (raw[0] is 0x21 or 0x26)
        {
            uint values = (uint)(raw[3] | raw[4] << 8 | raw[5] << 16 | raw[6] << 24);
            for (int i = 0; i < 10; i++)
                if ((active & (1 << i)) != 0) c[i] = (int)((values >> (3 * i)) & 7) + 1;
        }
        else if (raw[0] is 0x22 or 0x25)
        {
            int first = -1, last = -1;
            for (int i = 0; i < 10; i++)
                if ((active & (1 << i)) != 0)
                {
                    if (first < 0) first = i;
                    last = i;
                }
            for (int i = Math.Max(first, 0); i <= last; i++) c[i] = (raw[3] & 7) + 1;
        }
        else if (raw[0] != 0x05 && raw[0] != 0x00)
        {
            Array.Fill(c, 4);
        }
        return c;
    }

    [JsonIgnore]
    public string Summary => Type switch
    {
        TriggerEffectType.Off => "Off",
        TriggerEffectType.Resistance => $"Resistance from {Start * 10}% · strength {Strength}",
        TriggerEffectType.Slope => $"Slope {Start * 10}–{End * 10}% · {Strength}→{EndStrength}",
        TriggerEffectType.Weapon => $"Weapon click {Start * 10}–{End * 10}% · strength {Strength}",
        TriggerEffectType.Bow => $"Bow {Start * 10}–{End * 10}% · pull {Strength}, snap {EndStrength}",
        TriggerEffectType.Vibration => $"Vibration from {Start * 10}% · {Strength} at {Frequency} Hz",
        TriggerEffectType.Captured => $"Captured: {Name ?? "game effect"}",
        _ => "",
    };
}

sealed class TriggerManager
{
    readonly Config _cfg;
    readonly Func<IController?> _bt;
    readonly byte[][] _captured = new byte[2][];
    bool[] _customApplied = new bool[2];
    int _kickLevel;
    long _lastKick;

    public event Action<int, byte[]>? Captured;

    public TriggerManager(Config cfg, Func<IController?> bt)
    {
        _cfg = cfg;
        _bt = bt;
    }

    public byte[]? LastCaptured(int side) => _captured[side];

    public TriggerEffect? Effective(int side) => side == 0 ? _cfg.Now.TriggerL : _cfg.Now.TriggerR;

    public void Apply()
    {
        var bt = _bt();
        if (bt == null) return;
        var s = new StateData();
        for (int side = 0; side < 2; side++)
        {
            var effect = Effective(side);
            if (effect != null) s.SetTrigger(side, effect.Encode());
            else if (_customApplied[side]) s.SetTrigger(side, new TriggerEffect { Type = TriggerEffectType.Off }.Encode());
            _customApplied[side] = effect != null;
        }
        if (s.AllowLeftTriggerFFB || s.AllowRightTriggerFFB) bt.SendState(s.ToReport31());
    }

    public void Preview(int side, TriggerEffect effect)
    {
        var s = new StateData();
        s.SetTrigger(side, effect.Encode());
        _bt()?.SendState(s.ToReport31());
    }

    public void FilterGameReport(StateData state)
    {
        for (int side = 0; side < 2; side++)
        {
            if (!state.AllowTrigger(side)) continue;
            var bytes = state.GetTrigger(side);
            if (bytes[0] is not (0x00 or 0x05) && (_captured[side] == null || !_captured[side].AsSpan().SequenceEqual(bytes)))
            {
                _captured[side] = bytes;
                Captured?.Invoke(side, bytes);
            }
            if (Effective(side) != null) state.ClearTrigger(side);
        }
    }

    public void OnRumble(byte heavy, byte light)
    {
        if (_cfg.TriggerKick == 0) return;
        int level = Math.Max(heavy, light) * 8 / 256;
        if (level == _kickLevel) return;
        long now = Environment.TickCount64;
        if (level != 0 && now - _lastKick < 30) return;
        _lastKick = now;
        _kickLevel = level;
        var s = new StateData();
        for (int side = 0; side < 2; side++)
        {
            if (_cfg.TriggerKick == 2 && side == 0) continue;
            if (Effective(side) is { } custom && level == 0)
            {
                s.SetTrigger(side, custom.Encode());
                continue;
            }
            var effect = level == 0
                ? new TriggerEffect { Type = TriggerEffectType.Off }
                : new TriggerEffect { Type = TriggerEffectType.Vibration, Start = 0, Strength = level + 1, Frequency = light > heavy ? 60 : 30 };
            s.SetTrigger(side, effect.Encode());
        }
        _bt()?.SendState(s.ToReport31());
    }

    public void Reset()
    {
        _customApplied = new bool[2];
        _kickLevel = 0;
    }
}
