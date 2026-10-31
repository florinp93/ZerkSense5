using System.Text.Json.Serialization;

namespace ZerkSense5.Games;

enum ControllerMode
{
    Auto,
    DualSense,
    Xbox360,
}

sealed class GameProfile
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public ControllerMode Mode { get; set; } = ControllerMode.Auto;
    public float? HapticsGain { get; set; }
    public int? TriggerReduce { get; set; }
    public byte[]? LightbarColor { get; set; }
    public bool? AudioHaptics { get; set; }

    public bool? CustomTriggers { get; set; }
    public TriggerEffect? TriggerL { get; set; }
    public TriggerEffect? TriggerR { get; set; }
    public DateTime LastPlayed { get; set; }
    public string? ExePath { get; set; }
    public string? PcgwPage { get; set; }
}

sealed record Verdict(ControllerMode Recommended, string Summary, bool FromPcgw)
{
    public static Verdict For(PcgwInfo? info)
    {
        if (info == null)
            return new Verdict(ControllerMode.DualSense, "Not on PCGamingWiki. Using DualSense mode.", false);

        if (info.SupportsDualSense)
        {
            string extra = info.WiredOnly ? " PCGamingWiki lists it as wired-only; Zerk Sense 5 presents a wired controller." : "";
            return new Verdict(ControllerMode.DualSense,
                info.Haptics == Support.Yes || info.AdaptiveTriggers == Support.Yes
                    ? "Native DualSense support with haptics and adaptive triggers." + extra
                    : "Native DualSense support." + extra, true);
        }

        if (info.PlayStation == Support.Yes)
            return new Verdict(ControllerMode.Xbox360,
                "Supports DualShock 4 but not DualSense. Xbox 360 mode gives the most reliable input.", true);

        if (info.XInput == Support.Yes || info.ControllerSupport is Support.Yes or Support.Limited)
            return new Verdict(ControllerMode.Xbox360,
                "No PlayStation controller support. Xbox 360 mode, with DS5 features added by Zerk Sense 5.", true);

        if (info.ControllerSupport == Support.No)
            return new Verdict(ControllerMode.Xbox360, "No controller support listed. Steam Input or remapping may be needed.", true);

        return new Verdict(ControllerMode.DualSense, "Controller support unknown on PCGamingWiki. Using DualSense mode.", true);
    }
}

sealed class ActiveSettings(Config cfg)
{
    [JsonIgnore] public GameProfile? Profile { get; set; }

    public float HapticsGain => Profile?.HapticsGain ?? cfg.HapticsGain;
    public int TriggerReduce => Profile?.TriggerReduce ?? cfg.TriggerReduce;
    public byte[] LightbarColor => Profile?.LightbarColor ?? cfg.LightbarColor;

    public TriggerEffect? TriggerL => Profile?.CustomTriggers switch { true => Profile.TriggerL, false => null, _ => cfg.TriggerL };
    public TriggerEffect? TriggerR => Profile?.CustomTriggers switch { true => Profile.TriggerR, false => null, _ => cfg.TriggerR };
}
