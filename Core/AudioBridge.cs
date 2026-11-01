using System.Diagnostics;
using Concentus;
using Concentus.Enums;

namespace ZerkSense5;

sealed class AudioBridge
{
    const int SampleSize = 64;
    const int SpeakerOpusSize = 200;
    const int SpeakerFrames = 480;
    const int MicOpusSize = 71;
    const int MicFrames = 480;
    const int MaxQueuedFrames = 8;
    const double FrameMs = 32 * 1000.0 / 3000;

    readonly Config _cfg;
    readonly Func<IController?> _bt;
    byte _packetCounter;

    readonly LinearResampler _hapticsResampler = new(16, 1);
    readonly float[] _hapticNative = new float[SampleSize];
    int _hapticPos;
    readonly Queue<byte[]> _hapticsFifo = new();

    readonly LinearResampler _speakerResampler = new(16, 15);
    readonly float[] _speakerBuf = new float[SpeakerFrames * 2];
    int _speakerPos;
    readonly Queue<byte[]> _speakerFifo = new();
    readonly object _fifoLock = new();
    readonly AutoResetEvent _frameReady = new(false);
    readonly IOpusEncoder _encoder;
    readonly Action<float, float> _onSpeaker, _onHaptic;

    readonly IOpusDecoder _decoder;
    readonly Queue<short[]> _micFifo = new();
    short[]? _micFrame;
    int _micOffset;
    readonly object _micLock = new();

    public HapticsMixer? Mixer { get; set; }

    public volatile bool HeadsetPlugged;
    public volatile bool MicActive;

    public AudioBridge(Config cfg, Func<IController?> bt)
    {
        _cfg = cfg;
        _bt = bt;
        _encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;
        _encoder.Bitrate = SpeakerOpusSize * 8 * 100;
        _encoder.UseVBR = false;
        _encoder.Complexity = 0;
        _decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        _onSpeaker = OnSpeakerSample;
        _onHaptic = OnHapticSample;
        new Thread(SendLoop) { IsBackground = true, Name = "Audio sender", Priority = ThreadPriority.AboveNormal }.Start();
    }

    bool SpeakerEnabled => _cfg.SpeakerSelect != 3;
    bool MicEnabled => MicActive && _cfg.MicSelect != 3;

    public void ProcessUsbOut(ReadOnlySpan<byte> pcm)
    {
        int frames = pcm.Length / 8;
        for (int i = 0; i < frames; i++)
        {
            var f = pcm.Slice(i * 8, 8);
            float s0 = (short)(f[0] | f[1] << 8) / 32768f;
            float s1 = (short)(f[2] | f[3] << 8) / 32768f;
            float h0 = (short)(f[4] | f[5] << 8) / 32768f;
            float h1 = (short)(f[6] | f[7] << 8) / 32768f;

            if (SpeakerEnabled)
                _speakerResampler.Push(s0, s1, _onSpeaker);
            _hapticsResampler.Push(h0, h1, _onHaptic);
        }
    }

    void OnSpeakerSample(float l, float r)
    {
        _speakerBuf[_speakerPos++] = l;
        _speakerBuf[_speakerPos++] = r;
        if (_speakerPos == _speakerBuf.Length)
        {
            _speakerPos = 0;
            EncodeSpeaker();
        }
    }

    void OnHapticSample(float l, float r)
    {
        float gain = _cfg.Now.HapticsGain;
        _hapticNative[_hapticPos] = l * gain;
        _hapticNative[_hapticPos + 1] = r * gain;
        _hapticPos += 2;
        if (_hapticPos == SampleSize)
        {
            _hapticPos = 0;
            Mixer?.MixHostFrame(_hapticNative);
            var frame = new byte[SampleSize];
            for (int i = 0; i < SampleSize; i++)
                frame[i] = (byte)(sbyte)Math.Clamp((int)(_hapticNative[i] * 127f), -128, 127);
            Push(_hapticsFifo, frame);
        }
    }

    void EncodeSpeaker()
    {
        var packet = new byte[SpeakerOpusSize];
        int n = _encoder.Encode(_speakerBuf, SpeakerFrames, packet, packet.Length);
        if (n <= 0) return;
        Push(_speakerFifo, packet);
    }

    void Push(Queue<byte[]> q, byte[] item)
    {
        lock (_fifoLock)
        {
            if (q.Count >= MaxQueuedFrames) q.Dequeue();
            q.Enqueue(item);
        }
        _frameReady.Set();
    }

    int Buffered()
    {
        lock (_fifoLock)
            return SpeakerEnabled ? Math.Min(_hapticsFifo.Count, _speakerFifo.Count) : _hapticsFifo.Count;
    }

    void SendLoop()
    {
        var clock = Stopwatch.StartNew();
        double next = 0;
        bool playing = false;
        while (true)
        {
            int frames = _cfg.PacketMode == 1 ? 1 : 2;
            int target = frames * 2;
            if (!playing)
            {
                if (Buffered() < target)
                {
                    _frameReady.WaitOne(50);
                    continue;
                }
                playing = true;
                next = clock.Elapsed.TotalMilliseconds;
            }
            if (!SendFrames(frames))
            {
                playing = false;
                continue;
            }
            int left = Buffered();
            double period = frames * FrameMs * (left > target ? 0.97 : left < frames ? 1.03 : 1.0);
            next += period;
            double wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait < -period * 2) next = clock.Elapsed.TotalMilliseconds;
            else if (wait > 1.5) Thread.Sleep((int)(wait - 1));
            while (clock.Elapsed.TotalMilliseconds < next) Thread.SpinWait(20);
        }
    }

    bool SendFrames(int frames)
    {
        byte[] report;
        lock (_fifoLock)
        {
            if (_hapticsFifo.Count < frames) return false;
            if (SpeakerEnabled && _speakerFifo.Count < frames) return false;

            var haptics = new byte[frames * SampleSize];
            for (int i = 0; i < frames; i++) _hapticsFifo.Dequeue().CopyTo(haptics, i * SampleSize);

            if (!SpeakerEnabled)
            {
                _speakerFifo.Clear();
                report = HapticsOnly(haptics, frames == 2);
            }
            else
            {
                var speaker = new byte[frames * SpeakerOpusSize];
                for (int i = 0; i < frames; i++) _speakerFifo.Dequeue().CopyTo(speaker, i * SpeakerOpusSize);
                report = frames == 1 ? Single(haptics, speaker) : Double(haptics, speaker);
            }
        }
        _bt()?.SendAudio(report);
        return true;
    }

    byte SpeakerBlockId =>
        (byte)(_cfg.SpeakerSelect == 2 || (_cfg.SpeakerSelect == 0 && HeadsetPlugged) ? 0x16 : 0x13);

    byte[] Single(byte[] haptics, byte[] speaker)
    {
        var pkt = new byte[398];
        pkt[0] = 0x36;
        pkt[2] = 0x11 | 1 << 7;
        pkt[3] = 7;
        pkt[4] = (byte)(MicEnabled ? 0b11111111 : 0b11111110);
        byte bl = (byte)_cfg.AudioBufferLength;
        pkt[5] = pkt[6] = pkt[7] = pkt[8] = pkt[9] = bl;
        pkt[10] = _packetCounter++;
        pkt[11] = 0x12 | 1 << 7;
        pkt[12] = SampleSize;
        haptics.CopyTo(pkt, 13);
        pkt[77] = (byte)(SpeakerBlockId | 1 << 7);
        pkt[78] = SpeakerOpusSize;
        speaker.CopyTo(pkt, 79);
        return pkt;
    }

    byte[] Double(byte[] haptics, byte[] speaker)
    {
        var pkt = new byte[547];
        pkt[0] = 0x39;
        pkt[2] = 0x11 | 1 << 7;
        pkt[3] = 6;
        pkt[4] = (byte)(MicEnabled ? 0b01111111 : 0b01111110);
        byte bl = (byte)_cfg.AudioBufferLength;
        pkt[5] = pkt[6] = pkt[7] = pkt[8] = bl;
        _packetCounter += 2;
        pkt[9] = _packetCounter;
        pkt[10] = 0x12 | 1 << 6 | 1 << 7;
        pkt[11] = SampleSize;
        haptics.CopyTo(pkt, 12);
        pkt[140] = (byte)(SpeakerBlockId | 1 << 6 | 1 << 7);
        pkt[141] = SpeakerOpusSize;
        speaker.CopyTo(pkt, 142);
        return pkt;
    }

    byte[] HapticsOnly(byte[] haptics, bool doubleFrame)
    {
        var pkt = new byte[142];
        pkt[0] = 0x32;
        pkt[2] = 0x11 | 1 << 7;
        pkt[3] = 4;
        pkt[4] = (byte)(MicEnabled ? 0b01100111 : 0b01100110);
        byte bl = (byte)_cfg.AudioBufferLength;
        pkt[5] = pkt[6] = bl;
        pkt[7] = _packetCounter++;
        if (doubleFrame) _packetCounter++;
        pkt[8] = (byte)(0x12 | (doubleFrame ? 1 : 0) << 6 | 1 << 7);
        pkt[9] = SampleSize;
        haptics.CopyTo(pkt, 10);
        return pkt;
    }

    public byte[] HapticsReport(byte[] haptics, bool doubleFrame) => HapticsOnly(haptics, doubleFrame);

    public byte[] MicStatusReport()
    {
        var pkt = new byte[142];
        pkt[0] = 0x32;
        pkt[2] = 0x11 | 1 << 7;
        pkt[3] = 1;
        pkt[4] = (byte)(MicEnabled ? 0b00000011 : 0b00000010);
        return pkt;
    }

    public void OnMicPacket(ReadOnlySpan<byte> opus)
    {
        if (!MicEnabled || opus.Length < MicOpusSize) return;
        var pcm = new short[MicFrames];
        int n;
        try
        {
            n = _decoder.Decode(opus[..MicOpusSize], pcm, MicFrames, false);
        }
        catch (Exception)
        {
            return;
        }
        if (n <= 0) return;
        lock (_micLock)
        {
            if (_micFifo.Count >= 8) _micFifo.Dequeue();
            _micFifo.Enqueue(n == MicFrames ? pcm : pcm[..n]);
        }
    }

    public void FillMicPacket(Span<byte> dst)
    {
        dst.Clear();
        if (!MicEnabled) return;
        lock (_micLock)
        {
            int frames = dst.Length / 4;
            for (int i = 0; i < frames; i++)
            {
                if (_micFrame == null || _micOffset >= _micFrame.Length)
                {
                    if (!_micFifo.TryDequeue(out _micFrame))
                    {
                        _micFrame = null;
                        return;
                    }
                    _micOffset = 0;
                }
                short s = _micFrame[_micOffset++];
                dst[i * 4] = dst[i * 4 + 2] = (byte)s;
                dst[i * 4 + 1] = dst[i * 4 + 3] = (byte)(s >> 8);
            }
        }
    }

    public void ResetMic()
    {
        lock (_micLock)
        {
            _micFifo.Clear();
            _micFrame = null;
        }
    }
}

sealed class LinearResampler(int p, int q)
{
    long _inIndex = -1;
    long _outIndex;
    float _prevL, _prevR;

    public void Push(float l, float r, Action<float, float> emit)
    {
        _inIndex++;
        if (_inIndex == 0)
        {
            _prevL = l;
            _prevR = r;
            emit(l, r);
            _outIndex = 1;
            return;
        }
        while (true)
        {
            long num = _outIndex * p;
            long whole = num / q;
            if (whole > _inIndex || (whole == _inIndex && num % q != 0)) break;
            float frac = whole == _inIndex ? 1f : (float)(num % q) / q;
            emit(_prevL + (l - _prevL) * frac, _prevR + (r - _prevR) * frac);
            _outIndex++;
        }
        _prevL = l;
        _prevR = r;

        if (_inIndex >= (long)p * 1_000_000)
        {
            _inIndex -= (long)p * 1_000_000;
            _outIndex -= (long)q * 1_000_000;
        }
    }
}
