using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZerkSense5.Games;

namespace ZerkSense5.UI;

sealed record GameListItem(string Key, GameProfile Profile)
{
    public string Name => Profile.Name;
    public string Detail => $"{Profile.Source} · {(Profile.LastPlayed.Date == DateTime.Today ? "today" : Profile.LastPlayed.ToString("d MMM"))}";
}

partial class MainViewModel
{
    readonly GameManager _games;
    GameListItem? _selected;

    public ObservableCollection<GameListItem> GameList { get; } = new();
    public ICommand ShowOverlayNow { get; private set; } = null!;
    public ICommand RefreshPcgw { get; private set; } = null!;
    public ICommand ForgetGame { get; private set; } = null!;
    public ICommand SetProfileColor { get; private set; } = null!;

    void InitGames(Action showOverlay)
    {
        ShowOverlayNow = new Relay(_ => showOverlay());
        RefreshPcgw = new Relay(async _ => await _games.RefreshLookupAsync());
        ForgetGame = new Relay(_ =>
        {
            if (_selected == null || _selected.Key == _games.Current?.Game.Key) return;
            lock (_cfg.Games) _cfg.Games.Remove(_selected.Key);
            _cfg.Save();
            RebuildGameList();
        });
        SetProfileColor = new Relay(p =>
        {
            if (SelectedProfile == null) return;
            var c = (Color)ColorConverter.ConvertFromString((string)p!);
            SelectedProfile.LightbarColor = [c.R, c.G, c.B];
            ProfileChanged();
        });
        _games.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RebuildGameList();
            RefreshGames();
        });
        RebuildGameList();
    }

    void RebuildGameList()
    {
        string? keep = _selected?.Key ?? _games.Current?.Game.Key;
        List<GameListItem> items;
        lock (_cfg.Games)
            items = _cfg.Games.OrderByDescending(kv => kv.Value.LastPlayed).Select(kv => new GameListItem(kv.Key, kv.Value)).ToList();
        GameList.Clear();
        foreach (var i in items) GameList.Add(i);
        string? want = _games.Current?.Game.Key ?? keep;
        _selected = GameList.FirstOrDefault(i => i.Key == want) ?? GameList.FirstOrDefault();
        RefreshGames();
    }

    void RefreshGames()
    {
        foreach (var p in new[]
                 {
                     nameof(HasGame), nameof(NoGame), nameof(GameTitle), nameof(GameSource), nameof(GameVerdict),
                     nameof(GameModeText), nameof(GameModeBrush), nameof(GameModeNote), nameof(HasGameModeNote),
                     nameof(SelectedGame), nameof(HasSelection), nameof(SelectedName), nameof(SelectedRows),
                     nameof(SelectedPcgwUrl), nameof(HasSelectedPcgw), nameof(SelectedVerdict), nameof(ProfileMode),
                     nameof(OverrideHaptics), nameof(ProfileHaptics), nameof(ProfileHapticsText), nameof(OverrideTriggers),
                     nameof(ProfileTriggers), nameof(ProfileTriggersText), nameof(OverrideLightbar), nameof(ProfileLightbarBrush),
                     nameof(HasGames), nameof(CanForget), nameof(ProfileAudioHaptics),
                 })
            Notify(p);
        TriggerLab?.SelectedGameChanged();
    }

    GameSession? S => _games.Current;
    public bool HasGame => S != null;
    public bool NoGame => S == null;
    public string GameTitle => S?.Game.Name ?? "";
    public string GameSource => S == null ? "" : $"{S.Game.Source} · {System.IO.Path.GetFileName(S.Game.ExePath)}";
    public string GameVerdict => S == null ? "" : S.LookingUp ? "Looking up PCGamingWiki…" : S.Verdict.Summary;
    public string GameModeText => S == null ? "" :
        (S.Profile.Mode == ControllerMode.Auto ? "Recommended: " : "Profile: ") +
        (S.EffectiveMode == ControllerMode.Xbox360 ? "Xbox 360 mode" : "DualSense mode");
    public Brush GameModeBrush => new SolidColorBrush(S?.EffectiveMode == ControllerMode.Xbox360
        ? Color.FromRgb(0x10, 0x7C, 0x10) : Color.FromRgb(0x25, 0x63, 0xEB));
    public string GameModeNote => S?.EffectiveMode == ControllerMode.Xbox360
        ? "The game sees an Xbox 360 controller; its rumble plays as DualSense feedback." : "";
    public bool HasGameModeNote => GameModeNote.Length > 0;

    public bool HasGames => GameList.Count > 0;

    public GameListItem? SelectedGame
    {
        get => _selected;
        set
        {
            _selected = value;
            RefreshGames();
        }
    }

    public bool HasSelection => _selected != null;
    public bool CanForget => _selected != null && _selected.Key != _games.Current?.Game.Key;
    GameProfile? SelectedProfile => _selected?.Profile;
    PcgwInfo? SelectedPcgw => _selected == null ? null
        : _games.Current?.Game.Key == _selected.Key ? _games.Current.Pcgw : _games.Pcgw.Cached(_selected.Key);

    public string SelectedName => _selected?.Name ?? "";
    public string SelectedVerdict => _selected == null ? "" : Verdict.For(SelectedPcgw).Summary;
    public string SelectedPcgwUrl => SelectedPcgw?.Url ?? "";
    public bool HasSelectedPcgw => SelectedPcgw != null;

    public List<FeatureRow> SelectedRows
    {
        get
        {
            var p = SelectedPcgw;
            if (p == null) return new();
            return
            [
                new FeatureRow("HD haptics", p.Haptics),
                new FeatureRow("Adaptive triggers", p.AdaptiveTriggers),
                new FeatureRow("Lightbar", p.LightBar),
                new FeatureRow("Controller speaker", p.Speaker),
                new FeatureRow("Motion sensors", p.Motion),
                new FeatureRow("PlayStation prompts", p.PlayStationPrompts),
                new FeatureRow("Xbox controllers", p.XInput),
                new FeatureRow("Connection", p.ConnectionModes.Length > 0 ? Support.Yes : Support.Unknown,
                    p.ConnectionModes.Length > 0 ? p.ConnectionModes : null),
            ];
        }
    }

    void ProfileChanged()
    {
        if (SelectedProfile != null) _games.ProfileEdited(SelectedProfile);
        RefreshGames();
    }

    public int ProfileMode
    {
        get => (int)(SelectedProfile?.Mode ?? ControllerMode.Auto);
        set
        {
            if (SelectedProfile == null) return;
            SelectedProfile.Mode = (ControllerMode)value;
            ProfileChanged();
        }
    }

    public bool OverrideHaptics
    {
        get => SelectedProfile?.HapticsGain != null;
        set
        {
            if (SelectedProfile == null) return;
            SelectedProfile.HapticsGain = value ? _cfg.HapticsGain : null;
            ProfileChanged();
        }
    }

    public double ProfileHaptics
    {
        get => SelectedProfile?.HapticsGain ?? _cfg.HapticsGain;
        set
        {
            if (SelectedProfile?.HapticsGain == null) return;
            SelectedProfile.HapticsGain = (float)Math.Round(value, 2);
            ProfileChanged();
        }
    }
    public string ProfileHapticsText => $"{ProfileHaptics * 100:F0}%";

    public bool OverrideTriggers
    {
        get => SelectedProfile?.TriggerReduce != null;
        set
        {
            if (SelectedProfile == null) return;
            SelectedProfile.TriggerReduce = value ? _cfg.TriggerReduce : null;
            ProfileChanged();
        }
    }

    public double ProfileTriggers
    {
        get => SelectedProfile?.TriggerReduce ?? _cfg.TriggerReduce;
        set
        {
            if (SelectedProfile?.TriggerReduce == null) return;
            SelectedProfile.TriggerReduce = (int)value;
            ProfileChanged();
        }
    }
    public string ProfileTriggersText => (int)ProfileTriggers == 0 ? "Full" : $"−{(int)ProfileTriggers * 10}%";

    public bool OverrideLightbar
    {
        get => SelectedProfile?.LightbarColor != null;
        set
        {
            if (SelectedProfile == null) return;
            SelectedProfile.LightbarColor = value ? (byte[])_cfg.LightbarColor.Clone() : null;
            ProfileChanged();
        }
    }

    public Brush ProfileLightbarBrush
    {
        get
        {
            var c = SelectedProfile?.LightbarColor ?? _cfg.LightbarColor;
            return new SolidColorBrush(Color.FromRgb(c[0], c[1], c[2]));
        }
    }

    public bool ShowOverlay
    {
        get => _cfg.ShowOverlay;
        set { _cfg.ShowOverlay = value; Saved(); }
    }

    public double OverlaySeconds
    {
        get => _cfg.OverlaySeconds;
        set { _cfg.OverlaySeconds = (int)value; Saved(); Notify(nameof(OverlaySecondsText)); }
    }
    public string OverlaySecondsText => $"{_cfg.OverlaySeconds} s";

    public bool DetectFullscreenApps
    {
        get => _cfg.DetectFullscreenApps;
        set { _cfg.DetectFullscreenApps = value; Saved(); }
    }
}
