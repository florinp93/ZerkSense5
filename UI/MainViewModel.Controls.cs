using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace ZerkSense5.UI;

partial class MainViewModel
{
    static readonly PadButton[] TurnOffChord = [PadButton.L1, PadButton.R1, PadButton.L2, PadButton.R2, PadButton.Create, PadButton.Options];

    public ObservableCollection<BindingRow> BindingRows { get; } = new();
    public ObservableCollection<ButtonChip> EditButtons { get; } = new();
    public IReadOnlyList<ActionChoice> ActionChoices { get; } =
    [
        new(BindingAction.Key, "Keyboard key"),
        new(BindingAction.MouseLeft, "Left click"),
        new(BindingAction.MouseRight, "Right click"),
        new(BindingAction.MouseMiddle, "Middle click"),
        new(BindingAction.MouseBack, "Mouse back"),
        new(BindingAction.MouseForward, "Mouse forward"),
        new(BindingAction.WheelUp, "Scroll up"),
        new(BindingAction.WheelDown, "Scroll down"),
        new(BindingAction.ToggleTouchpadMouse, "Touchpad mouse on/off"),
        new(BindingAction.TurnOffController, "Turn off controller"),
        new(BindingAction.Nothing, "Disable button"),
    ];

    ButtonBinding? _editing;
    ButtonBinding? _editingOriginal;
    DispatcherTimer? _recordTimer;
    uint _recorded;
    long _recordStarted;
    bool _capturingKey;

    void InitControls()
    {
        foreach (var b in Pad.All) EditButtons.Add(new ButtonChip(b));
        foreach (var c in EditButtons) c.PropertyChanged += (_, _) => EditButtonsChanged();
        RebuildBindings();
    }

    void RebuildBindings()
    {
        BindingRows.Clear();
        foreach (var b in _cfg.Bindings) BindingRows.Add(new BindingRow(b));
        Notify(nameof(HasBindings));
        Notify(nameof(NoBindings));
    }

    void SetBindings(List<ButtonBinding> list)
    {
        // The mapper reads the list from the Bluetooth thread, so it is replaced, never changed in place.
        _cfg.Bindings = list;
        _cfg.Save();
        RebuildBindings();
    }

    public bool HasBindings => _cfg.Bindings.Count > 0;
    public bool NoBindings => _cfg.Bindings.Count == 0;

    public bool TouchpadMouse
    {
        get => _cfg.TouchpadMouse;
        set { _cfg.TouchpadMouse = value; Saved(); Notify(nameof(TouchpadMouseState)); Notify(nameof(TouchpadMouseActive)); }
    }

    public double TouchpadSpeed
    {
        get => _cfg.TouchpadSpeed;
        set { _cfg.TouchpadSpeed = (int)value; Saved(); }
    }

    public bool TouchpadTapToClick
    {
        get => _cfg.TouchpadTapToClick;
        set { _cfg.TouchpadTapToClick = value; Saved(); }
    }

    public bool TouchpadRightClickZone
    {
        get => _cfg.TouchpadRightClickZone;
        set { _cfg.TouchpadRightClickZone = value; Saved(); }
    }

    public bool TouchpadMouseActive => _service.Input.TouchpadMouseOn;

    public string TouchpadMouseState
    {
        get
        {
            bool on = _service.Input.TouchpadMouseOn;
            if (on != _cfg.Now.TouchpadMouse) return on ? "On right now (turned on with a button)" : "Off right now (turned off with a button)";
            if (_cfg.Now.Profile?.TouchpadMouse is bool p && p != _cfg.TouchpadMouse)
                return p ? $"On for {_cfg.Now.Profile.Name} (game setting)" : $"Off for {_cfg.Now.Profile.Name} (game setting)";
            return on ? "On: games don't see the touchpad" : "Off: games get the touchpad";
        }
    }

    public bool IsEditingBinding => _editing != null;
    public string EditTitle => _editingOriginal == null ? "New binding" : "Edit binding";
    public string EditButtonsText => _editing?.ButtonsText ?? "";

    public ActionChoice? EditAction
    {
        get => _editing == null ? null : ActionChoices.FirstOrDefault(a => a.Action == _editing.Action);
        set
        {
            if (_editing == null || value == null) return;
            _editing.Action = value.Action;
            NotifyEditor();
        }
    }

    public bool EditIsKey => _editing?.Action == BindingAction.Key;
    public string EditKeyText => _capturingKey ? "Press a key…" : _editing is { Key: not 0 } e ? e.KeyText : "Pick a key";
    public bool EditPassThrough
    {
        get => _editing?.PassThrough ?? false;
        set { if (_editing != null) _editing.PassThrough = value; Notify(); }
    }
    public bool CapturingKey => _capturingKey;
    public bool Recording => _recordTimer != null;
    public string RecordText => Recording ? "Press the buttons now…" : "Record from controller";
    public bool CanSaveBinding => _editing is { Buttons.Count: > 0 } e && (e.Action != BindingAction.Key || e.Key != 0);
    public string EditHint => _editing?.Action switch
    {
        BindingAction.Key => "The key is held for as long as the buttons are, so it works for push-to-talk.",
        BindingAction.TurnOffController => "Drops the Bluetooth link, which turns the DualSense off. Press PS to turn it back on.",
        BindingAction.ToggleTouchpadMouse => "Switches the touchpad between mouse and controller touchpad.",
        BindingAction.Nothing => "The game never sees these buttons.",
        _ => "",
    };

    void NotifyEditor()
    {
        foreach (var p in new[]
                 {
                     nameof(IsEditingBinding), nameof(EditTitle), nameof(EditButtonsText), nameof(EditAction), nameof(EditIsKey),
                     nameof(EditKeyText), nameof(EditPassThrough), nameof(CapturingKey), nameof(Recording), nameof(RecordText),
                     nameof(CanSaveBinding), nameof(EditHint),
                 })
            Notify(p);
    }

    bool _syncingChips;

    void LoadChips()
    {
        _syncingChips = true;
        foreach (var c in EditButtons) c.IsChecked = _editing?.Buttons.Contains(c.Button) == true;
        _syncingChips = false;
    }

    void EditButtonsChanged()
    {
        if (_syncingChips || _editing == null) return;
        _editing.Buttons = EditButtons.Where(c => c.IsChecked).Select(c => c.Button).ToList();
        NotifyEditor();
    }

    void BeginEdit(ButtonBinding? original, ButtonBinding draft)
    {
        StopRecording();
        _capturingKey = false;
        _editingOriginal = original;
        _editing = draft;
        LoadChips();
        NotifyEditor();
    }

    public ICommand AddBinding => _addBinding ??= new Relay(_ =>
        BeginEdit(null, new ButtonBinding { Action = BindingAction.Key }));
    ICommand? _addBinding;

    public ICommand AddPsToKey => _addPsToKey ??= new Relay(_ =>
    {
        BeginEdit(null, new ButtonBinding { Buttons = [PadButton.PS], Action = BindingAction.Key });
        _capturingKey = true;
        NotifyEditor();
    });
    ICommand? _addPsToKey;

    public ICommand AddTurnOff => _addTurnOff ??= new Relay(_ =>
        BeginEdit(null, new ButtonBinding { Buttons = [.. TurnOffChord], Action = BindingAction.TurnOffController, PassThrough = true }));
    ICommand? _addTurnOff;

    public ICommand EditBinding => _editBinding ??= new Relay(p =>
    {
        if (p is BindingRow row) BeginEdit(row.Binding, row.Binding.Clone());
    });
    ICommand? _editBinding;

    public ICommand DeleteBinding => _deleteBinding ??= new Relay(p =>
    {
        if (p is not BindingRow row) return;
        SetBindings(_cfg.Bindings.Where(b => b != row.Binding).ToList());
        if (_editingOriginal == row.Binding) CancelEdit.Execute(null);
    });
    ICommand? _deleteBinding;

    public ICommand SaveBinding => _saveBinding ??= new Relay(_ =>
    {
        if (_editing == null || !CanSaveBinding) return;
        var list = new List<ButtonBinding>(_cfg.Bindings);
        int i = _editingOriginal == null ? -1 : list.IndexOf(_editingOriginal);
        if (i >= 0) list[i] = _editing;
        else list.Add(_editing);
        Log.Info($"Binding saved: {_editing.ButtonsText} → {_editing.ActionText}");
        _editing = _editingOriginal = null;
        StopRecording();
        _capturingKey = false;
        SetBindings(list);
        NotifyEditor();
    });
    ICommand? _saveBinding;

    public ICommand CancelEdit => _cancelEdit ??= new Relay(_ =>
    {
        _editing = _editingOriginal = null;
        StopRecording();
        _capturingKey = false;
        NotifyEditor();
    });
    ICommand? _cancelEdit;

    public ICommand CaptureKey => _captureKey ??= new Relay(_ =>
    {
        _capturingKey = !_capturingKey;
        NotifyEditor();
    });
    ICommand? _captureKey;

    public bool OnKeyCaptured(Key key, ModifierKeys mods)
    {
        if (!_capturingKey || _editing == null) return false;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return true;
        bool modifierKey = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt;
        _editing.Key = vk;
        _editing.Ctrl = !modifierKey && mods.HasFlag(ModifierKeys.Control);
        _editing.Shift = !modifierKey && mods.HasFlag(ModifierKeys.Shift);
        _editing.Alt = !modifierKey && mods.HasFlag(ModifierKeys.Alt);
        _capturingKey = false;
        NotifyEditor();
        return true;
    }

    public ICommand RecordButtons => _recordButtons ??= new Relay(_ =>
    {
        if (Recording)
        {
            StopRecording();
            NotifyEditor();
            return;
        }
        _recorded = 0;
        _recordStarted = Environment.TickCount64;
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _recordTimer.Tick += (_, _) => RecordTick();
        _recordTimer.Start();
        NotifyEditor();
    });
    ICommand? _recordButtons;

    void RecordTick()
    {
        if (_editing == null || Environment.TickCount64 - _recordStarted > 10000)
        {
            StopRecording();
            NotifyEditor();
            return;
        }
        if (_service.LastInput is not { Length: >= 63 } input) return;
        uint held = Pad.Read(input);
        _recorded |= held;
        if (_recorded != 0 && held == 0)
        {
            _editing.Buttons = Pad.All.Where(b => (_recorded & 1u << (int)b) != 0).ToList();
            StopRecording();
            LoadChips();
            NotifyEditor();
        }
    }

    void StopRecording()
    {
        _recordTimer?.Stop();
        _recordTimer = null;
    }

    public int ProfileTouchpadMouse
    {
        get => SelectedProfile?.TouchpadMouse switch { true => 1, false => 2, _ => 0 };
        set
        {
            if (EditProfile is not { } profile) return;
            profile.TouchpadMouse = value switch { 1 => true, 2 => false, _ => null };
            ProfileChanged();
            Notify(nameof(TouchpadMouseState));
        }
    }

    public int ProfileBindings
    {
        get => SelectedProfile?.Bindings == false ? 1 : 0;
        set
        {
            if (EditProfile is not { } profile) return;
            profile.Bindings = value == 1 ? false : null;
            ProfileChanged();
        }
    }

    public bool GameInControl => _service.GameTriggers || _service.GameLightbar || _service.GameHaptics;

    public string GameControlText
    {
        get
        {
            var parts = new List<string>();
            if (_service.GameTriggers) parts.Add("triggers");
            if (_service.GameLightbar) parts.Add("lightbar");
            if (_service.GameHaptics) parts.Add("haptics");
            return parts.Count == 0 ? "" : "Game controls " + string.Join(", ", parts);
        }
    }

    public string LightingGameNote => _service.GameLightbar
        ? _cfg.Now.Profile?.LightbarColor != null
            ? "The game sets the lightbar, but this game's profile colour is kept."
            : "Paused: the running game sets the lightbar itself. Effects come back when it closes."
        : "";
    public bool HasLightingGameNote => _service.GameLightbar;
}

sealed class BindingRow(ButtonBinding binding)
{
    public ButtonBinding Binding { get; } = binding;
    public string Buttons => Binding.ButtonsText;
    public string Action => Binding.ActionText;
    public string Detail => Binding.PassThrough ? "Game still gets the buttons" : "Hidden from the game";
}

sealed record ActionChoice(BindingAction Action, string Label);

sealed class ButtonChip(PadButton button) : INotifyPropertyChanged
{
    bool _checked;

    public PadButton Button { get; } = button;
    public string Name { get; } = Pad.Name(button);

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Notify();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
