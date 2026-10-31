using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZerkSense5.Games;

namespace ZerkSense5.UI;

sealed class GameListItem : INotifyPropertyChanged
{
    ImageSource? _icon;

    public GameListItem(string key, string name, string source, int? steamAppId, string? exe, DateTime lastPlayed, bool running)
    {
        Key = key;
        Name = name;
        Source = source;
        SteamAppId = steamAppId;
        Exe = exe;
        LastPlayed = lastPlayed;
        Running = running;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public string Name { get; }
    public string Source { get; }
    public int? SteamAppId { get; }
    public string? Exe { get; }
    public DateTime LastPlayed { get; }
    public bool Running { get; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool HasIcon => _icon != null;

    public string Detail => Running ? $"{Source} · playing now"
        : LastPlayed == default ? $"{Source} · installed"
        : $"{Source} · {(LastPlayed.Date == DateTime.Today ? "today" : LastPlayed.ToString("d MMM"))}";

    public override string ToString() => Name;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }
    }
}

partial class MainViewModel
{
    readonly GameManager _games;
    GameListItem? _selected;
    PcgwInfo? _selectedPcgw;
    bool _selectedLookingUp;
    ImageSource? _selectedCover;
    string _gameFilter = "";
    bool _matching;
    bool _searching;
    string _matchQuery = "";

    public ObservableCollection<GameListItem> GameList { get; } = new();
    public ObservableCollection<string> MatchResults { get; } = new();
    public ICommand ShowOverlayNow { get; private set; } = null!;
    public ICommand RefreshPcgw { get; private set; } = null!;
    public ICommand SetProfileColor { get; private set; } = null!;
    public ICommand ToggleMatch { get; private set; } = null!;
    public ICommand SearchMatch { get; private set; } = null!;
    public ICommand PickMatch { get; private set; } = null!;
    public ICommand NoMatch { get; private set; } = null!;
    public ICommand AutoMatch { get; private set; } = null!;
    public ICommand NotAGame { get; private set; } = null!;
    public ICommand AddGame { get; private set; } = null!;
    public ICommand OpenLink { get; private set; } = null!;

    void InitGames(Action showOverlay)
    {
        ShowOverlayNow = new Relay(_ => showOverlay());
        RefreshPcgw = new Relay(async _ =>
        {
            if (_selected == null) return;
            if (_games.Current?.Game.Key == _selected.Key) await _games.RefreshLookupAsync();
            else await LoadSelectedAsync(refresh: true);
        });
        SetProfileColor = new Relay(p =>
        {
            if (EditProfile is not { } profile) return;
            var c = (Color)ColorConverter.ConvertFromString((string)p!);
            profile.LightbarColor = [c.R, c.G, c.B];
            ProfileChanged();
        });
        ToggleMatch = new Relay(_ =>
        {
            Matching = !_matching;
            if (_matching && _selected != null)
            {
                MatchQuery = _selected.Name;
                _ = SearchAsync();
            }
        });
        SearchMatch = new Relay(async _ => await SearchAsync());
        PickMatch = new Relay(async p => await SetMatchAsync((string)p!));
        NoMatch = new Relay(async _ => await SetMatchAsync(PcgwClient.NoPage));
        AutoMatch = new Relay(async _ => await SetMatchAsync(null));
        NotAGame = new Relay(_ =>
        {
            if (_selected == null) return;
            _games.Ignore(_selected.Key);
            _selected = null;
            RebuildGameList();
        });
        AddGame = new Relay(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Games|*.exe", Title = "Choose the game's .exe" };
            if (dlg.ShowDialog() != true) return;
            string name;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(dlg.FileName);
                name = !string.IsNullOrWhiteSpace(v.ProductName) && v.ProductName.Trim().Length > 2 ? v.ProductName.Trim() : Path.GetFileName(Path.GetDirectoryName(dlg.FileName))!;
            }
            catch (FileNotFoundException)
            {
                name = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
            _games.AddGame(dlg.FileName, name);
            _selected = null;
            RebuildGameList(GameLibrary.CustomKey(dlg.FileName));
        });
        OpenLink = new Relay(p =>
        {
            if (p is string url && url.StartsWith("http")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        });
        _games.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RebuildGameList();
            if (_selected != null && _games.Current?.Game.Key == _selected.Key)
            {
                _selectedPcgw = _games.Current.Pcgw;
                _selectedLookingUp = _games.Current.LookingUp;
                _ = LoadCoverAsync();
            }
            RefreshGames();
        });
        RebuildGameList();
    }

    void RebuildGameList(string? select = null)
    {
        string? keep = select ?? _selected?.Key ?? _games.Current?.Game.Key;
        var running = _games.Current?.Game.Key;
        var items = new Dictionary<string, GameListItem>();
        lock (_cfg.Games)
            foreach (var (key, p) in _cfg.Games)
                items[key] = new GameListItem(key, p.Name, p.Source, null, p.ExePath, p.LastPlayed, key == running);
        if (_cfg.ShowInstalledGames)
            foreach (var g in _games.Library)
            {
                if (items.TryGetValue(g.Key, out var played))
                {
                    items[g.Key] = new GameListItem(g.Key, played.Name, played.Source, g.SteamAppId, played.Exe ?? g.Exe, played.LastPlayed, played.Running);
                    continue;
                }
                bool ignored;
                lock (_cfg.IgnoredGames) ignored = _cfg.IgnoredGames.Contains(g.Key);
                if (!ignored) items[g.Key] = new GameListItem(g.Key, g.Name, g.Source, g.SteamAppId, g.Exe, default, false);
            }

        var ordered = items.Values
            .Where(i => _gameFilter.Length == 0 || i.Name.Contains(_gameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Running)
            .ThenByDescending(i => i.LastPlayed)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        GameList.Clear();
        foreach (var item in ordered)
        {
            GameList.Add(item);
            _ = LoadIconAsync(item);
        }
        string? want = running ?? keep;
        var next = GameList.FirstOrDefault(i => i.Key == want) ?? GameList.FirstOrDefault(i => i.Key == keep) ?? GameList.FirstOrDefault();
        if (next?.Key != _selected?.Key) Select(next);
        else _selected = next;
        Notify(nameof(HasGames));
    }

    async Task LoadIconAsync(GameListItem item)
    {
        var lib = _games.Library.FirstOrDefault(g => g.Key == item.Key);
        string? exe = item.Exe ?? (lib == null ? null : await Task.Run(() => GameLibrary.GuessExe(lib)));
        item.Icon = await GameArt.IconAsync(exe);
        if (item.Key == _selected?.Key) Notify(nameof(SelectedIcon));
        if (item.Key == _games.Current?.Game.Key) Notify(nameof(GameIcon));
    }

    void Select(GameListItem? item)
    {
        _selected = item;
        _selectedPcgw = null;
        _selectedCover = null;
        _matching = false;
        MatchResults.Clear();
        RefreshGames();
        if (item != null) _ = LoadSelectedAsync(refresh: false);
    }

    async Task LoadSelectedAsync(bool refresh)
    {
        var item = _selected;
        if (item == null) return;
        if (_games.Current?.Game.Key == item.Key && !refresh)
        {
            _selectedPcgw = _games.Current.Pcgw;
            _selectedLookingUp = _games.Current.LookingUp;
        }
        else
        {
            _selectedPcgw = _games.ExistingProfile(item.Key)?.PcgwPage == PcgwClient.NoPage ? null : _games.Pcgw.Cached(item.Key);
            _selectedLookingUp = true;
            RefreshGames();
            var info = await _games.LookupAsync(item.Key, item.Name, item.SteamAppId, refresh);
            if (_selected?.Key != item.Key) return;
            bool renamed = _games.ExistingProfile(item.Key)?.PcgwPage == null && _games.AdoptPcgwTitle(item.Key, info);
            _selectedPcgw = info;
            if (renamed) RebuildGameList();
            _selectedLookingUp = false;
        }
        RefreshGames();
        await LoadCoverAsync();
    }

    async Task LoadCoverAsync()
    {
        var key = _selected?.Key;
        var appId = _selected?.SteamAppId ?? _games.Library.FirstOrDefault(g => g.Key == key)?.SteamAppId;
        var cover = await GameArt.CoverAsync(appId, _selectedPcgw?.CoverUrl);
        if (_selected?.Key != key) return;
        _selectedCover = cover;
        Notify(nameof(SelectedCover));
        Notify(nameof(HasCover));
    }

    async Task SearchAsync()
    {
        Searching = true;
        var results = await _games.Pcgw.SearchAsync(_matchQuery);
        MatchResults.Clear();
        foreach (var r in results) MatchResults.Add(r);
        Searching = false;
        Notify(nameof(NoMatchResults));
    }

    async Task SetMatchAsync(string? page)
    {
        var item = _selected;
        if (item == null) return;
        Matching = false;
        _selectedLookingUp = true;
        RefreshGames();
        var info = await _games.SetPcgwMatchAsync(item.Key, item.Name, item.Source, page);
        if (_selected?.Key != item.Key) return;
        _selectedPcgw = info;
        _selectedLookingUp = false;
        _selectedCover = null;
        RefreshGames();
        await LoadCoverAsync();
    }

    void RefreshGames()
    {
        foreach (var p in new[]
                 {
                     nameof(HasGame), nameof(NoGame), nameof(GameTitle), nameof(GameSource), nameof(GameVerdict), nameof(GameIcon),
                     nameof(GameModeText), nameof(GameModeBrush), nameof(GameModeNote), nameof(HasGameModeNote),
                     nameof(SelectedGame), nameof(HasSelection), nameof(SelectedName), nameof(SelectedSource), nameof(SelectedIcon),
                     nameof(SelectedRows), nameof(SelectedPcgwUrl), nameof(HasSelectedPcgw), nameof(SelectedVerdict),
                     nameof(MatchText), nameof(IsAutoMatch), nameof(Matching), nameof(SelectedCover), nameof(HasCover),
                     nameof(HasPromptMod), nameof(PromptModText), nameof(PromptModLinks), nameof(PromptModTitle),
                     nameof(ProfileMode), nameof(OverrideHaptics), nameof(ProfileHaptics), nameof(ProfileHapticsText),
                     nameof(OverrideTriggers), nameof(ProfileTriggers), nameof(ProfileTriggersText), nameof(OverrideLightbar),
                     nameof(ProfileLightbarBrush), nameof(HasGames), nameof(ProfileAudioHaptics),
                 })
            Notify(p);
        TriggerLab?.SelectedGameChanged();
    }

    GameSession? S => _games.Current;
    public bool HasGame => S != null;
    public bool NoGame => S == null;
    public string GameTitle => S?.Game.Name ?? "";
    public ImageSource? GameIcon => S == null ? null : GameList.FirstOrDefault(i => i.Key == S.Game.Key)?.Icon;
    public string GameSource => S == null ? "" : $"{S.Game.Source} · {Path.GetFileName(S.Game.ExePath)}";
    public string GameVerdict => S == null ? "" : S.LookingUp ? "Looking up PCGamingWiki…" : S.Verdict.Summary;
    public string GameModeText => S == null ? "" :
        (S.Profile.Mode == ControllerMode.Auto ? "Recommended: " : "Profile: ") +
        (S.EffectiveMode == ControllerMode.Xbox360 ? "Xbox 360 mode" : "DualSense mode");
    public Brush GameModeBrush => new SolidColorBrush(S?.EffectiveMode == ControllerMode.Xbox360
        ? Color.FromRgb(0x10, 0x7C, 0x10) : Color.FromRgb(0x25, 0x63, 0xEB));
    public string GameModeNote => S?.EffectiveMode == ControllerMode.Xbox360
        ? "The game sees an Xbox 360 controller; its rumble plays as DualSense feedback." : "";
    public bool HasGameModeNote => GameModeNote.Length > 0;

    public bool HasGames => GameList.Count > 0 || _gameFilter.Length > 0;

    public string GameFilter
    {
        get => _gameFilter;
        set
        {
            _gameFilter = value.Trim();
            Notify(nameof(FilterEmpty));
            RebuildGameList();
        }
    }

    public bool FilterEmpty => _gameFilter.Length == 0;

    public bool ShowInstalledGames
    {
        get => _cfg.ShowInstalledGames;
        set
        {
            _cfg.ShowInstalledGames = value;
            Saved();
            RebuildGameList();
        }
    }

    public GameListItem? SelectedGame
    {
        get => _selected;
        set
        {
            if (value?.Key == _selected?.Key) return;
            Select(value);
        }
    }

    public bool HasSelection => _selected != null;
    GameProfile? SelectedProfile => _selected == null ? null : _games.ExistingProfile(_selected.Key);
    GameProfile? EditProfile => _selected == null ? null : _games.ProfileFor(_selected.Key, _selected.Name, _selected.Source);
    PcgwInfo? SelectedPcgw => _selectedPcgw;

    public string SelectedName => _selected?.Name ?? "";
    public string SelectedSource => _selected?.Detail ?? "";
    public ImageSource? SelectedIcon => _selected?.Icon;
    public ImageSource? SelectedCover => _selectedCover;
    public bool HasCover => _selectedCover != null;
    public string SelectedVerdict => _selected == null ? "" : _selectedLookingUp && SelectedPcgw == null ? "Looking up PCGamingWiki…" : Verdict.For(SelectedPcgw).Summary;
    public string SelectedPcgwUrl => SelectedPcgw?.Url ?? "";
    public bool HasSelectedPcgw => SelectedPcgw != null;

    string? SelectedOverride => _selected == null ? null : _games.ExistingProfile(_selected.Key)?.PcgwPage;
    public bool IsAutoMatch => SelectedOverride == null;

    public string MatchText => SelectedOverride == PcgwClient.NoPage ? "Marked as not on PCGamingWiki"
        : SelectedPcgw != null ? $"PCGamingWiki: {SelectedPcgw.Page}{(IsAutoMatch ? "" : " (set by you)")}"
        : _selectedLookingUp ? "Looking up PCGamingWiki…"
        : "No PCGamingWiki page found";

    public bool Matching
    {
        get => _matching;
        set
        {
            _matching = value;
            Notify();
        }
    }

    public string MatchQuery
    {
        get => _matchQuery;
        set
        {
            _matchQuery = value;
            Notify();
        }
    }

    public bool Searching
    {
        get => _searching;
        set
        {
            _searching = value;
            Notify();
        }
    }

    public bool NoMatchResults => !_searching && MatchResults.Count == 0;

    PromptMod? Mod => SelectedPcgw?.PromptMod;
    public bool HasPromptMod => Mod != null;
    public string PromptModTitle => SelectedPcgw?.PlayStationPrompts == Support.Yes
        ? "PlayStation button prompts: tip from PCGamingWiki"
        : "PlayStation button prompts: mod available";
    public string PromptModText => Mod?.Text ?? "";
    public IReadOnlyList<WikiLink> PromptModLinks => Mod?.Links ?? [];

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
                new FeatureRow("PlayStation prompts", p.PlayStationPrompts, p.PromptMod != null && p.PlayStationPrompts != Support.Yes ? "Mod available" : null),
                new FeatureRow("Xbox controllers", p.XInput),
                new FeatureRow("Connection", p.ConnectionModes.Length > 0 ? Support.Yes : Support.Unknown,
                    p.ConnectionModes.Length > 0 ? p.ConnectionModes : null),
            ];
        }
    }

    void ProfileChanged()
    {
        if (EditProfile is { } profile) _games.ProfileEdited(profile);
        RefreshGames();
    }

    public int ProfileMode
    {
        get => (int)(SelectedProfile?.Mode ?? ControllerMode.Auto);
        set
        {
            if (EditProfile is not { } profile) return;
            profile.Mode = (ControllerMode)value;
            ProfileChanged();
        }
    }

    public bool OverrideHaptics
    {
        get => SelectedProfile?.HapticsGain != null;
        set
        {
            if (EditProfile is not { } profile) return;
            profile.HapticsGain = value ? _cfg.HapticsGain : null;
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
            if (EditProfile is not { } profile) return;
            profile.TriggerReduce = value ? _cfg.TriggerReduce : null;
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
            if (EditProfile is not { } profile) return;
            profile.LightbarColor = value ? (byte[])_cfg.LightbarColor.Clone() : null;
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
