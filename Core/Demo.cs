using ZerkSense5.Games;

namespace ZerkSense5;

static class Demo
{
    static readonly (int id, string name)[] Games =
    [
        (1091500, "Cyberpunk 2077"),
        (1245620, "ELDEN RING"),
        (1817070, "Marvel's Spider-Man Remastered"),
        (2215430, "Ghost of Tsushima DIRECTOR'S CUT"),
        (1649240, "Returnal"),
        (1850570, "DEATH STRANDING DIRECTOR'S CUT"),
        (814380, "Sekiro: Shadows Die Twice"),
        (105600, "Terraria"),
        (1145360, "Hades"),
        (367520, "Hollow Knight"),
        (12120, "Grand Theft Auto: San Andreas"),
    ];

    public static List<LibraryGame> Library =>
        Games.Select(g => new LibraryGame($"steam:{g.id}", g.name, "Steam", g.id, $@"C:\Demo\{g.id}")).ToList();

    public static GameInfo Running => new("steam:1091500", "Cyberpunk 2077", "Steam", 1091500, "", 0);

    public static ControllerStatus Controller => new(false, 85, PowerState.Charging, false, false, false, 612);

    public static byte[] Input
    {
        get
        {
            var s = new byte[63];
            s[0] = 196; s[1] = 92; s[2] = 128; s[3] = 128;
            s[5] = 178;
            s[7] = 0x08 | 0x20;
            s[8] = 0x02;
            s[32] = 0x01; s[33] = 0x40; s[34] = 0x46; s[35] = 0x19;
            s[36] = 0x80;
            return s;
        }
    }

    public static Config CreateConfig()
    {
        string path = Path.Combine(Path.GetTempPath(), "ZerkSense5", "demo-config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        var cfg = Config.Load(path);
        cfg.ShowOverlay = false;
        cfg.LightbarColor = [0x00, 0x40, 0xFF];
        cfg.TriggerR = new TriggerEffect { Type = TriggerEffectType.Weapon, Start = 3, End = 6, Strength = 7 };
        cfg.SavedEffects.Add(new TriggerEffect { Type = TriggerEffectType.Bow, Start = 1, End = 7, Strength = 5, EndStrength = 8, Name = "Bow draw" });
        cfg.SavedEffects.Add(new TriggerEffect { Type = TriggerEffectType.Slope, Start = 1, End = 8, Strength = 1, EndStrength = 7, Name = "Accelerator" });

        var now = DateTime.Now;
        cfg.Games["steam:1091500"] = new GameProfile { Name = "Cyberpunk 2077", Source = "Steam", LastPlayed = now };
        cfg.Games["steam:1245620"] = new GameProfile { Name = "ELDEN RING", Source = "Steam", LastPlayed = now.AddDays(-1), AudioHaptics = true };
        cfg.Games["steam:1817070"] = new GameProfile { Name = "Marvel's Spider-Man Remastered", Source = "Steam", LastPlayed = now.AddDays(-3), LightbarColor = [0xFF, 0x2D, 0x2D] };
        cfg.Games["steam:105600"] = new GameProfile { Name = "Terraria", Source = "Steam", LastPlayed = now.AddDays(-6), Mode = ControllerMode.Xbox360 };
        cfg.Save();
        return cfg;
    }
}
