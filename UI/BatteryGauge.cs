using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ZerkSense5.UI;

sealed class BatteryGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int), typeof(BatteryGauge), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(
        nameof(Charging), typeof(bool), typeof(BatteryGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ConnectedProperty = DependencyProperty.Register(
        nameof(Connected), typeof(bool), typeof(BatteryGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Percent { get => (int)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public bool Connected { get => (bool)GetValue(ConnectedProperty); set => SetValue(ConnectedProperty, value); }

    public static Color LevelColor(int percent, bool charging) =>
        charging ? Color.FromRgb(0xFF, 0x94, 0x66)
        : percent <= 15 ? Color.FromRgb(0xF8, 0x71, 0x71)
        : percent <= 35 ? Color.FromRgb(0xFB, 0xBF, 0x24)
        : Color.FromRgb(0x34, 0xD3, 0x99);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 6) return;
        double capW = Math.Max(3, w * 0.05);
        var body = new Rect(1, 1, w - capW - 3, h - 2);
        double radius = h * 0.18;
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 2);
        dc.DrawRoundedRectangle(null, outline, body, radius, radius);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), null,
            new Rect(body.Right + 1.5, h * 0.32, capW, h * 0.36), 1.5, 1.5);

        if (!Connected) return;
        double inset = 4;
        double fullW = body.Width - inset * 2;
        double fillW = Math.Max(fullW * Math.Clamp(Percent, 0, 100) / 100.0, 3);
        var fill = new LinearGradientBrush(LevelColor(Percent, Charging),
            Color.Multiply(LevelColor(Percent, Charging), 0.8f), 90);
        dc.DrawRoundedRectangle(fill, null, new Rect(body.X + inset, body.Y + inset, fillW, body.Height - inset * 2),
            radius * 0.6, radius * 0.6);

        if (Charging)
        {
            var bolt = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                h * 0.55, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(bolt, new Point(body.X + body.Width / 2 - bolt.Width / 2, h / 2 - bolt.Height / 2));
        }
    }
}
