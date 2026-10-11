using System.Text.Json;
using System.Text.Json.Serialization;
using ZerkSense5.Games;

namespace ZerkSense5;

sealed class Config
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    string? _path;
    ActiveSettings? _now;

    public float HapticsGain { get; set; } = 1.0f;
    public int SpeakerSelect { get; set; }
    public int MicSelect { get; set; }
    public int SpeakerGain { get; set; }
    public bool LockVolume { get; set; }
    public int PacketMode { get; set; }
    public int AudioBufferLength { get; set; } = 48;

    public int AudioHapticsMode { get; set; } = 1;
    public int AudioHapticsIntensity { get; set; } = 100;
    public int AudioHapticsCutoff { get; set; } = 120;
    public int AudioHapticsGate { get; set; } = 5;
    public int AudioHapticsSmoothness { get; set; } = 80;

    public bool AutoXboxMode { get; set; } = true;
    public int RumbleStyle { get; set; }
    public int RumbleStrength { get; set; } = 100;

    public int TriggerReduce { get; set; }
    public int TriggerKick { get; set; }
    public TriggerEffect? TriggerL { get; set; }
    public TriggerEffect? TriggerR { get; set; }
    public List<TriggerEffect> SavedEffects { get; set; } = new();

    public byte[] LightbarColor { get; set; } = [0xFF, 0xD7, 0x00];
    public int LightingMode { get; set; }
    public int LightingSpeed { get; set; } = 5;

    public bool ShowOverlay { get; set; } = true;
    public int OverlaySeconds { get; set; } = 8;
    public bool DetectFullscreenApps { get; set; } = true;
    public Dictionary<string, GameProfile> Games { get; set; } = new();

    public bool StartMinimized { get; set; }
    public bool LowBatteryAlert { get; set; } = true;
    public bool UseHidHide { get; set; } = true;
    public string UsbipPath { get; set; } = "";
    public int UsbipPort { get; set; } = 3240;

    [JsonIgnore] public ActiveSettings Now => _now ??= new ActiveSettings(this);

    public static Config Load(string path)
    {
        Config cfg;
        if (File.Exists(path))
        {
            cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new Config();
        }
        else
        {
            cfg = new Config();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, Json));
        }
        cfg.Validate();
        cfg._path = path;
        return cfg;
    }

    public void Save()
    {
        if (_path == null) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException e)
        {
            Log.Warn($"Could not save settings: {e.Message}");
        }
    }

    void Validate()
    {
        if (float.IsNaN(HapticsGain) || HapticsGain < 1.0f || HapticsGain > 2.0f) HapticsGain = 1.0f;
        if (SpeakerSelect is < 0 or > 3) SpeakerSelect = 0;
        if (MicSelect is < 0 or > 3) MicSelect = 0;
        SpeakerGain = Math.Clamp(SpeakerGain, 0, 7);
        if (PacketMode is < 0 or > 1) PacketMode = 0;
        if (AudioBufferLength is < 16 or > 128) AudioBufferLength = 48;

        if (AudioHapticsMode is < 0 or > 2) AudioHapticsMode = 1;
        AudioHapticsIntensity = Math.Clamp(AudioHapticsIntensity, 0, 300);
        AudioHapticsCutoff = Math.Clamp(AudioHapticsCutoff, 30, 250);
        AudioHapticsGate = Math.Clamp(AudioHapticsGate, 0, 30);
        AudioHapticsSmoothness = Math.Clamp(AudioHapticsSmoothness, 20, 400);

        if (RumbleStyle is < 0 or > 1) RumbleStyle = 0;
        RumbleStrength = Math.Clamp(RumbleStrength, 0, 200);
        TriggerReduce = Math.Clamp(TriggerReduce, 0, 10);
        if (TriggerKick is < 0 or > 2) TriggerKick = 0;
        SavedEffects ??= new();

        if (LightbarColor is not { Length: 3 }) LightbarColor = [0xFF, 0xD7, 0x00];
        if (LightingMode is < 0 or > 4) LightingMode = 0;
        LightingSpeed = Math.Clamp(LightingSpeed, 1, 10);

        OverlaySeconds = Math.Clamp(OverlaySeconds, 3, 30);
        Games ??= new();
    }
}
