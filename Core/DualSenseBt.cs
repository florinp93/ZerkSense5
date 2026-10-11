using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace ZerkSense5;

interface IController
{
    event Action<byte[], int>? InputReport;
    byte[]? GetFeatureLive(byte id);
    byte[]? GetFeatureCached(byte id);
    bool SetFeature(byte id, ReadOnlySpan<byte> body);
    void SendState(byte[] report);
    void SendAudio(byte[] report);
}

sealed class DualSenseBt : IController, IDisposable
{
    public const int BtInputLength = 78;

    readonly HidDeviceInfo _info;
    readonly SafeFileHandle _io;
    readonly SafeFileHandle _feature;
    readonly FileStream _stream;
    readonly Thread _reader, _writer;
    readonly BlockingCollection<byte[]> _stateQueue = new();
    readonly ConcurrentQueue<byte[]> _audioQueue = new();
    readonly SemaphoreSlim _writeSignal = new(0);
    readonly ConcurrentDictionary<byte, byte[]> _featureCache = new();
    readonly object _featureLock = new();
    readonly CancellationTokenSource _cts = new();
    byte _seq;
    readonly ConcurrentDictionary<int, bool> _padRequired = new();

    public event Action<byte[], int>? InputReport;
    public event Action? Disconnected;

    public HidDeviceInfo Info => _info;
    public IReadOnlyDictionary<int, bool> PadRequired => _padRequired;
    public bool IsEdge { get; private set; }
    volatile bool _alive = true;
    public bool Alive => _alive;

    DualSenseBt(HidDeviceInfo info, SafeFileHandle io, SafeFileHandle feature)
    {
        _info = info;
        _io = io;
        _feature = feature;
        HidNative.SetInputBuffers(_io, 64);
        _stream = new FileStream(_io, FileAccess.ReadWrite, 0, isAsync: true);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "BT read", Priority = ThreadPriority.AboveNormal };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "BT write", Priority = ThreadPriority.Highest };
    }

    public static HidDeviceInfo? Find()
    {
        return HidNative.Enumerate(UsbDescriptors.VendorId).FirstOrDefault(d =>
            (d.ProductId == UsbDescriptors.ProductIdDs || d.ProductId == UsbDescriptors.ProductIdDse) &&
            d.InputLength == BtInputLength);
    }

    public static DualSenseBt? Open(HidDeviceInfo info)
    {
        var io = HidNative.Open(info.Path, overlapped: true);
        if (io.IsInvalid) return null;
        var feature = HidNative.Open(info.Path, overlapped: false);
        if (feature.IsInvalid)
        {
            io.Dispose();
            return null;
        }
        return new DualSenseBt(info, io, feature);
    }

    public void Start()
    {
        _reader.Start();
        _writer.Start();
    }

    public void InitFeatures()
    {
        _featureCache.Clear();
        foreach (byte id in new byte[] { 0x09, 0x20, 0x22, 0x05 })
            GetFeatureLive(id);

        if (_featureCache.TryGetValue(0x20, out var r20) && r20.Length > 21 && r20[21] == 0x44)
        {
            IsEdge = true;
            _featureCache[0x20] = FakeDs5.Report20;
        }
    }

    public byte[]? GetFeatureLive(byte id)
    {
        lock (_featureLock)
        {
            var buf = new byte[_info.FeatureLength];
            buf[0] = id;
            if (!HidNative.GetFeature(_feature, buf)) return null;
            var body = buf[1..];
            if (!(IsEdge && id == 0x20)) _featureCache[id] = body;
            return _featureCache[id];
        }
    }

    public byte[]? GetFeatureCached(byte id)
    {
        if (_featureCache.TryGetValue(id, out var body)) return body;
        return GetFeatureLive(id);
    }

    public bool SetFeature(byte id, ReadOnlySpan<byte> body)
    {
        if (body.Length < 4) return false;
        lock (_featureLock)
        {
            var report = new byte[1 + body.Length];
            report[0] = id;
            body.CopyTo(report.AsSpan(1));
            Crc.FillFeature(report);
            var buf = new byte[Math.Max(_info.FeatureLength, report.Length)];
            report.CopyTo(buf, 0);
            return HidNative.SetFeature(_feature, buf);
        }
    }

    public void SendState(byte[] report)
    {
        _stateQueue.Add(report);
        _writeSignal.Release();
    }

    public void SendAudio(byte[] report)
    {
        _audioQueue.Enqueue(report);
        while (_audioQueue.Count > 3 && _audioQueue.TryDequeue(out _)) { }
        _writeSignal.Release();
    }

    void ReadLoop()
    {
        var buf = new byte[Math.Max(_info.InputLength, BtInputLength)];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                InputReport?.Invoke(buf, n);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Fail();
    }

    void WriteLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                _writeSignal.Wait(_cts.Token);
                byte[]? report = _stateQueue.TryTake(out var s) ? s : _audioQueue.TryDequeue(out var a) ? a : null;
                if (report == null) continue;
                Write(report);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Fail();
    }

    void Write(byte[] report)
    {
        report[1] = (byte)((report[1] & 0x0F) | (_seq << 4));
        _seq = (byte)((_seq + 1) & 0x0F);
        Crc.FillOutput(report);

        int len = report.Length;
        if (len < _info.OutputLength && !_padRequired.GetValueOrDefault(len))
        {
            try
            {
                _stream.Write(report, 0, len);
                return;
            }
            catch (IOException)
            {
                _padRequired[len] = true;
            }
        }
        var padded = new byte[Math.Max(len, _info.OutputLength)];
        report.CopyTo(padded, 0);
        _stream.Write(padded, 0, padded.Length);
    }

    void Fail()
    {
        if (!_alive) return;
        _alive = false;
        _cts.Cancel();
        Disconnected?.Invoke();
    }

    public void Dispose()
    {
        _alive = false;
        _cts.Cancel();
        _stream.Dispose();
        _feature.Dispose();
    }
}

static class FakeDs5
{
    public static readonly byte[] Report20 =
    [
        0x4a, 0x75, 0x6c, 0x20, 0x20, 0x34, 0x20, 0x32, 0x30, 0x32, 0x35, 0x31, 0x30, 0x3a, 0x33, 0x38,
        0x3a, 0x34, 0x30, 0x03, 0x00, 0x0b, 0x00, 0x11, 0x08, 0x00, 0x00, 0x2a, 0x00, 0x10, 0x01, 0x00,
        0x28, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x30, 0x06, 0x00, 0x00, 0x01,
        0x00, 0x03, 0x00, 0x10, 0x10, 0x03, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];
}
