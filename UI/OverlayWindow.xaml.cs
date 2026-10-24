using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZerkSense5.Games;

namespace ZerkSense5.UI;

partial class OverlayWindow : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(nint h, int i, int v);

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;

    readonly DispatcherTimer _hide = new();

    public OverlayWindow()
    {
        Resources["OverlayVisible"] = new BoolToVisibility();
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Fade(0, 450, () => Hide());
        };
    }

    internal void ShowFor(OverlayModel model, (int left, int top, int right, int bottom)? monitor, int seconds)
    {
        DataContext = model;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        double right, top;
        if (monitor is { } m)
        {
            right = m.right / dpi.DpiScaleX;
            top = m.top / dpi.DpiScaleY;
        }
        else
        {
            right = SystemParameters.PrimaryScreenWidth;
            top = 0;
        }
        Left = right - ActualWidth - 8;
        Top = top + 40;
        Topmost = false;
        Topmost = true;
        Fade(1, 220, null);
        _hide.Stop();
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Start();
    }

    void Fade(double to, int ms, Action? done)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() };
        if (done != null) a.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, a);
    }
}

sealed record FeatureRow(string Name, Support Support, string? StateOverride = null)
{
    public string Glyph => Support switch
    {
        Support.Yes => "",
        Support.No => "",
        Support.Limited or Support.Modded => "",
        _ => "",
    };

    public Brush Brush => new SolidColorBrush(Support switch
    {
        Support.Yes => Color.FromRgb(0x34, 0xD3, 0x99),
        Support.No => Color.FromRgb(0x5A, 0x62, 0x73),
        Support.Limited or Support.Modded => Color.FromRgb(0xFB, 0xBF, 0x24),
        _ => Color.FromRgb(0x3A, 0x41, 0x50),
    });

    public string State => StateOverride ?? Support switch
    {
        Support.Yes => "Supported",
        Support.No => "Not supported",
        Support.Limited => "Limited",
        Support.Modded => "With mods",
        Support.NotApplicable => "N/A",
        _ => "Unknown",
    };
}

sealed class OverlayModel
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool HasMode { get; init; }
    public string ModeText { get; init; } = "";
    public string ModeIcon { get; init; } = "";
    public Brush ModeBrush { get; init; } = Brushes.Transparent;
    public string ModeNote { get; init; } = "";
    public bool HasModeNote => ModeNote.Length > 0;
    public List<FeatureRow> Features { get; init; } = new();
    public string Callout { get; init; } = "";
    public bool HasCallout => Callout.Length > 0;
    public string Source { get; init; } = "";
    public bool HasBattery { get; init; }
    public int BatteryPercent { get; init; }
    public bool Charging { get; init; }
    public string BatteryText => $"{BatteryPercent}%";

    public static OverlayModel Build(GameSession? s, ControllerStatus? c, Config cfg)
    {
        bool battery = c != null && c.BatteryPercent > 0;
        if (s == null)
        {
            return new OverlayModel
            {
                Title = c == null ? "No controller" : c.IsEdge ? "DualSense Edge" : "DualSense",
                Subtitle = c == null ? "Turn on your DualSense to connect." : "No game detected.",
                HasBattery = battery,
                BatteryPercent = c?.BatteryPercent ?? 0,
                Charging = c?.Power == PowerState.Charging,
                Source = "Zerk Sense 5",
            };
        }

        var p = s.Pcgw;
        var rows = new List<FeatureRow>();
        bool xbox = s.EffectiveMode == ControllerMode.Xbox360;
        if (xbox)
        {
            if (p != null) rows.Add(new FeatureRow("Xbox controllers", p.XInput));
            rows.Add(new FeatureRow(cfg.RumbleStyle == 0 ? "Rumble as HD haptics" : "Rumble (classic)", Support.Yes, "Zerk Sense 5"));
            rows.Add(new FeatureRow("Player LEDs", Support.Yes, "Zerk Sense 5"));
            rows.Add(new FeatureRow("Adaptive triggers", Support.No, "Not in Xbox mode"));
        }
        else if (p != null)
        {
            rows.Add(new FeatureRow("HD haptics", p.Haptics));
            rows.Add(new FeatureRow("Adaptive triggers", p.AdaptiveTriggers));
            rows.Add(new FeatureRow("Lightbar", p.LightBar));
            rows.Add(new FeatureRow("Controller speaker", p.Speaker));
            rows.Add(new FeatureRow("Motion sensors", p.Motion));
            rows.Add(new FeatureRow("PlayStation prompts", p.PlayStationPrompts));
        }

        string modeText = (s.Profile.Mode == ControllerMode.Auto ? "Recommended: " : "Profile: ") + (xbox ? "Xbox 360 mode" : "DualSense mode");
        return new OverlayModel
        {
            Title = s.Game.Name,
            Subtitle = s.LookingUp ? "Looking up PCGamingWiki…" : s.Verdict.Summary,
            HasMode = true,
            ModeText = modeText,
            ModeIcon = xbox ? "" : "",
            ModeBrush = new SolidColorBrush(xbox ? Color.FromRgb(0x10, 0x7C, 0x10) : Color.FromRgb(0x25, 0x63, 0xEB)),
            Features = rows,
            Callout = p?.WiredOnly == true && p.SupportsDualSense ? "✓  Wired-only features unlocked over Bluetooth" : "",
            Source = p != null ? "Source: PCGamingWiki" : s.Game.Source,
            HasBattery = battery,
            BatteryPercent = c?.BatteryPercent ?? 0,
            Charging = c?.Power == PowerState.Charging,
        };
    }
}
