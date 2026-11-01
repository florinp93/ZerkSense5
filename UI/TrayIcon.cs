using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ZerkSense5.UI;

sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    readonly Forms.NotifyIcon _icon;
    readonly BridgeService _service;
    Icon? _current;
    string _drawnKey = "";
    bool _lowWarned;
    bool _wasConnected;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon(BridgeService service)
    {
        _service = service;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Zerk Sense 5", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
        Update();
    }

    public void Update()
    {
        var c = _service.Controller;
        string text = c == null
            ? "Zerk Sense 5: no controller"
            : $"Zerk Sense 5: {(c.IsEdge ? "DualSense Edge" : "DualSense")} {c.BatteryPercent}%" +
              (c.Power == PowerState.Charging ? " (charging)" : c.Power == PowerState.Full ? " (full)" : "");
        _icon.Text = text.Length > 63 ? text[..63] : text;

        string key = c == null ? "off" : $"{c.BatteryPercent}-{c.Power}";
        if (key != _drawnKey)
        {
            _drawnKey = key;
            var old = _current;
            _current = Draw(c);
            _icon.Icon = _current;
            if (old != null)
            {
                DestroyIcon(old.Handle);
                old.Dispose();
            }
        }

        if (c != null && !_wasConnected)
            _icon.ShowBalloonTip(3000, "Controller connected", $"{(c.IsEdge ? "DualSense Edge" : "DualSense")} is ready.", Forms.ToolTipIcon.None);
        if (c == null && _wasConnected)
            _icon.ShowBalloonTip(3000, "Controller disconnected", "Press PS to reconnect.", Forms.ToolTipIcon.None);
        _wasConnected = c != null;

        if (c is { Power: PowerState.Discharging, BatteryPercent: > 0 and <= 10 } && !_lowWarned && _service.Config.LowBatteryAlert)
        {
            _lowWarned = true;
            _icon.ShowBalloonTip(5000, "Controller battery low", $"{c.BatteryPercent}% left. Plug in your DualSense.", Forms.ToolTipIcon.Warning);
        }
        if (c == null || c.Power != PowerState.Discharging || c.BatteryPercent > 20) _lowWarned = false;
    }

    public void ShowInfo(string title, string text) => _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    static Icon Draw(ControllerStatus? c)
    {
        int size = Math.Max(32, Forms.SystemInformation.SmallIconSize.Width * 2);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            var body = new RectangleF(2 * s, 8 * s, 25 * s, 16 * s);
            using var outline = new Pen(Color.FromArgb(c == null ? 140 : 235, 255, 255, 255), 2.4f * s);
            using (var path = Rounded(body, 3.5f * s)) g.DrawPath(outline, path);
            using (var cap = new SolidBrush(Color.FromArgb(c == null ? 140 : 235, 255, 255, 255)))
                g.FillRectangle(cap, 27.5f * s, 13 * s, 3 * s, 6 * s);

            if (c == null)
            {
                using var slash = new Pen(Color.FromArgb(200, 255, 255, 255), 2.4f * s);
                g.DrawLine(slash, 8 * s, 19 * s, 21 * s, 13 * s);
            }
            else
            {
                var m = BatteryGauge.LevelColor(c.BatteryPercent, c.Power == PowerState.Charging);
                using var fill = new SolidBrush(Color.FromArgb(m.R, m.G, m.B));
                float inner = 21 * s * Math.Clamp(c.BatteryPercent, 5, 100) / 100f;
                using (var path = Rounded(new RectangleF(4.5f * s, 10.5f * s, inner, 11 * s), 1.8f * s)) g.FillPath(fill, path);
                if (c.Power == PowerState.Charging)
                {
                    PointF[] bolt =
                    [
                        new(16 * s, 6 * s), new(10 * s, 17 * s), new(15 * s, 17 * s),
                        new(13 * s, 26 * s), new(21 * s, 14 * s), new(16 * s, 14 * s), new(18 * s, 6 * s),
                    ];
                    using var b = new SolidBrush(Color.White);
                    using var edge = new Pen(Color.FromArgb(200, 20, 24, 32), 1.2f * s);
                    g.FillPolygon(b, bolt);
                    g.DrawPolygon(edge, bolt);
                }
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_current != null) DestroyIcon(_current.Handle);
    }
}
