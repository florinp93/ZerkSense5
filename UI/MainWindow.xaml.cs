using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZerkSense5.UI;

partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) => LogScroll.ScrollToEnd();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (DataContext is not MainViewModel { CapturingKey: true } vm) return;
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                _ => e.Key,
            };
            e.Handled = vm.OnKeyCaptured(key, Keyboard.Modifiers);
        };

        RadioButton[] tabs = [NavOverview, NavGames, NavFeedback, NavControls, NavLog, NavSettings];
        foreach (var tab in tabs) tab.Checked += (_, _) => MoveIndicator(animate: true);
        TopBar.SizeChanged += (_, _) => MoveIndicator(animate: false);
        Loaded += (_, _) =>
        {
            MoveIndicator(animate: false);
            if (Content is UIElement root) Motion.Enter(root, distance: 0, durationMs: 320);
        };
    }

    void MoveIndicator(bool animate)
    {
        RadioButton[] tabs = [NavOverview, NavGames, NavFeedback, NavControls, NavLog, NavSettings];
        var tab = tabs.FirstOrDefault(t => t.IsChecked == true);
        if (tab == null || !tab.IsVisible || TopBar.ActualWidth == 0) return;
        double x = tab.TransformToAncestor(TopBar).Transform(new Point(0, 0)).X, w = tab.ActualWidth;
        if (!animate || !Motion.Enabled)
        {
            NavIndicatorMove.BeginAnimation(TranslateTransform.XProperty, null);
            NavIndicator.BeginAnimation(WidthProperty, null);
            NavIndicatorMove.X = x;
            NavIndicator.Width = w;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(280);
        NavIndicatorMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
        NavIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
    }

    public static void DarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int caption = 0x0E1012;
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }
}
