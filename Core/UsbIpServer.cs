using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZerkSense5;

sealed class UsbIpServer : IDisposable
{
    public const string BusId = "1-1";
    const ushort Version = 0x0111;
    const uint BusNum = 1, DevNum = 1;

    readonly TcpListener _listener;
    readonly Thread _thread;
    volatile IUsbDevice? _device;
    UsbIpSession? _session;
    readonly object _lock = new();

    public UsbIpServer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "USB/IP accept" };
    }

    public void Start()
    {
        _listener.Start();
        _thread.Start();
    }

    public IUsbDevice? Device
    {
        get => _device;
        set
        {
            lock (_lock)
            {
                _device = value;
                if (value == null)
                {
                    _session?.Dispose();
                    _session = null;
                }
            }
        }
    }

    public event Action<bool>? SessionChanged;

    void AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            client.NoDelay = true;
            new Thread(() => HandleClient(client)) { IsBackground = true, Name = "USB/IP client" }.Start();
        }
    }

    void HandleClient(TcpClient client)
    {
        var stream = client.GetStream();
        try
        {
            var hdr = new byte[8];
            stream.ReadExactly(hdr);
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(2));
            var dev = _device;

            if (code == 0x8005)
            {
                var reply = new List<byte>();
                reply.AddRange(OpHeader(0x0005, 0));
                reply.AddRange(BeU32(dev == null ? 0u : 1u));
                if (dev != null)
                {
                    reply.AddRange(DeviceRecord(dev));
                    foreach (var itf in dev.Interfaces)
                        reply.AddRange(new byte[] { itf.cls, itf.sub, itf.proto, 0 });
                }
                stream.Write(reply.ToArray());
                client.Close();
                return;
            }

            if (code == 0x8003)
            {
                var busId = new byte[32];
                stream.ReadExactly(busId);
                string requested = System.Text.Encoding.ASCII.GetString(busId).TrimEnd('\0');
                lock (_lock)
                {
                    dev = _device;
                    if (dev == null || requested != BusId || _session != null)
                    {
                        stream.Write(OpHeader(0x0003, 1));
                        client.Close();
                        return;
                    }
                    var reply = new List<byte>();
                    reply.AddRange(OpHeader(0x0003, 0));
                    reply.AddRange(DeviceRecord(dev));
                    stream.Write(reply.ToArray());
                    _session = new UsbIpSession(client, dev);
                }
                Log.Info("USB/IP: virtual controller attached");
                SessionChanged?.Invoke(true);
                _session.Run();
                lock (_lock)
                {
                    _session?.Dispose();
                    _session = null;
                }
                Log.Info("USB/IP: virtual controller detached");
                SessionChanged?.Invoke(false);
                return;
            }
            client.Close();
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
            client.Close();
        }
    }

    static byte[] OpHeader(ushort code, uint status)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(b, Version);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), code);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), status);
        return b;
    }

    static byte[] BeU32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    static byte[] DeviceRecord(IUsbDevice dev)
    {
        var b = new byte[312];
        System.Text.Encoding.ASCII.GetBytes("/sys/devices/pci0000:00/0000:00:1d.0/usb1/1-1").CopyTo(b, 0);
        System.Text.Encoding.ASCII.GetBytes(BusId).CopyTo(b, 256);
        var s = b.AsSpan(288);
        BinaryPrimitives.WriteUInt32BigEndian(s, BusNum);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], DevNum);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 2);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], dev.VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], dev.ProductId);
        BinaryPrimitives.WriteUInt16BigEndian(s[16..], dev.BcdDevice);
        s[18] = 0; s[19] = 0; s[20] = 0;
        s[21] = 1;
        s[22] = 1;
        s[23] = (byte)dev.Interfaces.Count;
        return b;
    }

    public void Dispose()
    {
        Device = null;
        _listener.Stop();
    }
}

interface IUsbDevice
{
    ushort VendorId { get; }
    ushort ProductId { get; }
    ushort BcdDevice { get; }
    IReadOnlyList<(byte cls, byte sub, byte proto)> Interfaces { get; }

    int Control(ReadOnlySpan<byte> setup, ReadOnlySpan<byte> outData, out byte[] inData);
    void InterruptOut(int ep, ReadOnlySpan<byte> data);
    void IsoOut(int ep, ReadOnlySpan<byte> packet);
    void IsoIn(int ep, Span<byte> packet);
    bool IsIsochronous(int ep);
    int IsoInPacketSize(int ep);
    void Attach(UsbIpSession session);
    void Detach(UsbIpSession session);
    void Release();
}

sealed class UsbIpSession : IDisposable
{
    const int EPIPE = 32, ECONNRESET = 104;
    const uint CmdSubmit = 1, CmdUnlink = 2, RetSubmit = 3, RetUnlink = 4;
    const uint DirOut = 0, DirIn = 1;

    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly IUsbDevice _device;
    readonly object _sendLock = new();
    readonly Dictionary<uint, Urb> _pending = new();
    readonly Dictionary<uint, Queue<Urb>> _interruptIn = new();
    readonly Dictionary<uint, byte[]> _latestInput = new();
    readonly object _interruptLock = new();
    readonly IsoPacer _pacer;
    readonly Thread _controlThread;
    readonly System.Collections.Concurrent.BlockingCollection<Urb> _controlQueue = new();
    volatile bool _closed;

    sealed class Urb
    {
        public uint Seq, Ep, Dir, Flags, Length, StartFrame;
        public int NumPackets;
        public byte[] Setup = [];
        public byte[] Data = [];
        public (uint offset, uint length)[] Iso = [];
        int _done;
        public bool TryClaim() => Interlocked.Exchange(ref _done, 1) == 0;
    }

    public UsbIpSession(TcpClient client, IUsbDevice device)
    {
        _client = client;
        _stream = client.GetStream();
        _device = device;
        _pacer = new IsoPacer();
        _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "USB/IP control" };
    }

    public void Run()
    {
        _device.Attach(this);
        _pacer.Start();
        _controlThread.Start();
        try
        {
            var hdr = new byte[48];
            while (!_closed)
            {
                _stream.ReadExactly(hdr);
                uint command = BeU32(hdr, 0);
                if (command == CmdSubmit) ReadSubmit(hdr);
                else if (command == CmdUnlink) HandleUnlink(BeU32(hdr, 4), BeU32(hdr, 20));
                else break;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
        }
        finally
        {
            _device.Detach(this);
            Dispose();
        }
    }

    void ReadSubmit(byte[] hdr)
    {
        var urb = new Urb
        {
            Seq = BeU32(hdr, 4),
            Dir = BeU32(hdr, 12),
            Ep = BeU32(hdr, 16),
            Flags = BeU32(hdr, 20),
            Length = BeU32(hdr, 24),
            StartFrame = BeU32(hdr, 28),
            Setup = hdr[40..48],
        };
        uint packets = BeU32(hdr, 32);
        bool iso = _device.IsIsochronous((int)urb.Ep) && packets != 0xFFFFFFFF && packets > 0;
        urb.NumPackets = iso ? (int)packets : -1;

        if (urb.Dir == DirOut && urb.Length > 0)
        {
            urb.Data = new byte[urb.Length];
            _stream.ReadExactly(urb.Data);
        }
        if (iso)
        {
            var desc = new byte[16 * packets];
            _stream.ReadExactly(desc);
            urb.Iso = new (uint, uint)[packets];
            for (int i = 0; i < packets; i++)
                urb.Iso[i] = (BeU32(desc, i * 16), BeU32(desc, i * 16 + 4));
        }

        lock (_pending) _pending[urb.Seq] = urb;

        if (urb.Ep == 0)
        {
            _controlQueue.Add(urb);
        }
        else if (iso)
        {
            _pacer.Schedule((int)urb.Ep + (urb.Dir == DirIn ? 16 : 0), urb.NumPackets, () => CompleteIso(urb));
        }
        else if (urb.Dir == DirOut)
        {
            _device.InterruptOut((int)urb.Ep, urb.Data);
            if (urb.TryClaim()) SendSubmit(urb, 0, urb.Length, null);
        }
        else
        {
            byte[]? ready;
            lock (_interruptLock)
            {
                if (_latestInput.Remove(urb.Ep, out ready)) { }
                else
                {
                    if (!_interruptIn.TryGetValue(urb.Ep, out var q)) _interruptIn[urb.Ep] = q = new Queue<Urb>();
                    q.Enqueue(urb);
                }
            }
            if (ready != null && urb.TryClaim()) SendInterruptIn(urb, ready);
        }
    }

    void ControlLoop()
    {
        try
        {
            foreach (var urb in _controlQueue.GetConsumingEnumerable())
            {
                int status;
                byte[] inData = [];
                try
                {
                    status = _device.Control(urb.Setup, urb.Data, out inData);
                }
                catch (Exception e)
                {
                    Log.Warn($"Control request failed: {e.Message}");
                    status = -EPIPE;
                }
                if (!urb.TryClaim()) continue;
                if (urb.Dir == DirIn)
                {
                    if (inData.Length > urb.Length) inData = inData[..(int)urb.Length];
                    SendSubmit(urb, status, status == 0 ? (uint)inData.Length : 0, status == 0 ? inData : null);
                }
                else
                {
                    SendSubmit(urb, status, status == 0 ? urb.Length : 0, null);
                }
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or IOException)
        {
        }
    }

    void CompleteIso(Urb urb)
    {
        if (!urb.TryClaim()) return;
        int ep = (int)urb.Ep;
        var actual = new uint[urb.NumPackets];
        if (urb.Dir == DirOut)
        {
            uint total = 0;
            for (int i = 0; i < urb.NumPackets; i++)
            {
                var (off, len) = urb.Iso[i];
                if (off + len <= urb.Data.Length) _device.IsoOut(ep, urb.Data.AsSpan((int)off, (int)len));
                actual[i] = len;
                total += len;
            }
            SendSubmit(urb, 0, total, null, actual);
        }
        else
        {
            int want = _device.IsoInPacketSize(ep);
            var packed = new byte[urb.NumPackets * want];
            int pos = 0;
            for (int i = 0; i < urb.NumPackets; i++)
            {
                int n = (int)Math.Min((uint)want, urb.Iso[i].length);
                _device.IsoIn(ep, packed.AsSpan(pos, n));
                actual[i] = (uint)n;
                pos += n;
            }
            SendSubmit(urb, 0, (uint)pos, packed[..pos], actual);
        }
    }

    public void PushInterruptIn(int ep, byte[] report)
    {
        Urb? urb = null;
        lock (_interruptLock)
        {
            if (_interruptIn.TryGetValue((uint)ep, out var q))
                while (q.Count > 0)
                {
                    var u = q.Dequeue();
                    if (u.TryClaim())
                    {
                        urb = u;
                        break;
                    }
                }
            if (urb == null) _latestInput[(uint)ep] = report;
        }
        if (urb != null) SendInterruptIn(urb, report);
    }

    void SendInterruptIn(Urb urb, byte[] report)
    {
        var data = report.Length > urb.Length ? report[..(int)urb.Length] : report;
        SendSubmit(urb, 0, (uint)data.Length, data);
    }

    void HandleUnlink(uint seq, uint victim)
    {
        Urb? urb;
        lock (_pending) _pending.TryGetValue(victim, out urb);
        int status = urb != null && urb.TryClaim() ? -ECONNRESET : 0;
        if (urb != null) lock (_pending) _pending.Remove(victim);

        var b = new byte[48];
        WriteBe(b, 0, RetUnlink);
        WriteBe(b, 4, seq);
        WriteBe(b, 20, (uint)status);
        Send(b);
    }

    void SendSubmit(Urb urb, int status, uint actualLength, byte[]? inData, uint[]? isoActual = null)
    {
        lock (_pending) _pending.Remove(urb.Seq);
        bool iso = urb.NumPackets >= 0;
        int dataLen = inData?.Length ?? 0;
        var b = new byte[48 + dataLen + (iso ? 16 * urb.NumPackets : 0)];
        WriteBe(b, 0, RetSubmit);
        WriteBe(b, 4, urb.Seq);
        WriteBe(b, 20, (uint)status);
        WriteBe(b, 24, actualLength);
        WriteBe(b, 28, iso ? _pacer.FrameNumber : 0);
        WriteBe(b, 32, iso ? (uint)urb.NumPackets : 0xFFFFFFFF);
        WriteBe(b, 36, 0);
        inData?.CopyTo(b, 48);
        if (iso)
        {
            int p = 48 + dataLen;
            for (int i = 0; i < urb.NumPackets; i++, p += 16)
            {
                WriteBe(b, p, urb.Iso[i].offset);
                WriteBe(b, p + 4, urb.Iso[i].length);
                WriteBe(b, p + 8, isoActual![i]);
                WriteBe(b, p + 12, 0);
            }
        }
        Send(b);
    }

    void Send(byte[] b)
    {
        if (_closed) return;
        try
        {
            lock (_sendLock) _stream.Write(b);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Dispose();
        }
    }

    static uint BeU32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    static void WriteBe(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _pacer.Dispose();
        _controlQueue.CompleteAdding();
        _client.Close();
    }
}

sealed class IsoPacer : IDisposable
{
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    readonly PriorityQueue<Action, long> _queue = new();
    readonly Dictionary<int, long> _streamClock = new();
    readonly object _lock = new();
    readonly AutoResetEvent _wake = new(false);
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly Thread _thread;
    volatile bool _stop;
    static readonly long TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000;

    public IsoPacer()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Iso pacer", Priority = ThreadPriority.Highest };
    }

    public uint FrameNumber => (uint)(_clock.ElapsedTicks / TicksPerMs) & 0x7FF;

    public void Start()
    {
        timeBeginPeriod(1);
        _thread.Start();
    }

    public void Schedule(int stream, int packets, Action complete)
    {
        long now = _clock.ElapsedTicks;
        lock (_lock)
        {
            long next = _streamClock.GetValueOrDefault(stream, 0);
            if (next < now - 20 * TicksPerMs) next = now;
            long due = next + packets * TicksPerMs;
            _streamClock[stream] = due;
            _queue.Enqueue(complete, due);
        }
        _wake.Set();
    }

    void Loop()
    {
        while (!_stop)
        {
            Action? action = null;
            long wait;
            lock (_lock)
            {
                if (_queue.TryPeek(out var a, out long due))
                {
                    wait = due - _clock.ElapsedTicks;
                    if (wait <= 0)
                    {
                        _queue.Dequeue();
                        action = a;
                    }
                }
                else
                {
                    wait = long.MaxValue;
                }
            }
            if (action != null)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Warn($"Iso completion failed: {e.Message}");
                }
                continue;
            }
            if (wait == long.MaxValue) _wake.WaitOne(100);
            else if (wait > TicksPerMs) _wake.WaitOne((int)(wait / TicksPerMs) - 1 > 0 ? (int)(wait / TicksPerMs) - 1 : 0);
            else Thread.SpinWait(50);
        }
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _wake.Set();
        timeEndPeriod(1);
    }
}
