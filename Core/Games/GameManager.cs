using System.Text.RegularExpressions;

namespace ZerkSense5.Games;

sealed record GameSession(GameInfo Game, GameProfile Profile, PcgwInfo? Pcgw, Verdict Verdict, bool LookingUp, bool AutoXbox)
{
    public ControllerMode EffectiveMode => Profile.Mode switch
    {
        ControllerMode.Auto => AutoXbox ? Verdict.Recommended : ControllerMode.DualSense,
        var m => m,
    };
}

sealed class GameManager : IDisposable
{
    readonly BridgeService _bridge;
    readonly Config _cfg;
    readonly GameDetector _detector;

    public GameManager(BridgeService bridge, string dataDir)
    {
        _bridge = bridge;
        _cfg = bridge.Config;
        Pcgw = new PcgwClient(Path.Combine(dataDir, "pcgw-cache.json"));
        _detector = new GameDetector(_cfg);
        _detector.GameStarted += OnStarted;
        _detector.GameStopped += OnStopped;
        _detector.LibraryChanged += () =>
        {
            MigrateProfiles();
            Changed?.Invoke();
        };
        Task.Run(_detector.RescanLibrary);
    }

    public event Action? Changed;
    public event Action<GameSession>? GameLaunched;

    public PcgwClient Pcgw { get; }
    public GameSession? Current { get; private set; }
    public IReadOnlyList<LibraryGame> Library => _detector.Library;

    public GameProfile ProfileFor(string key, string name, string source)
    {
        lock (_cfg.Games)
        {
            if (!_cfg.Games.TryGetValue(key, out var p))
            {
                p = new GameProfile { Name = name, Source = source };
                _cfg.Games[key] = p;
            }
            return p;
        }
    }

    public GameProfile? ExistingProfile(string key)
    {
        lock (_cfg.Games) return _cfg.Games.GetValueOrDefault(key);
    }

    static GameRef RefFor(string key, string name, int? steamAppId, GameProfile? profile, string[]? aliases) =>
        new(key, name, steamAppId, profile?.PcgwPage, aliases);

    static readonly HashSet<string> LocalSources = new(StringComparer.OrdinalIgnoreCase) { "Fullscreen app", "Games folder", "Added manually" };

    public bool AdoptPcgwTitle(string key, PcgwInfo? info) => ExistingProfile(key) is { } profile && AdoptPcgwTitle(profile, info);

    bool AdoptPcgwTitle(GameProfile profile, PcgwInfo? info)
    {
        if (info == null || !LocalSources.Contains(profile.Source)) return false;
        string title = Regex.Replace(info.Page, @"\s*\(.*?\)$", "");
        if (profile.Name == title) return false;
        profile.Name = title;
        _cfg.Save();
        return true;
    }

    void MigrateProfiles()
    {
        bool moved = false;
        lock (_cfg.Games)
            foreach (var (key, old) in _cfg.Games.Where(kv => kv.Key.StartsWith("exe:") && kv.Value.Source == "Fullscreen app").ToList())
            {
                string file = key[4..];
                var target = Library.FirstOrDefault(g => g.Source != "Added manually" && ContainsFile(g.InstallDir, file));
                if (target == null || _cfg.Games.ContainsKey(target.Key)) continue;
                old.Source = target.Source;
                old.Name = target.Name;
                _cfg.Games[target.Key] = old;
                _cfg.Games.Remove(key);
                moved = true;
            }
        if (moved) _cfg.Save();
    }

    static bool ContainsFile(string dir, string file)
    {
        try
        {
            return Directory.EnumerateFiles(dir, file, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    async void OnStarted(GameInfo game)
    {
        Log.Info($"Game detected: {game.Name} ({game.Source})");
        var profile = ProfileFor(game.Key, game.Name, game.Source);
        profile.Name = game.Name;
        profile.ExePath = game.ExePath;
        profile.LastPlayed = DateTime.Now;
        _cfg.Save();
        Apply(profile);

        var cached = profile.PcgwPage == PcgwClient.NoPage ? null : Pcgw.Cached(game.Key);
        Current = new GameSession(game, profile, cached, Verdict.For(cached), LookingUp: true, _cfg.AutoXboxMode);
        if (profile.Mode != ControllerMode.Auto || cached != null) _bridge.SetMode(Current.EffectiveMode);
        UpdateAudioHaptics();
        Changed?.Invoke();

        var info = await Pcgw.LookupAsync(RefFor(game.Key, game.Name, game.SteamAppId, profile, game.Aliases));
        if (profile.PcgwPage == null) AdoptPcgwTitle(profile, info);
        if (Current?.Game.Key != game.Key) return;
        Current = Current with { Pcgw = info, Verdict = Verdict.For(info), LookingUp = false };
        Log.Info($"{game.Name}: {Current.Verdict.Summary}");
        if (info?.PromptMod != null) Log.Info($"{game.Name}: a PlayStation button prompt mod is listed on PCGamingWiki");
        _bridge.SetMode(Current.EffectiveMode);
        UpdateAudioHaptics();
        Changed?.Invoke();
        GameLaunched?.Invoke(Current);
    }

    void OnStopped(GameInfo game)
    {
        if (Current?.Game.Key != game.Key) return;
        Log.Info($"Game closed: {game.Name}");
        Current = null;
        Apply(null);
        _bridge.SetMode(ControllerMode.DualSense);
        UpdateAudioHaptics();
        Changed?.Invoke();
    }

    public void ProfileEdited(GameProfile profile)
    {
        _cfg.Save();
        if (Current?.Profile == profile) Refresh();
    }

    public void Refresh()
    {
        if (Current is not { } s) return;
        Current = s with { AutoXbox = _cfg.AutoXboxMode };
        Apply(Current.Profile);
        if (!Current.LookingUp || Current.Profile.Mode != ControllerMode.Auto) _bridge.SetMode(Current.EffectiveMode);
        UpdateAudioHaptics();
        Changed?.Invoke();
    }

    void Apply(GameProfile? profile)
    {
        _cfg.Now.Profile = profile;
        _bridge.ApplyLightbar();
        _bridge.ApplyTriggerReduce();
        _bridge.Triggers.Apply();
    }

    public Task<PcgwInfo?> LookupAsync(string key, string name, int? steamAppId, bool refresh = false)
    {
        var library = Library.FirstOrDefault(g => g.Key == key);
        var aliases = Current?.Game.Key == key ? Current.Game.Aliases : library?.Aliases;
        return Pcgw.LookupAsync(RefFor(key, name, steamAppId ?? library?.SteamAppId, ExistingProfile(key), aliases), refresh);
    }

    public async Task RefreshLookupAsync()
    {
        if (Current is not { } s) return;
        Current = s with { LookingUp = true };
        Changed?.Invoke();
        var info = await LookupAsync(s.Game.Key, s.Game.Name, s.Game.SteamAppId, refresh: true);
        if (Current?.Game.Key != s.Game.Key) return;
        Current = Current with { Pcgw = info, Verdict = Verdict.For(info), LookingUp = false };
        _bridge.SetMode(Current.EffectiveMode);
        UpdateAudioHaptics();
        Changed?.Invoke();
    }

    public async Task<PcgwInfo?> SetPcgwMatchAsync(string key, string name, string source, string? page)
    {
        var profile = ProfileFor(key, name, source);
        profile.PcgwPage = page;
        _cfg.Save();
        Pcgw.Forget(key);
        if (page == PcgwClient.NoPage) Log.Info($"{name}: marked as not on PCGamingWiki");
        else if (page != null) Log.Info($"{name}: matched to PCGamingWiki page \"{page}\"");

        int? appId = Current?.Game.Key == key ? Current.Game.SteamAppId : Library.FirstOrDefault(g => g.Key == key)?.SteamAppId;
        var info = await LookupAsync(key, name, appId, refresh: true);
        if (page != null && page != PcgwClient.NoPage) AdoptPcgwTitle(profile, info);
        if (Current?.Game.Key == key)
        {
            Current = Current with { Pcgw = info, Verdict = Verdict.For(info), LookingUp = false };
            _bridge.SetMode(Current.EffectiveMode);
            UpdateAudioHaptics();
        }
        Changed?.Invoke();
        return info;
    }

    public void Ignore(string key)
    {
        lock (_cfg.IgnoredGames) _cfg.IgnoredGames.Add(key);
        lock (_cfg.Games) _cfg.Games.Remove(key);
        _cfg.Save();
        if (Current?.Game.Key == key) OnStopped(Current.Game);
        Changed?.Invoke();
    }

    public void RescanLibrary() => Task.Run(_detector.RescanLibrary);

    public void UnhideAll()
    {
        lock (_cfg.IgnoredGames) _cfg.IgnoredGames.Clear();
        _cfg.Save();
        RescanLibrary();
    }

    public void AddGame(string exe, string name)
    {
        string key = GameLibrary.CustomKey(exe);
        lock (_cfg.CustomGames)
        {
            _cfg.CustomGames.RemoveAll(c => string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase));
            _cfg.CustomGames.Add(new CustomGame(exe, name));
        }
        lock (_cfg.IgnoredGames) _cfg.IgnoredGames.Remove(key);
        var profile = ProfileFor(key, name, "Added manually");
        profile.Name = name;
        profile.ExePath = exe;
        _cfg.Save();
        Log.Info($"Added game: {name}");
        Task.Run(_detector.RescanLibrary);
    }

    public bool AudioHapticsOn => Current is { } s && (s.Profile.AudioHaptics ?? _cfg.AudioHapticsMode switch
    {
        2 => true,
        1 => s.EffectiveMode == ControllerMode.Xbox360 || s.Pcgw?.Haptics != Support.Yes,
        _ => false,
    });

    public void UpdateAudioHaptics() => _bridge.SetAudioHapticsTarget(AudioHapticsOn ? Current!.Game.ProcessId : null);

    public void Dispose() => _detector.Dispose();
}
