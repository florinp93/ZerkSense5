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
    public PcgwClient Pcgw { get; }

    public GameSession? Current { get; private set; }

    public event Action? Changed;
    public event Action<GameSession>? GameLaunched;

    public GameManager(BridgeService bridge, string dataDir)
    {
        _bridge = bridge;
        _cfg = bridge.Config;
        Pcgw = new PcgwClient(Path.Combine(dataDir, "pcgw-cache.json"));
        _detector = new GameDetector(() => _cfg.DetectFullscreenApps);
        _detector.GameStarted += OnStarted;
        _detector.GameStopped += OnStopped;
    }

    public GameProfile ProfileFor(GameInfo game)
    {
        lock (_cfg.Games)
        {
            if (!_cfg.Games.TryGetValue(game.Key, out var p))
            {
                p = new GameProfile { Name = game.Name, Source = game.Source };
                _cfg.Games[game.Key] = p;
            }
            return p;
        }
    }

    async void OnStarted(GameInfo game)
    {
        Log.Info($"Game detected: {game.Name} ({game.Source})");
        var profile = ProfileFor(game);
        profile.Name = game.Name;
        profile.LastPlayed = DateTime.Now;
        _cfg.Save();
        Apply(profile);

        var cached = Pcgw.Cached(game);
        Current = new GameSession(game, profile, cached, Verdict.For(cached), LookingUp: true, _cfg.AutoXboxMode);
        if (profile.Mode != ControllerMode.Auto || cached != null) _bridge.SetMode(Current.EffectiveMode);
        UpdateAudioHaptics();
        Changed?.Invoke();

        var info = await Pcgw.LookupAsync(game);
        if (Current?.Game.Key != game.Key) return;
        Current = Current with { Pcgw = info, Verdict = Verdict.For(info), LookingUp = false };
        Log.Info($"{game.Name}: {Current.Verdict.Summary}");
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

    public async Task RefreshLookupAsync()
    {
        if (Current is not { } s) return;
        Current = s with { LookingUp = true };
        Changed?.Invoke();
        var info = await Pcgw.LookupAsync(s.Game, refresh: true);
        if (Current?.Game.Key != s.Game.Key) return;
        Current = Current with { Pcgw = info, Verdict = Verdict.For(info), LookingUp = false };
        _bridge.SetMode(Current.EffectiveMode);
        UpdateAudioHaptics();
        Changed?.Invoke();
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
