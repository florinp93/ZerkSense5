namespace ZerkSense5;

sealed class VirtualDualSense : IUsbDevice
{
    const int EPIPE = 32;
    const int MicPacketBytes = 48 * 2 * 2;

    readonly IController _bt;
    readonly AudioBridge _audio;
    readonly Config _cfg;
    readonly Action<StateData>? _onGameReport;
    readonly byte[] _device = UsbDescriptors.Device(UsbDescriptors.ProductIdDs);
    readonly byte[] _config = UsbDescriptors.Configuration(UsbDescriptors.HidReportDs.Length);
    UsbIpSession? _session;

    readonly int[] _alt = new int[4];
    readonly byte[] _mute = new byte[2];
    readonly short[] _volume = [0, 48 * 256];
    byte[] _lastInput = new byte[64];
    bool _headphones, _micMuted, _mutePressed;

    public VirtualDualSense(IController bt, AudioBridge audio, Config cfg, Action<StateData>? onGameReport = null)
    {
        _onGameReport = onGameReport;
        _bt = bt;
        _audio = audio;
        _cfg = cfg;
        _lastInput[0] = 0x01;
        _lastInput[1] = 0x7F; _lastInput[2] = 0x7D; _lastInput[3] = 0x7F; _lastInput[4] = 0x7E;
        _lastInput[8] = 0x08;
        bt.InputReport += OnBtInput;
    }

    public ushort VendorId => UsbDescriptors.VendorId;
    public ushort ProductId => UsbDescriptors.ProductIdDs;
    public ushort BcdDevice => 0x0100;

    public IReadOnlyList<(byte cls, byte sub, byte proto)> Interfaces { get; } =
        [(1, 1, 0), (1, 2, 0), (1, 2, 0), (3, 0, 0)];

    public bool SpeakerStreamOpen => _alt[UsbDescriptors.ItfSpeaker] != 0;
    public bool MicStreamOpen => _alt[UsbDescriptors.ItfMic] != 0;
    public event Action? StreamsChanged;

    public bool IsIsochronous(int ep) => ep is UsbDescriptors.EpSpeakerOut or UsbDescriptors.EpMicIn;
    public int IsoInPacketSize(int ep) => MicPacketBytes;

    public void Attach(UsbIpSession session) => _session = session;

    public void Release() => _bt.InputReport -= OnBtInput;

    public void Detach(UsbIpSession session)
    {
        if (_session != session) return;
        _session = null;
        _alt[UsbDescriptors.ItfSpeaker] = _alt[UsbDescriptors.ItfMic] = 0;
        SetMicActive(false);
        StreamsChanged?.Invoke();
    }

    void OnBtInput(byte[] buf, int len)
    {
        if (len < 3 || buf[0] != 0x31) return;

        if ((buf[1] & 0x02) != 0)
        {
            _audio.OnMicPacket(buf.AsSpan(3, len - 3));
            return;
        }
        if (len < 2 + 63) return;

        byte status = buf[2 + 53];
        bool headphones = (status & 1) != 0;
        if (headphones != _headphones)
        {
            _headphones = headphones;
            _audio.HeadsetPlugged = headphones;
        }
        bool mutePressed = (buf[2 + 9] & 0x04) != 0;
        if (mutePressed && !_mutePressed)
        {
            _micMuted = !_micMuted;
            SendMicMute();
            Log.Info(_micMuted ? "Microphone muted" : "Microphone unmuted");
        }
        _mutePressed = mutePressed;

        var report = new byte[64];
        report[0] = 0x01;
        Array.Copy(buf, 2, report, 1, 63);
        _lastInput = report;
        _session?.PushInterruptIn(UsbDescriptors.EpHidIn, report);
    }

    void ForwardOutputReport(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return;
        var state = new StateData(body);
        _onGameReport?.Invoke(state);
        if (_cfg.Now.TriggerReduce > 0)
        {
            state.AllowMotorPowerLevel = true;
            state.TriggerMotorPowerReduction = _cfg.Now.TriggerReduce;
        }
        if (_cfg.SpeakerGain > 0)
        {
            state.AllowAudioControl2 = true;
            state.SpeakerCompPreGain = _cfg.SpeakerGain;
        }
        if (_cfg.MicSelect != 0)
        {
            state.AllowAudioControl = true;
            state.MicSelect = _cfg.MicSelect;
            state.NoiseCancelEnable = true;
        }
        if (state.AllowAudioControl) state.NoiseCancelEnable = true;
        if (_cfg.LockVolume)
        {
            state.AllowHeadphoneVolume = state.AllowMicVolume = state.AllowSpeakerVolume = false;
            state.AllowAudioMute = state.AllowMuteLight = false;
        }
        _bt.SendState(state.ToReport31());
    }

    public void InterruptOut(int ep, ReadOnlySpan<byte> data)
    {
        if (ep == UsbDescriptors.EpHidOut && data.Length > 0 && data[0] == 0x02)
            ForwardOutputReport(data[1..]);
    }

    public void IsoOut(int ep, ReadOnlySpan<byte> packet)
    {
        if (_alt[UsbDescriptors.ItfSpeaker] != 0) _audio.ProcessUsbOut(packet);
    }

    public void IsoIn(int ep, Span<byte> packet)
    {
        _audio.FillMicPacket(packet);
        if (_micMuted) packet.Clear();
    }

    void SendMicMute()
    {
        var s = new StateData
        {
            AllowMuteLight = true,
            MuteLightMode = (byte)(_micMuted ? 1 : 0),
            AllowAudioMute = true,
            MicMute = _micMuted || _mute[1] != 0,
            SpeakerMute = _mute[0] != 0,
            HeadphoneMute = _mute[0] != 0,
        };
        _bt.SendState(s.ToReport32());
    }

    void SendSpeakerLevel()
    {
        if (_cfg.LockVolume) return;
        byte level = (byte)Math.Clamp(100f + _volume[0] / 256f, 0, 100);
        var s = new StateData
        {
            AllowHeadphoneVolume = true,
            AllowSpeakerVolume = true,
            VolumeHeadphones = level,
            VolumeSpeaker = level,
            AllowAudioMute = true,
            MicMute = _micMuted || _mute[1] != 0,
            SpeakerMute = _mute[0] != 0,
            HeadphoneMute = _mute[0] != 0,
        };
        _bt.SendState(s.ToReport32());
    }

    void SetMicActive(bool active)
    {
        if (_audio.MicActive == active) return;
        _audio.MicActive = active;
        if (!active) _audio.ResetMic();
        _bt.SendState(_audio.MicStatusReport());
    }

    public int Control(ReadOnlySpan<byte> setup, ReadOnlySpan<byte> outData, out byte[] inData)
    {
        inData = [];
        byte type = setup[0], request = setup[1];
        ushort value = (ushort)(setup[2] | setup[3] << 8);
        ushort index = (ushort)(setup[4] | setup[5] << 8);
        int kind = (type >> 5) & 3;
        int recipient = type & 0x1F;

        if (kind == 0) return Standard(request, value, index, recipient, out inData);
        if (kind == 1 && recipient == 1)
        {
            int itf = index & 0xFF;
            if (itf == UsbDescriptors.ItfHid) return HidClass(request, value, outData, out inData);
            if (itf == UsbDescriptors.ItfAudioControl) return AudioClass(request, value, index, outData, out inData);
        }
        if (kind == 1 && recipient == 2)
        {
            if ((type & 0x80) != 0) inData = [0x80, 0xBB, 0x00];
            return 0;
        }
        return -EPIPE;
    }

    int Standard(byte request, ushort value, ushort index, int recipient, out byte[] inData)
    {
        inData = [];
        switch (request)
        {
            case 0x00:
                inData = recipient == 0 ? [0x01, 0x00] : [0x00, 0x00];
                return 0;
            case 0x01:
            case 0x03:
            case 0x05:
            case 0x09:
                return 0;
            case 0x08:
                inData = [0x01];
                return 0;
            case 0x0A:
                inData = [(byte)_alt[Math.Min(index & 0xFF, 3)]];
                return 0;
            case 0x0B:
            {
                int itf = index & 0xFF, alt = value & 0xFF;
                if (itf > 3) return -EPIPE;
                _alt[itf] = alt;
                if (itf == UsbDescriptors.ItfSpeaker)
                {
                    Log.Info($"Speaker/haptics stream {(alt != 0 ? "opened" : "closed")}");
                    if (alt != 0) SendSpeakerLevel();
                }
                if (itf == UsbDescriptors.ItfMic)
                {
                    Log.Info($"Microphone stream {(alt != 0 ? "opened" : "closed")}");
                    SetMicActive(alt != 0);
                }
                StreamsChanged?.Invoke();
                return 0;
            }
            case 0x06:
            {
                int descType = value >> 8, descIndex = value & 0xFF;
                if (recipient == 1)
                {
                    if (descType == 0x22) { inData = UsbDescriptors.HidReportDs; return 0; }
                    if (descType == 0x21) { inData = UsbDescriptors.HidDescriptor(UsbDescriptors.HidReportDs.Length); return 0; }
                    return -EPIPE;
                }
                switch (descType)
                {
                    case 0x01: inData = _device; return 0;
                    case 0x02: inData = _config; return 0;
                    case 0x03:
                        inData = UsbDescriptors.String(descIndex, false);
                        return inData.Length > 0 ? 0 : -EPIPE;
                    default: return -EPIPE;
                }
            }
        }
        return -EPIPE;
    }

    int HidClass(byte request, ushort value, ReadOnlySpan<byte> outData, out byte[] inData)
    {
        inData = [];
        int reportType = value >> 8;
        byte reportId = (byte)value;
        switch (request)
        {
            case 0x01:
            {
                if (reportType == 1)
                {
                    inData = _lastInput;
                    return 0;
                }
                if (reportType != 3) return -EPIPE;
                bool live = reportId == 0x81 || reportId is >= 0x70 and <= 0x7B || reportId is >= 0x63 and <= 0x65;
                byte[]? body = live ? _bt.GetFeatureLive(reportId) : _bt.GetFeatureCached(reportId);
                if (body == null) return -EPIPE;
                inData = new byte[1 + body.Length];
                inData[0] = reportId;
                body.CopyTo(inData, 1);
                return 0;
            }
            case 0x09:
            {
                if (outData.Length == 0) return 0;
                var body = reportId != 0 ? outData[1..] : outData;
                if (reportType == 2 && reportId == 0x02) ForwardOutputReport(body);
                else if (reportType == 3 && (reportId == 0x80 || reportId is >= 0x60 and <= 0x62))
                    return _bt.SetFeature(reportId, body) ? 0 : -EPIPE;
                return 0;
            }
            case 0x0A:
            case 0x0B:
                return 0;
            case 0x02:
                inData = [0];
                return 0;
            case 0x03:
                inData = [1];
                return 0;
        }
        return -EPIPE;
    }

    int AudioClass(byte request, ushort value, ushort index, ReadOnlySpan<byte> outData, out byte[] inData)
    {
        inData = [];
        int control = value >> 8;
        byte entity = (byte)(index >> 8);
        if (entity != UsbDescriptors.EntitySpeakerFeature && entity != UsbDescriptors.EntityMicFeature) return -EPIPE;
        int i = entity == UsbDescriptors.EntitySpeakerFeature ? 0 : 1;
        bool spk = i == 0;

        if (request == 0x01)
        {
            if (control == 1 && outData.Length >= 1)
            {
                _mute[i] = outData[0];
                if (!_cfg.LockVolume)
                {
                    var s = new StateData
                    {
                        AllowAudioMute = true,
                        MicMute = _micMuted || _mute[1] != 0,
                        SpeakerMute = _mute[0] != 0,
                        HeadphoneMute = _mute[0] != 0,
                    };
                    _bt.SendState(s.ToReport32());
                }
                return 0;
            }
            if (control == 2 && outData.Length >= 2)
            {
                _volume[i] = (short)(outData[0] | outData[1] << 8);
                float db = _volume[i] / 256f;
                if (!_cfg.LockVolume)
                {
                    var s = new StateData();
                    if (spk)
                    {
                        s.AllowHeadphoneVolume = s.AllowSpeakerVolume = true;
                        s.VolumeHeadphones = s.VolumeSpeaker = (byte)Math.Clamp(100f + db, 0, 100);
                    }
                    else
                    {
                        s.AllowMicVolume = true;
                        s.VolumeMic = (byte)Math.Clamp(db, 0, 64);
                    }
                    _bt.SendState(s.ToReport32());
                }
                return 0;
            }
            return -EPIPE;
        }

        if (control == 1)
        {
            inData = [_mute[i]];
            return 0;
        }
        if (control != 2) return -EPIPE;
        short v = request switch
        {
            0x81 => _volume[i],
            0x82 => spk ? unchecked((short)0x9C00) : (short)0,
            0x83 => spk ? (short)0 : (short)0x3000,
            0x84 => spk ? (short)0x0100 : (short)0x007A,
            _ => short.MinValue,
        };
        if (v == short.MinValue) return -EPIPE;
        inData = [(byte)v, (byte)(v >> 8)];
        return 0;
    }
}
