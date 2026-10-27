using System.Diagnostics;

namespace ZerkSense5;

interface IHapticsSource
{
    bool Active { get; }

    void Render(Span<float> left, Span<float> right);
}

sealed class HapticsMixer : IDisposable
{
    public const int Rate = 3000;
    const int FrameSamples = 32;

    readonly Config _cfg;
    readonly Func<IController?> _bt;
    readonly AudioBridge _audio;
    readonly List<IHapticsSource> _sources = new();
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    long _lastHostFrame;

    public HapticsMixer(Config cfg, Func<IController?> bt, AudioBridge audio)
    {
        _cfg = cfg;
        _bt = bt;
        _audio = audio;
        audio.Mixer = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "Haptics mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Add(IHapticsSource source)
    {
        lock (_sources) _sources.Add(source);
    }

    public void Wake() => _wake.Set();

    bool HostClocked => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHostFrame)).TotalMilliseconds < 60;

    bool AnyActive()
    {
        lock (_sources) return _sources.Any(s => s.Active);
    }

    void Render(Span<float> l, Span<float> r)
    {
        l.Clear();
        r.Clear();
        lock (_sources)
            foreach (var s in _sources)
                if (s.Active) s.Render(l, r);
    }

    public void MixHostFrame(float[] interleaved)
    {
        Interlocked.Exchange(ref _lastHostFrame, Stopwatch.GetTimestamp());
        if (!AnyActive()) return;
        Span<float> l = stackalloc float[FrameSamples], r = stackalloc float[FrameSamples];
        Render(l, r);
        for (int i = 0; i < FrameSamples; i++)
        {
            interleaved[i * 2] += l[i];
            interleaved[i * 2 + 1] += r[i];
        }
    }

    void Run()
    {
        var clock = Stopwatch.StartNew();
        double next = 0;
        int silent = 0;
        while (!_stop)
        {
            bool active = !HostClocked && AnyActive() && _bt() != null;
            if (!active)
            {
                if (silent < 2 && !HostClocked && _bt() != null)
                {
                    Send(silentOnly: true);
                    silent++;
                    continue;
                }
                _wake.WaitOne(100);
                next = clock.Elapsed.TotalMilliseconds;
                continue;
            }
            silent = 0;
            int frames = Send(silentOnly: false);
            next += frames * FrameSamples * 1000.0 / Rate;
            double wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait < -50) next = clock.Elapsed.TotalMilliseconds;
            else if (wait > 1.5) Thread.Sleep((int)(wait - 1));
            while (clock.Elapsed.TotalMilliseconds < next) Thread.SpinWait(20);
        }
    }

    int Send(bool silentOnly)
    {
        int frames = _cfg.PacketMode == 1 ? 1 : 2;
        int n = frames * FrameSamples;
        var l = new float[n];
        var r = new float[n];
        if (!silentOnly) Render(l, r);
        var buf = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            buf[i * 2] = (byte)(sbyte)Math.Clamp((int)(l[i] * 127f), -128, 127);
            buf[i * 2 + 1] = (byte)(sbyte)Math.Clamp((int)(r[i] * 127f), -128, 127);
        }
        _bt()?.SendAudio(_audio.HapticsReport(buf, frames == 2));
        return frames;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
