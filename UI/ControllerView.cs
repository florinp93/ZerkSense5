using System.Windows;
using System.Windows.Media;

namespace ZerkSense5.UI;

sealed class ControllerView : FrameworkElement
{
    const double W = 1117, H = 892;

    public static readonly DependencyProperty InputProperty = DependencyProperty.Register(
        nameof(Input), typeof(Func<byte[]?>), typeof(ControllerView), new PropertyMetadata(null));
    public static readonly DependencyProperty LightbarProperty = DependencyProperty.Register(
        nameof(Lightbar), typeof(Brush), typeof(ControllerView), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ConnectedProperty = DependencyProperty.Register(
        nameof(Connected), typeof(bool), typeof(ControllerView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MicMutedProperty = DependencyProperty.Register(
        nameof(MicMuted), typeof(bool), typeof(ControllerView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Func<byte[]?>? Input { get => (Func<byte[]?>?)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Lightbar { get => (Brush)GetValue(LightbarProperty); set => SetValue(LightbarProperty, value); }
    public bool Connected { get => (bool)GetValue(ConnectedProperty); set => SetValue(ConnectedProperty, value); }
    public bool MicMuted { get => (bool)GetValue(MicMutedProperty); set => SetValue(MicMutedProperty, value); }

    static readonly Geometry GripLeft = G("M296.668,193.129c-5.479,-25.983 -25.743,-37.755 -62.282,-33.901c-40.742,4.297 -79.814,11.918 -115.424,22.309c-11.905,3.474 -23.487,8.273 -28.39,18.67c-91.01,192.963 -106.846,408.796 -55.913,643.084c2.947,13.555 9.046,24.221 21.05,32.104c5.843,3.836 12.587,7.06 21.886,9.445c21.562,5.529 31.041,-9.866 33.636,-23.758c33.416,-178.821 88.679,-342.348 191.663,-450.233c9.318,-9.761 18.181,-31.681 20.635,-51.733c0.969,-7.926 0.451,-15.333 -0.463,-22.036c-6.051,-44.414 -16.477,-96.901 -26.398,-143.951Z");
    static readonly Geometry GripRight = G("M820.718,193.129c5.479,-25.983 25.744,-37.755 62.283,-33.901c40.742,4.297 79.814,11.918 115.424,22.309c11.905,3.474 23.486,8.273 28.39,18.67c91.01,192.963 106.846,408.796 55.912,643.084c-2.947,13.555 -9.046,24.221 -21.05,32.104c-5.842,3.836 -12.586,7.06 -21.885,9.445c-21.563,5.529 -31.041,-9.866 -33.637,-23.758c-33.415,-178.821 -88.678,-342.348 -191.662,-450.233c-9.318,-9.761 -18.181,-31.681 -20.635,-51.733c-0.97,-7.926 -0.451,-15.333 0.462,-22.036c6.052,-44.414 16.477,-96.901 26.398,-143.951Z");
    static readonly Geometry Underside = G("M100.97,881.749c8.551,1.607 62.356,4.094 68.38,-3.153c33.93,-40.827 69.521,-154.237 85.416,-196.14c9.791,-25.813 35.4,-37.881 67.491,-40.687c30.597,13.569 45.149,13.982 96.708,3.594l280.228,-0c51.559,10.388 66.111,9.975 96.708,-3.594c32.091,2.806 57.701,14.874 67.492,40.687c15.894,41.903 51.486,155.313 85.416,196.14c6.023,7.247 59.828,4.76 68.38,3.153");
    static readonly Geometry ShoulderLeft = G("M282.527,168.559c13.841,1.794 27.682,-1.16 41.522,-6.511");
    static readonly Geometry ShoulderRight = G("M835.33,168.559c-13.84,1.794 -27.681,-1.16 -41.522,-6.511");
    static readonly Geometry Touchpad = G("M559.079,143.015c0,0 158.534,-0.805 226.555,15.497c12.437,2.981 21.237,14.507 19.467,24.644c-8.942,51.221 -20.354,109.033 -30.53,160.023c-8.029,40.224 -40.893,53.816 -68.431,53.692c-27.538,-0.124 -147.061,-0.559 -147.061,-0.559c0,0 -119.522,0.435 -147.06,0.559c-27.538,0.124 -60.403,-13.468 -68.431,-53.692c-10.177,-50.99 -21.589,-108.802 -30.531,-160.023c-1.77,-10.137 7.031,-21.663 19.467,-24.644c68.021,-16.302 226.555,-15.497 226.555,-15.497Z");
    static readonly Geometry L2 = G("M252.212,116.785l-114.214,0c-12.312,0 1.248,-110.916 78.919,-110.916c37.32,-0 51.81,110.916 35.295,110.916Z");
    static readonly Geometry R2 = G("M866.471,116.785l114.214,0c12.312,0 -1.248,-110.916 -78.919,-110.916c-37.32,-0 -51.81,110.916 -35.295,110.916Z");
    static readonly Geometry L1 = G("M122.514,180.513c0.736,-29.244 151.654,-71.59 148.099,-18.465c-9.518,-3.397 -21.562,-4.367 -36.227,-2.82c-39.392,4.154 -77.223,11.417 -111.872,21.285Z");
    static readonly Geometry R1 = G("M995.675,180.421c-0.736,-29.244 -151.654,-71.59 -148.099,-18.465c9.518,-3.397 21.562,-4.367 36.227,-2.82c39.392,4.154 77.223,11.417 111.872,21.285Z");
    static readonly Geometry Create = G("M281.064,206.321l6.369,31.916c-0,-0 2.557,11.875 -11.724,14.725c-14.281,2.849 -16.478,-9.098 -16.478,-9.098l-6.4,-32.075c-0,-0 -2.559,-11.876 11.739,-14.645c14.299,-2.77 16.494,9.177 16.494,9.177Z");
    static readonly Geometry Options = G("M839.082,206.321l-6.369,31.916c-0,-0 -2.557,11.875 11.724,14.725c14.281,2.849 16.478,-9.098 16.478,-9.098l6.4,-32.075c0,-0 2.559,-11.876 -11.74,-14.645c-14.298,-2.77 -16.493,9.177 -16.493,9.177Z");
    static readonly Geometry CreateIcon = G("M262.694,188.058l-4.528,-7.079 M269.717,185.341l-0,-8.725 M275.535,188.058l5.186,-7.079");
    static readonly Geometry OptionsIcon = G("M856.893,174.616l-13.987,0 M856.893,180.337l-13.987,0 M856.893,186.058l-13.987,-0");
    static readonly Geometry Mute = G("M590.061,591.266c-0,-3.699 -2.999,-6.698 -6.698,-6.698c-11.914,0 -36.653,0 -48.567,0c-3.7,0 -6.698,2.999 -6.698,6.698c-0,3.699 2.998,6.698 6.698,6.698c11.914,-0 36.653,-0 48.567,-0c3.699,-0 6.698,-2.999 6.698,-6.698Z");

    static readonly Geometry DpadUp = G("M213.487,271.996c0,-11.214 -9.091,-20.305 -20.305,-20.305l-28.888,-0c-11.215,-0 -20.306,9.091 -20.306,20.305l0,24.336c0,6.286 2.397,12.335 6.703,16.915c6.856,7.293 17.331,18.436 23.351,24.838c1.218,1.296 2.917,2.031 4.696,2.031c1.778,-0 3.478,-0.735 4.696,-2.031c6.019,-6.402 16.494,-17.545 23.35,-24.838c4.306,-4.58 6.703,-10.629 6.703,-16.915c0,-6.766 0,-16.013 0,-24.336Z");
    static readonly Geometry DpadDown = G("M213.487,443.093c0,11.215 -9.091,20.306 -20.305,20.306l-28.888,-0c-11.215,-0 -20.306,-9.091 -20.306,-20.306l0,-24.125c0,-6.412 2.494,-12.572 6.953,-17.178c6.884,-7.11 17.23,-17.796 23.166,-23.927c1.214,-1.254 2.885,-1.962 4.631,-1.962c1.745,0 3.416,0.708 4.63,1.962c5.936,6.131 16.282,16.817 23.166,23.927c4.46,4.606 6.953,10.766 6.953,17.178c0,6.746 0,15.888 0,24.125Z");
    static readonly Geometry DpadRight = G("M263.75,322.259c11.215,0 20.306,9.091 20.306,20.306l-0,28.888c-0,11.214 -9.091,20.305 -20.306,20.305l-24.349,0c-6.278,0 -12.32,-2.391 -16.899,-6.687c-7.081,-6.644 -17.749,-16.654 -23.907,-22.433c-1.274,-1.195 -2.008,-2.855 -2.035,-4.601c-0.027,-1.746 0.656,-3.428 1.892,-4.661c6.106,-6.094 16.81,-16.778 23.946,-23.901c4.63,-4.621 10.905,-7.216 17.446,-7.216c6.724,0 15.758,0 23.906,0Z");
    static readonly Geometry DpadLeft = G("M93.725,322.259c-11.214,0 -20.306,9.091 -20.306,20.306l0,28.888c0,11.214 9.092,20.305 20.306,20.305l24.349,0c6.278,0 12.321,-2.391 16.899,-6.687c7.081,-6.644 17.749,-16.654 23.908,-22.433c1.273,-1.195 2.007,-2.855 2.034,-4.601c0.027,-1.746 -0.656,-3.428 -1.891,-4.661c-6.106,-6.094 -16.81,-16.778 -23.947,-23.901c-4.63,-4.621 -10.904,-7.216 -17.446,-7.216c-6.724,0 -15.757,0 -23.906,0Z");
    static readonly Geometry ArrowUp = G("M168.337,270.016l18.658,0l-9.081,-9.08l-9.577,9.08Z");
    static readonly Geometry ArrowDown = G("M168.337,445.073l18.658,0l-9.081,9.08l-9.577,-9.08Z");
    static readonly Geometry ArrowRight = G("M265.73,367.409l0,-18.657l9.081,9.08l-9.081,9.577Z");
    static readonly Geometry ArrowLeft = G("M91.745,367.409l0,-18.657l-9.08,9.08l9.08,9.577Z");

    static readonly Geometry CrossMark = G("M917.682,411.683l32.795,32.795m-0,-32.795l-32.795,32.795");
    static readonly Geometry SquareMark = G("M847.313,341.648h32.865v32.865h-32.865Z");
    static readonly Geometry TriangleMark = G("M934.079,271.403l20.206,33.355l-40.411,-0l20.205,-33.355Z");
    static readonly Geometry CircleMark = new EllipseGeometry(new Point(1004.08, 358.08), 19.1, 19.1);

    static readonly Point Cross = new(934.079, 428.08), Square = new(864.079, 358.08), Circle = new(1004.08, 358.08), Triangle = new(934.079, 288.08);
    static readonly Point StickL = new(351.764, 528.548), StickR = new(763.456, 528.548), Ps = new(557, 532);
    const double FaceR = 34.957, StickWell = 87.347, StickCap = 57.193;

    static readonly Brush Shell = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x19, 0x17)));
    static readonly Brush Well = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x10, 0x0E)));
    static readonly Brush Lit = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x2E)));
    static readonly Brush LitSoft = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x5F, 0x2E)));
    static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x0D, 0x07)));
    static readonly Pen Line = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x80, 0x76)), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    static readonly Pen Dim = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x5E, 0x56, 0x4E)), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    static readonly Pen LitPen = Frozen(new Pen(Lit, 5) { LineJoin = PenLineJoin.Round });
    static readonly Pen InkPen = Frozen(new Pen(Ink, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    byte[]? _last;

    public ControllerView()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) CompositionTarget.Rendering += Poll;
            else CompositionTarget.Rendering -= Poll;
        };
    }

    static Geometry G(string path) => Frozen(Geometry.Parse(path));

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    void Poll(object? sender, EventArgs e)
    {
        var input = Input?.Invoke();
        if (ReferenceEquals(input, _last)) return;
        _last = input;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? W : available.Width;
        double h = double.IsInfinity(available.Height) ? w * H / W : available.Height;
        double k = Math.Min(w / W, h / H);
        return new Size(W * k, H * k);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double k = Math.Min(ActualWidth / W, ActualHeight / H);
        if (k <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - W * k) / 2, (ActualHeight - H * k) / 2));
        dc.PushTransform(new ScaleTransform(k, k));
        if (!Connected) dc.PushOpacity(0.35);

        var s = Connected ? _last : null;
        bool Btn(int i, int mask) => s != null && (s[i] & mask) != 0;
        int hat = s == null ? 8 : s[7] & 0x0F;

        DrawTrigger(dc, L2, s?[4] ?? 0);
        DrawTrigger(dc, R2, s?[5] ?? 0);
        dc.DrawGeometry(Shell, Line, GripLeft);
        dc.DrawGeometry(Shell, Line, GripRight);
        dc.DrawGeometry(null, Line, Underside);
        dc.DrawGeometry(null, Line, ShoulderLeft);
        dc.DrawGeometry(null, Line, ShoulderRight);
        Part(dc, L1, Btn(8, 0x01));
        Part(dc, R1, Btn(8, 0x02));

        var glow = Lightbar.Clone();
        glow.Opacity = 0.3;
        var glowPen = new Pen(glow, 22) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var barPen = new Pen(Lightbar, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var (a, b) in new[] { (new Point(300, 205), new Point(328, 350)), (new Point(818, 205), new Point(790, 350)) })
        {
            dc.DrawLine(glowPen, a, b);
            dc.DrawLine(barPen, a, b);
        }

        dc.DrawGeometry(Well, Btn(9, 0x02) ? LitPen : Line, Touchpad);
        if (s != null)
        {
            dc.PushClip(Touchpad);
            for (int t = 0; t < 2; t++)
            {
                int o = 32 + t * 4;
                if ((s[o] & 0x80) != 0) continue;
                int tx = s[o + 1] | (s[o + 2] & 0x0F) << 8, ty = s[o + 2] >> 4 | s[o + 3] << 4;
                var p = new Point(320 + 478 * Math.Clamp(tx / 1919.0, 0, 1), 148 + 248 * Math.Clamp(ty / 1079.0, 0, 1));
                dc.DrawEllipse(LitSoft, null, p, 30, 30);
                dc.DrawEllipse(Lit, null, p, 13, 13);
            }
            dc.Pop();
        }

        Part(dc, Create, Btn(8, 0x10));
        Part(dc, Options, Btn(8, 0x20));
        dc.DrawGeometry(null, Dim, CreateIcon);
        dc.DrawGeometry(null, Dim, OptionsIcon);

        Arrow(dc, DpadUp, ArrowUp, hat is 7 or 0 or 1);
        Arrow(dc, DpadRight, ArrowRight, hat is 1 or 2 or 3);
        Arrow(dc, DpadDown, ArrowDown, hat is 3 or 4 or 5);
        Arrow(dc, DpadLeft, ArrowLeft, hat is 5 or 6 or 7);

        Face(dc, Triangle, TriangleMark, Btn(7, 0x80));
        Face(dc, Circle, CircleMark, Btn(7, 0x40));
        Face(dc, Cross, CrossMark, Btn(7, 0x20));
        Face(dc, Square, SquareMark, Btn(7, 0x10));

        Stick(dc, StickL, s?[0] ?? 128, s?[1] ?? 128, Btn(8, 0x40));
        Stick(dc, StickR, s?[2] ?? 128, s?[3] ?? 128, Btn(8, 0x80));

        dc.DrawEllipse(Btn(9, 0x01) ? Lit : Well, Line, Ps, 24, 24);
        dc.DrawGeometry(MicMuted ? Lit : Btn(9, 0x04) ? LitSoft : Well, Line, Mute);

        if (!Connected) dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    static void Part(DrawingContext dc, Geometry g, bool on) => dc.DrawGeometry(on ? Lit : Well, on ? LitPen : Line, g);

    static void DrawTrigger(DrawingContext dc, Geometry g, byte value)
    {
        dc.DrawGeometry(Well, Line, g);
        if (value < 3) return;
        var b = g.Bounds;
        double h = b.Height * value / 255.0;
        dc.PushClip(new RectangleGeometry(new Rect(b.X - 4, b.Bottom - h, b.Width + 8, h + 4)));
        dc.DrawGeometry(Lit, LitPen, g);
        dc.Pop();
    }

    static void Arrow(DrawingContext dc, Geometry pad, Geometry arrow, bool on)
    {
        Part(dc, pad, on);
        dc.DrawGeometry(on ? Ink : Dim.Brush, null, arrow);
    }

    static void Face(DrawingContext dc, Point c, Geometry mark, bool on)
    {
        dc.DrawEllipse(on ? Lit : Well, on ? LitPen : Line, c, FaceR, FaceR);
        dc.DrawGeometry(null, on ? InkPen : Dim, mark);
    }

    static void Stick(DrawingContext dc, Point c, byte x, byte y, bool pressed)
    {
        dc.DrawEllipse(Well, Line, c, StickWell, StickWell);
        var cap = new Point(c.X + (x - 128) / 128.0 * 26, c.Y + (y - 128) / 128.0 * 26);
        bool moved = Math.Abs(x - 128) > 20 || Math.Abs(y - 128) > 20;
        dc.DrawEllipse(pressed ? Lit : Shell, pressed ? LitPen : moved ? LitPen : Line, cap, StickCap, StickCap);
        dc.DrawEllipse(null, Dim, cap, StickCap * 0.62, StickCap * 0.62);
    }
}
