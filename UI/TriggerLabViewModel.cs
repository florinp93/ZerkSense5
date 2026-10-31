using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ZerkSense5.Games;

namespace ZerkSense5.UI;

sealed class TriggerEditor : INotifyPropertyChanged
{
    readonly int _side;
    readonly Func<TriggerEffect?> _get;
    readonly Action<TriggerEffect?> _set;
    readonly TriggerManager _triggers;
    TriggerEffect _draft = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public TriggerEditor(int side, Func<TriggerEffect?> get, Action<TriggerEffect?> set, TriggerManager triggers)
    {
        _side = side;
        _get = get;
        _set = set;
        _triggers = triggers;
        Try = new Relay(_ => _triggers.Preview(_side, _draft));
        Reload();
    }

    public string Title => _side == 0 ? "L2 · left trigger" : "R2 · right trigger";
    public ICommand Try { get; }

    public void Reload()
    {
        var e = _get();
        if (e != null) _draft = e.Clone();
        foreach (var p in typeof(TriggerEditor).GetProperties()) Notify(p.Name);
    }

    void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    void Changed()
    {
        if (Custom) _set(_draft.Clone());
        foreach (var p in new[] { nameof(Bars), nameof(Summary), nameof(ShowEnd), nameof(ShowEndStrength), nameof(ShowFrequency),
                     nameof(ShowParams), nameof(EndStrengthLabel), nameof(IsCaptured) })
            Notify(p);
    }

    public bool Custom
    {
        get => _get() != null;
        set
        {
            _set(value ? _draft.Clone() : null);
            Notify();
            Changed();
        }
    }

    public int Type
    {
        get => (int)_draft.Type;
        set
        {
            _draft.Type = (TriggerEffectType)value;
            Notify();
            Changed();
        }
    }

    public double Start { get => _draft.Start; set { _draft.Start = (int)value; Notify(); Changed(); } }
    public double End { get => _draft.End; set { _draft.End = (int)value; Notify(); Changed(); } }
    public double Strength { get => _draft.Strength; set { _draft.Strength = (int)value; Notify(); Changed(); } }
    public double EndStrength { get => _draft.EndStrength; set { _draft.EndStrength = (int)value; Notify(); Changed(); } }
    public double Frequency { get => _draft.Frequency; set { _draft.Frequency = (int)value; Notify(); Changed(); } }

    public bool IsCaptured => _draft.Type == TriggerEffectType.Captured;
    public bool ShowParams => !IsCaptured;
    public bool ShowEnd => _draft.Type is TriggerEffectType.Slope or TriggerEffectType.Weapon or TriggerEffectType.Bow;
    public bool ShowEndStrength => _draft.Type is TriggerEffectType.Slope or TriggerEffectType.Bow;
    public bool ShowFrequency => _draft.Type == TriggerEffectType.Vibration;
    public string EndStrengthLabel => _draft.Type == TriggerEffectType.Bow ? "Snap-back force" : "End strength";
    public string Summary => _draft.Summary;

    public List<double> Bars => _draft.Curve().Select(v => 6.0 + v * 7.0).ToList();

    public void Load(TriggerEffect effect)
    {
        _draft = effect.Clone();
        if (!Custom) _set(_draft.Clone());
        Reload();
        Changed();
    }

    public TriggerEffect Draft => _draft.Clone();
}

sealed record SavedEffectItem(TriggerEffect Effect)
{
    public string Name => Effect.Name ?? Effect.Type.ToString();
    public string Detail => Effect.Summary;
}

sealed class TriggerLabViewModel : INotifyPropertyChanged
{
    readonly Config _cfg;
    readonly TriggerManager _triggers;
    readonly Func<GameProfile?> _selectedProfile;
    readonly Func<GameProfile?> _editProfile;
    readonly Func<string?> _selectedName;
    readonly Action _profileChanged;
    readonly Action _save;
    int _target;

    public event PropertyChangedEventHandler? PropertyChanged;
    public TriggerEditor Left { get; }
    public TriggerEditor Right { get; }
    public ObservableCollection<SavedEffectItem> Library { get; } = new();
    public ICommand SaveLeft { get; }
    public ICommand SaveRight { get; }
    public ICommand UseLeft { get; }
    public ICommand UseRight { get; }
    public ICommand Delete { get; }
    public ICommand UseCapturedLeft { get; }
    public ICommand UseCapturedRight { get; }

    public TriggerLabViewModel(Config cfg, TriggerManager triggers, Func<GameProfile?> selectedProfile, Func<GameProfile?> editProfile,
        Func<string?> selectedName, Action profileChanged, Action save)
    {
        _editProfile = editProfile;
        _selectedName = selectedName;
        _cfg = cfg;
        _triggers = triggers;
        _selectedProfile = selectedProfile;
        _profileChanged = profileChanged;
        _save = save;
        Left = new TriggerEditor(0, () => Get(0), e => Set(0, e), triggers);
        Right = new TriggerEditor(1, () => Get(1), e => Set(1, e), triggers);
        SaveLeft = new Relay(_ => SaveToLibrary(Left.Draft));
        SaveRight = new Relay(_ => SaveToLibrary(Right.Draft));
        UseLeft = new Relay(p => Left.Load(((SavedEffectItem)p!).Effect));
        UseRight = new Relay(p => Right.Load(((SavedEffectItem)p!).Effect));
        Delete = new Relay(p =>
        {
            _cfg.SavedEffects.Remove(((SavedEffectItem)p!).Effect);
            _save();
            RebuildLibrary();
        });
        UseCapturedLeft = new Relay(_ => UseCaptured(0, Left));
        UseCapturedRight = new Relay(_ => UseCaptured(1, Right));
        triggers.Captured += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Notify(nameof(CapturedText));
            Notify(nameof(HasCaptured));
        });
        RebuildLibrary();
    }

    void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    GameProfile? Profile => _target == 1 ? _selectedProfile() : null;

    TriggerEffect? Get(int side)
    {
        if (_target == 1) return Profile is { CustomTriggers: true } p ? (side == 0 ? p.TriggerL : p.TriggerR) : null;
        return side == 0 ? _cfg.TriggerL : _cfg.TriggerR;
    }

    void Set(int side, TriggerEffect? e)
    {
        if (_target == 1 && _editProfile() is { } p)
        {
            if (side == 0) p.TriggerL = e;
            else p.TriggerR = e;
            if (e != null) p.CustomTriggers = true;
            else if (p.TriggerL == null && p.TriggerR == null && p.CustomTriggers == true) p.CustomTriggers = null;
            _profileChanged();
        }
        else
        {
            if (side == 0) _cfg.TriggerL = e;
            else _cfg.TriggerR = e;
            _save();
        }
        _triggers.Apply();
        Notify(nameof(ProfileTriggerMode));
    }

    public int Target
    {
        get => _target;
        set
        {
            _target = value;
            Notify();
            Notify(nameof(EditingGame));
            Notify(nameof(ProfileTriggerMode));
            Left.Reload();
            Right.Reload();
        }
    }

    public bool HasGameTarget => _selectedName() != null;
    public string GameTargetName => _selectedName() ?? "No game selected";
    public bool EditingGame => _target == 1 && HasGameTarget;

    public int ProfileTriggerMode
    {
        get => _selectedProfile()?.CustomTriggers switch { true => 2, false => 1, _ => 0 };
        set
        {
            if (_editProfile() is not { } p) return;
            p.CustomTriggers = value switch { 2 => true, 1 => false, _ => null };
            _profileChanged();
            _triggers.Apply();
            Notify();
            Left.Reload();
            Right.Reload();
        }
    }

    public void SelectedGameChanged()
    {
        Notify(nameof(HasGameTarget));
        Notify(nameof(GameTargetName));
        if (_target == 1) Target = 1;
    }

    public int Kick
    {
        get => _cfg.TriggerKick;
        set
        {
            _cfg.TriggerKick = value;
            _save();
            Notify();
        }
    }

    public bool HasCaptured => _triggers.LastCaptured(0) != null || _triggers.LastCaptured(1) != null;

    public string CapturedText
    {
        get
        {
            string Side(int s) => _triggers.LastCaptured(s) is { } b
                ? new TriggerEffect { Type = TriggerEffectType.Captured, Raw = b }.Curve().Count(v => v > 0) + " active zones, mode 0x" + b[0].ToString("X2")
                : "nothing yet";
            return $"L2: {Side(0)}  ·  R2: {Side(1)}";
        }
    }

    void UseCaptured(int side, TriggerEditor editor)
    {
        if (_triggers.LastCaptured(side) is not { } raw) return;
        var e = new TriggerEffect { Type = TriggerEffectType.Captured, Raw = raw, Name = $"Captured {DateTime.Now:d MMM HH:mm}" };
        SaveToLibrary(e);
        editor.Load(e);
    }

    void SaveToLibrary(TriggerEffect e)
    {
        e.Name ??= $"{e.Type} {DateTime.Now:HH:mm:ss}";
        _cfg.SavedEffects.Add(e.Clone());
        _save();
        RebuildLibrary();
    }

    void RebuildLibrary()
    {
        Library.Clear();
        foreach (var e in _cfg.SavedEffects) Library.Add(new SavedEffectItem(e));
        Notify(nameof(HasLibrary));
    }

    public bool HasLibrary => Library.Count > 0;
}
