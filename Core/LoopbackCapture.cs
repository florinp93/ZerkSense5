using System.Runtime.InteropServices;

namespace ZerkSense5;

sealed class LoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;

    public delegate void DataHandler(ReadOnlySpan<float> frames);

    readonly int? _pid;
    readonly DataHandler _onData;
    readonly Thread _thread;
    readonly ManualResetEvent _stop = new(false);

    public bool IsRunning { get; private set; }
    public string Description => _pid is int p ? $"process {p}" : "all system audio";

    LoopbackCapture(int? pid, DataHandler onData)
    {
        _pid = pid;
        _onData = onData;
        _thread = new Thread(Run) { IsBackground = true, Name = "Loopback capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public static LoopbackCapture Start(int? pid, DataHandler onData)
    {
        var c = new LoopbackCapture(pid, onData);
        c._thread.Start();
        return c;
    }

    void Run()
    {
        IAudioClient? client = null;
        try
        {
            client = _pid is int pid ? ActivateProcessLoopback(pid) : ActivateSystemLoopback();
            var format = new WAVEFORMATEX
            {
                wFormatTag = 3,
                nChannels = 2,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = 8,
                nAvgBytesPerSec = SampleRate * 8,
            };
            const uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                               AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Check(client.Initialize(0, flags, 200_000, 0, ref format, IntPtr.Zero), "Initialize");
            using var ready = new AutoResetEvent(false);
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var svc), "GetService");
            var capture = (IAudioCaptureClient)svc;
            Check(client.Start(), "Start");
            IsRunning = true;
            Log.Info($"Audio haptics: capturing {Description}");

            var handles = new WaitHandle[] { _stop, ready };
            while (WaitHandle.WaitAny(handles, 200) != 0)
            {
                while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    if (capture.GetBuffer(out IntPtr data, out uint frames, out uint bufFlags, out _, out _) != 0) break;
                    unsafe
                    {
                        if ((bufFlags & 0x2) == 0 && frames > 0)
                            _onData(new ReadOnlySpan<float>((void*)data, (int)frames * 2));
                    }
                    capture.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or TimeoutException)
        {
            Log.Warn($"Audio haptics: capture of {Description} failed ({e.Message})");
        }
        finally
        {
            IsRunning = false;
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed", hr);
    }

    static IAudioClient ActivateProcessLoopback(int pid)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = 1,
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = 0,
        };
        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr blob = Marshal.AllocHGlobal(size);
        IntPtr pv = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(p, blob, false);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65);
            Marshal.WriteInt32(pv, 8, size);
            Marshal.WriteIntPtr(pv, 16, blob);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out var op);
            Check(hr, "ActivateAudioInterfaceAsync");
            if (!handler.Done.WaitOne(5000)) throw new TimeoutException("audio activation timed out");
            op.GetActivateResult(out int result, out object iface);
            Check(result, "process loopback activation");
            return (IAudioClient)iface;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(blob);
        }
    }

    static IAudioClient ActivateSystemLoopback()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(0, 0, out var device), "GetDefaultAudioEndpoint");
            var iid = typeof(IAudioClient).GUID;
            Check(device.Activate(ref iid, 23, IntPtr.Zero, out var client), "Activate");
            Marshal.ReleaseComObject(device);
            return (IAudioClient)client;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(1000);
    }

    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation op);

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }
}
