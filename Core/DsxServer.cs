using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ZerkSense5;

sealed class DsxServer : IDisposable
{
    const string PortFile = @"C:\Temp\DualSenseX\DualSenseX_PortNumber.txt";

    enum Instruction { Invalid, TriggerUpdate, RgbUpdate, PlayerLed, TriggerThreshold, MicLed, PlayerLedNewRevision, ResetToUserSettings }

    enum Side { Invalid, Left, Right }

    enum Mode
    {
        Normal, GameCube, VerySoft, Soft, Hard, VeryHard, Hardest, Rigid, VibrateTrigger, Choppy, Medium,
        VibrateTriggerPulse, CustomTriggerValue, Resistance, Bow, Galloping, SemiAutomaticGun, AutomaticGun, Machine,
    }

    static readonly byte[] CustomModes =
    [
        0x00, 0x01, 0x21, 0x05, 0x25, 0x02, 0x22, 0x06, 0x26,
        0x06, 0x26, 0x06, 0x26, 0x06, 0x26, 0x06, 0x26,
    ];

    readonly Config _cfg;
    readonly TriggerManager _triggers;
    readonly Func<IController?> _bt;
    readonly Func<ControllerStatus?> _status;
    readonly Action _gameLightbar;
    readonly bool _writePortFile;
    readonly object _lock = new();
    UdpClient? _udp;
    Thread? _thread;
    long _lastPacket;

    public DsxServer(Config cfg, TriggerManager triggers, Func<IController?> bt, Func<ControllerStatus?> status, Action gameLightbar,
        bool writePortFile = true)
    {
        _writePortFile = writePortFile;
        _cfg = cfg;
        _triggers = triggers;
        _bt = bt;
        _status = status;
        _gameLightbar = gameLightbar;
    }

    public bool Listening => _udp != null;
    public bool PortBusy { get; private set; }
    public bool ModActive => Interlocked.Read(ref _lastPacket) is var t && t != 0 && Environment.TickCount64 - t < 10000;

    public void Apply()
    {
        lock (_lock)
        {
            if (_cfg.DsxMods == (_udp != null)) return;
            if (!_cfg.DsxMods)
            {
                Stop();
                return;
            }
            UdpClient udp;
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _cfg.DsxPort));
            }
            catch (SocketException e)
            {
                PortBusy = true;
                Log.Warn($"DSX mods: port {_cfg.DsxPort} is in use ({e.Message}). Close DSX to let Zerk Sense 5 handle DSX mods.");
                return;
            }
            if (_writePortFile) try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PortFile)!);
                File.WriteAllText(PortFile, _cfg.DsxPort.ToString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"DSX mods: could not write {PortFile} ({e.Message}). Mods may not find the port.");
            }
            _udp = udp;
            PortBusy = false;
            _thread = new Thread(() => Run(udp)) { IsBackground = true, Name = "DSX" };
            _thread.Start();
            Log.Info($"DSX mods: listening on 127.0.0.1:{_cfg.DsxPort}");
        }
    }

    void Stop()
    {
        _udp?.Dispose();
        _udp = null;
        _thread = null;
        Reset();
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _lastPacket, 0);
        _triggers.ClearMods();
    }

    void Run(UdpClient udp)
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            byte[] data;
            try
            {
                data = udp.Receive(ref from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return;
            }
            if (Interlocked.Exchange(ref _lastPacket, Environment.TickCount64) == 0)
                Log.Info("DSX mod connected");
            try
            {
                Handle(data);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or IndexOutOfRangeException)
            {
                Log.Warn($"DSX mods: ignored a malformed packet ({e.Message})");
            }
            try
            {
                var reply = Reply();
                udp.Send(reply, reply.Length, from);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    byte[] Reply()
    {
        var status = _status();
        return Encoding.ASCII.GetBytes(JsonSerializer.Serialize(new
        {
            Status = "DSX Received UDP Instructions",
            TimeReceived = DateTime.Now.ToString(),
            isControllerConnected = status != null,
            BatteryLevel = status?.BatteryPercent ?? 0,
        }));
    }

    void Handle(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("instructions", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var ins in list.EnumerateArray())
        {
            if (ins.ValueKind != JsonValueKind.Object || !ins.TryGetProperty("type", out var type)) continue;
            var p = ins.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().ToArray() : [];
            switch (Int<Instruction>(type))
            {
                case Instruction.TriggerUpdate when p.Length >= 3:
                {
                    int side = Int<Side>(p[1]) == Side.Left ? 0 : 1;
                    _triggers.SetMod(side, Effect(Int<Mode>(p[2]), p.Skip(3).Select(v => Int(v)).ToArray()));
                    break;
                }
                case Instruction.RgbUpdate when p.Length >= 4:
                    _gameLightbar();
                    _bt()?.SendState(new StateData
                    {
                        AllowLedColor = true,
                        LedRed = Byte(p[1]),
                        LedGreen = Byte(p[2]),
                        LedBlue = Byte(p[3]),
                    }.ToReport32());
                    break;
                case Instruction.PlayerLed when p.Length >= 6:
                {
                    int lights = 0;
                    for (int i = 0; i < 5; i++)
                        if (Int(p[1 + i]) != 0) lights |= 1 << i;
                    SendPlayerLights((byte)lights);
                    break;
                }
                case Instruction.PlayerLedNewRevision when p.Length >= 2:
                    SendPlayerLights(Int(p[1]) switch { 0 => 0x04, 1 => 0x0A, 2 => 0x15, 3 => 0x1B, 4 => 0x1F, _ => 0 });
                    break;
                case Instruction.MicLed when p.Length >= 2:
                    _bt()?.SendState(new StateData
                    {
                        AllowMuteLight = true,
                        MuteLightMode = Int(p[1]) switch { 0 => 1, 1 => 2, _ => 0 },
                    }.ToReport32());
                    break;
                case Instruction.ResetToUserSettings:
                    _triggers.ClearMods();
                    break;
            }
        }
    }

    void SendPlayerLights(byte pattern) =>
        _bt()?.SendState(new StateData { AllowPlayerIndicators = true, PlayerLights = pattern }.ToReport32());

    static TriggerEffect Effect(Mode mode, int[] a)
    {
        int At(int i, int fallback = 0) => i < a.Length ? a[i] : fallback;
        TriggerEffect Res(int start, int strength) => strength <= 0
            ? new TriggerEffect { Type = TriggerEffectType.Off }
            : new TriggerEffect { Type = TriggerEffectType.Resistance, Start = start, Strength = strength };

        return mode switch
        {
            Mode.Normal => new TriggerEffect { Type = TriggerEffectType.Off },
            Mode.VerySoft => Res(0, 1),
            Mode.Soft => Res(0, 3),
            Mode.Medium => Res(0, 4),
            Mode.Hard => Res(0, 6),
            Mode.VeryHard => Res(0, 7),
            Mode.Hardest or Mode.Rigid => Res(0, 8),
            Mode.GameCube => Res(8, 8),
            Mode.Choppy => Raw(mode, TriggerEffect.Zones(0x21, [8, 0, 8, 0, 8, 0, 8, 0, 8, 0])),
            Mode.VibrateTrigger => new TriggerEffect { Type = TriggerEffectType.Vibration, Start = 0, Strength = 8, Frequency = Math.Max(At(0, 10), 1) },
            Mode.VibrateTriggerPulse => new TriggerEffect { Type = TriggerEffectType.Vibration, Start = 0, Strength = 4, Frequency = 10 },
            Mode.Resistance => Res(At(0), At(1)),
            Mode.Bow => new TriggerEffect { Type = TriggerEffectType.Bow, Start = At(0), End = At(1), Strength = At(2), EndStrength = At(3) },
            Mode.SemiAutomaticGun => new TriggerEffect { Type = TriggerEffectType.Weapon, Start = At(0), End = At(1), Strength = At(2) },
            Mode.AutomaticGun => At(1) <= 0
                ? new TriggerEffect { Type = TriggerEffectType.Off }
                : new TriggerEffect { Type = TriggerEffectType.Vibration, Start = At(0), Strength = At(1), Frequency = Math.Max(At(2), 1) },
            Mode.Galloping => Raw(mode, Zoned(0x23, At(0), At(1), ((At(3) & 7) << 3) | (At(2) & 7), At(4))),
            Mode.Machine => Raw(mode, Zoned(0x27, At(0), At(1), ((At(3) & 7) << 3) | (At(2) & 7), At(4), At(5))),
            Mode.CustomTriggerValue => Custom(a),
            _ => new TriggerEffect { Type = TriggerEffectType.Off },
        };
    }

    static byte[] Zoned(byte mode, int start, int end, params int[] rest)
    {
        start = Math.Clamp(start, 0, 8);
        int zones = (1 << start) | (1 << Math.Clamp(end, start + 1, 9));
        var e = new byte[11];
        e[0] = mode;
        e[1] = (byte)zones;
        e[2] = (byte)(zones >> 8);
        for (int i = 0; i < rest.Length; i++) e[3 + i] = (byte)rest[i];
        return e;
    }

    static TriggerEffect Custom(int[] a)
    {
        int sub = a.Length > 0 ? a[0] : 0;
        var e = new byte[11];
        e[0] = sub is >= 0 and < 17 ? CustomModes[sub] : (byte)0;
        for (int i = 0; i < 7 && 1 + i < a.Length; i++) e[1 + i] = (byte)a[1 + i];
        return Raw("CustomTriggerValue", e);
    }

    static TriggerEffect Raw(object name, byte[] bytes) =>
        new() { Type = TriggerEffectType.Captured, Raw = bytes, Name = $"DSX {name}" };

    static byte Byte(JsonElement v) => (byte)Math.Clamp(Int(v), 0, 255);

    static T Int<T>(JsonElement v) where T : struct, Enum =>
        v.ValueKind == JsonValueKind.String && Enum.TryParse<T>(v.GetString(), true, out var named)
            ? named
            : (T)Enum.ToObject(typeof(T), Int(v));

    static int Int(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt32(out int i) ? i : (int)v.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.String when int.TryParse(v.GetString(), out int s) => s,
        _ => 0,
    };

    public void Dispose()
    {
        lock (_lock) Stop();
    }
}
