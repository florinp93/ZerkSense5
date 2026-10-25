using System.Text;

namespace ZerkSense5;

sealed class VirtualXbox360 : IUsbDevice
{
    const int EPIPE = 32;
    public const int EpReportIn = 1;
    public const int EpCommandOut = 2;

    static readonly byte[] DeviceDescriptor =
    [
        0x12, 0x01, 0x00, 0x02,
        0xFF, 0xFF, 0xFF, 0x40,
        0x5E, 0x04, 0x8E, 0x02,
        0x14, 0x01,
        0x01, 0x02, 0x03, 0x01,
    ];

    static readonly byte[] ConfigDescriptor =
    [
        0x09, 0x02, 0x99, 0x00, 0x04, 0x01, 0x00, 0xA0, 0xFA,
        0x09, 0x04, 0x00, 0x00, 0x02, 0xFF, 0x5D, 0x01, 0x00,
        0x11, 0x21, 0x00, 0x01, 0x01, 0x25, 0x81, 0x14, 0x00, 0x00, 0x00, 0x00, 0x13, 0x02, 0x08, 0x00, 0x00,
        0x07, 0x05, 0x81, 0x03, 0x20, 0x00, 0x01,
        0x07, 0x05, 0x02, 0x03, 0x20, 0x00, 0x08,
        0x09, 0x04, 0x01, 0x00, 0x04, 0xFF, 0x5D, 0x03, 0x00,
        0x1B, 0x21, 0x00, 0x01, 0x01, 0x01, 0x83, 0x40, 0x01, 0x04, 0x20, 0x16, 0x85, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x16, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x07, 0x05, 0x83, 0x03, 0x20, 0x00, 0x02,
        0x07, 0x05, 0x04, 0x03, 0x20, 0x00, 0x04,
        0x07, 0x05, 0x85, 0x03, 0x20, 0x00, 0x40,
        0x07, 0x05, 0x06, 0x03, 0x20, 0x00, 0x10,
        0x09, 0x04, 0x02, 0x00, 0x01, 0xFF, 0x5D, 0x02, 0x00,
        0x09, 0x21, 0x00, 0x01, 0x01, 0x22, 0x86, 0x03, 0x00,
        0x07, 0x05, 0x86, 0x03, 0x20, 0x00, 0x10,
        0x09, 0x04, 0x03, 0x00, 0x00, 0xFF, 0xFD, 0x13, 0x04,
        0x06, 0x41, 0x00, 0x01, 0x01, 0x03,
    ];

    readonly IController _bt;
    UsbIpSession? _session;
    byte[] _lastReport = Report(null);

    public event Action<byte, byte>? Rumble;
    public event Action<int>? PlayerChanged;

    public VirtualXbox360(IController bt)
    {
        if (ConfigDescriptor.Length != 0x99) throw new InvalidOperationException("Xbox 360 config descriptor length");
        _bt = bt;
        bt.InputReport += OnBtInput;
    }

    public ushort VendorId => 0x045E;
    public ushort ProductId => 0x028E;
    public ushort BcdDevice => 0x0114;
    public IReadOnlyList<(byte cls, byte sub, byte proto)> Interfaces { get; } =
        [(0xFF, 0x5D, 0x01), (0xFF, 0x5D, 0x03), (0xFF, 0x5D, 0x02), (0xFF, 0xFD, 0x13)];

    public bool IsIsochronous(int ep) => false;
    public int IsoInPacketSize(int ep) => 0;
    public void IsoOut(int ep, ReadOnlySpan<byte> packet) { }
    public void IsoIn(int ep, Span<byte> packet) { }

    public void Attach(UsbIpSession session) => _session = session;

    public void Detach(UsbIpSession session)
    {
        if (_session == session) _session = null;
        Rumble?.Invoke(0, 0);
    }

    public void Release() => _bt.InputReport -= OnBtInput;

    void OnBtInput(byte[] buf, int len)
    {
        if (len < 2 + 63 || buf[0] != 0x31 || (buf[1] & 0x02) != 0) return;
        var report = Report(buf.AsSpan(2, 63));
        _lastReport = report;
        _session?.PushInterruptIn(EpReportIn, report);
    }

    public static byte[] Report(ReadOnlySpan<byte> s)
    {
        var r = new byte[20];
        r[0] = 0x00;
        r[1] = 0x14;
        if (s.IsEmpty)
        {
            return r;
        }

        int hat = s[7] & 0x0F;
        bool up = hat is 7 or 0 or 1, right = hat is 1 or 2 or 3, down = hat is 3 or 4 or 5, left = hat is 5 or 6 or 7;
        byte b7 = s[7], b8 = s[8], b9 = s[9];
        r[2] = (byte)((up ? 0x01 : 0) | (down ? 0x02 : 0) | (left ? 0x04 : 0) | (right ? 0x08 : 0)
                      | ((b8 & 0x20) != 0 ? 0x10 : 0)
                      | ((b8 & 0x10) != 0 ? 0x20 : 0)
                      | ((b8 & 0x40) != 0 ? 0x40 : 0)
                      | ((b8 & 0x80) != 0 ? 0x80 : 0));
        r[3] = (byte)(((b8 & 0x01) != 0 ? 0x01 : 0)
                      | ((b8 & 0x02) != 0 ? 0x02 : 0)
                      | ((b9 & 0x01) != 0 ? 0x04 : 0)
                      | ((b7 & 0x20) != 0 ? 0x10 : 0)
                      | ((b7 & 0x40) != 0 ? 0x20 : 0)
                      | ((b7 & 0x10) != 0 ? 0x40 : 0)
                      | ((b7 & 0x80) != 0 ? 0x80 : 0));
        r[4] = s[4];
        r[5] = s[5];
        Axis(r, 6, s[0], invert: false);
        Axis(r, 8, s[1], invert: true);
        Axis(r, 10, s[2], invert: false);
        Axis(r, 12, s[3], invert: true);
        return r;
    }

    static void Axis(byte[] r, int o, byte v, bool invert)
    {
        int x = v * 257 - 32768;
        if (invert) x = -1 - x;
        r[o] = (byte)x;
        r[o + 1] = (byte)(x >> 8);
    }

    public void InterruptOut(int ep, ReadOnlySpan<byte> data)
    {
        if (ep != EpCommandOut || data.Length < 3) return;
        if (data[0] == 0x00 && data[1] == 0x08 && data.Length >= 5)
        {
            Rumble?.Invoke(data[3], data[4]);
        }
        else if (data[0] == 0x01 && data[1] == 0x03)
        {
            int p = data[2] is >= 0x06 and <= 0x09 ? data[2] - 5 : data[2] is >= 0x02 and <= 0x05 ? data[2] - 1 : 0;
            PlayerChanged?.Invoke(p);
        }
    }

    public int Control(ReadOnlySpan<byte> setup, ReadOnlySpan<byte> outData, out byte[] inData)
    {
        inData = [];
        byte type = setup[0], request = setup[1];
        ushort value = (ushort)(setup[2] | setup[3] << 8);
        int kind = (type >> 5) & 3, recipient = type & 0x1F;
        if (kind != 0)
        {
            if (kind == 1 && request == 0x01)
            {
                inData = _lastReport;
                return 0;
            }
            return -EPIPE;
        }
        switch (request)
        {
            case 0x00:
                inData = recipient == 0 ? [0x02, 0x00] : [0x00, 0x00];
                return 0;
            case 0x01: case 0x03: case 0x05: case 0x09: case 0x0B:
                return 0;
            case 0x08:
                inData = [0x01];
                return 0;
            case 0x0A:
                inData = [0x00];
                return 0;
            case 0x06:
                switch (value >> 8)
                {
                    case 0x01: inData = DeviceDescriptor; return 0;
                    case 0x02: inData = ConfigDescriptor; return 0;
                    case 0x03:
                        inData = StringDescriptor(value & 0xFF);
                        return inData.Length > 0 ? 0 : -EPIPE;
                }
                return -EPIPE;
        }
        return -EPIPE;
    }

    static readonly string Serial = "ZS5" + Environment.MachineName.Aggregate(2166136261u, (h, c) => (h ^ c) * 16777619u).ToString("X8")[..5];

    static byte[] StringDescriptor(int index)
    {
        if (index == 0) return [0x04, 0x03, 0x09, 0x04];
        string? s = index switch
        {
            1 => "©Microsoft Corporation",
            2 => "Controller",
            3 => Serial,
            4 => "Xbox Security Method 3, Version 1.00, © 2005 Microsoft Corporation. All rights reserved.",
            _ => null,
        };
        if (s == null) return [];
        var utf16 = Encoding.Unicode.GetBytes(s);
        var d = new byte[2 + utf16.Length];
        d[0] = (byte)d.Length;
        d[1] = 0x03;
        utf16.CopyTo(d, 2);
        return d;
    }
}
