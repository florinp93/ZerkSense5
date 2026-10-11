namespace ZerkSense5;

sealed class StateData
{
    public const int Size = 63;
    public readonly byte[] Bytes = new byte[Size];

    public StateData() { }
    public StateData(ReadOnlySpan<byte> src) => src[..Math.Min(src.Length, Size)].CopyTo(Bytes);

    bool Bit(int b, int bit) => (Bytes[b] & (1 << bit)) != 0;
    void Bit(int b, int bit, bool v)
    {
        if (v) Bytes[b] |= (byte)(1 << bit);
        else Bytes[b] &= (byte)~(1 << bit);
    }

    public bool EnableRumbleEmulation { get => Bit(0, 0); set => Bit(0, 0, value); }
    public bool UseRumbleNotHaptics { get => Bit(0, 1); set => Bit(0, 1, value); }
    public byte RumbleRight { get => Bytes[2]; set => Bytes[2] = value; }
    public byte RumbleLeft { get => Bytes[3]; set => Bytes[3] = value; }
    public bool EnableImprovedRumbleEmulation { get => Bit(38, 2); set => Bit(38, 2, value); }

    public bool AllowPlayerIndicators { get => Bit(1, 4); set => Bit(1, 4, value); }
    public byte PlayerLights { get => Bytes[43]; set => Bytes[43] = value; }

    public bool AllowRightTriggerFFB { get => Bit(0, 2); set => Bit(0, 2, value); }
    public bool AllowLeftTriggerFFB { get => Bit(0, 3); set => Bit(0, 3, value); }

    static int TriggerOffset(int side) => side == 0 ? 21 : 10;
    public bool AllowTrigger(int side) => side == 0 ? AllowLeftTriggerFFB : AllowRightTriggerFFB;
    public byte[] GetTrigger(int side) => Bytes.AsSpan(TriggerOffset(side), 11).ToArray();

    public void SetTrigger(int side, byte[] effect)
    {
        effect.AsSpan(0, 11).CopyTo(Bytes.AsSpan(TriggerOffset(side)));
        if (side == 0) AllowLeftTriggerFFB = true;
        else AllowRightTriggerFFB = true;
    }

    public void ClearTrigger(int side)
    {
        Bytes.AsSpan(TriggerOffset(side), 11).Clear();
        if (side == 0) AllowLeftTriggerFFB = false;
        else AllowRightTriggerFFB = false;
    }

    public bool AllowHeadphoneVolume { get => Bit(0, 4); set => Bit(0, 4, value); }
    public bool AllowSpeakerVolume { get => Bit(0, 5); set => Bit(0, 5, value); }
    public bool AllowMicVolume { get => Bit(0, 6); set => Bit(0, 6, value); }
    public bool AllowAudioControl { get => Bit(0, 7); set => Bit(0, 7, value); }
    public bool AllowMuteLight { get => Bit(1, 0); set => Bit(1, 0, value); }
    public bool AllowAudioMute { get => Bit(1, 1); set => Bit(1, 1, value); }
    public bool AllowLedColor { get => Bit(1, 2); set => Bit(1, 2, value); }
    public bool AllowMotorPowerLevel { get => Bit(1, 6); set => Bit(1, 6, value); }
    public bool AllowAudioControl2 { get => Bit(1, 7); set => Bit(1, 7, value); }

    public byte VolumeHeadphones { get => Bytes[4]; set => Bytes[4] = value; }
    public byte VolumeSpeaker { get => Bytes[5]; set => Bytes[5] = value; }
    public byte VolumeMic { get => Bytes[6]; set => Bytes[6] = value; }

    public int MicSelect { get => Bytes[7] & 3; set => Bytes[7] = (byte)((Bytes[7] & ~3) | (value & 3)); }
    public bool NoiseCancelEnable { get => Bit(7, 3); set => Bit(7, 3, value); }

    public byte MuteLightMode { get => Bytes[8]; set => Bytes[8] = value; }

    public bool MicMute { get => Bit(9, 4); set => Bit(9, 4, value); }
    public bool SpeakerMute { get => Bit(9, 5); set => Bit(9, 5, value); }
    public bool HeadphoneMute { get => Bit(9, 6); set => Bit(9, 6, value); }

    public int TriggerMotorPowerReduction
    {
        get => Bytes[36] >> 4;
        set => Bytes[36] = (byte)((Bytes[36] & 0x0F) | ((value & 0x0F) << 4));
    }

    public int SpeakerCompPreGain { get => Bytes[37] & 7; set => Bytes[37] = (byte)((Bytes[37] & ~7) | (value & 7)); }

    public bool AllowLightBrightnessChange { get => Bit(38, 0); set => Bit(38, 0, value); }
    public bool AllowColorLightFadeAnimation { get => Bit(38, 1); set => Bit(38, 1, value); }
    public byte LightFadeAnimation { get => Bytes[41]; set => Bytes[41] = value; }
    public byte LightBrightness { get => Bytes[42]; set => Bytes[42] = value; }

    public byte LedRed { get => Bytes[44]; set => Bytes[44] = value; }
    public byte LedGreen { get => Bytes[45]; set => Bytes[45] = value; }
    public byte LedBlue { get => Bytes[46]; set => Bytes[46] = value; }

    public byte[] ToReport31()
    {
        var pkt = new byte[78];
        pkt[0] = 0x31;
        pkt[2] = 0x10;
        Bytes.CopyTo(pkt, 3);
        return pkt;
    }

    public byte[] ToReport32()
    {
        var pkt = new byte[142];
        pkt[0] = 0x32;
        pkt[1] = 0x10;
        pkt[2] = 0x90;
        pkt[3] = 0x3F;
        Bytes.CopyTo(pkt, 4);
        return pkt;
    }
}
