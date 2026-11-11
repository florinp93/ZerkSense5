using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZerkSense5.UI;

static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static void Enter(UIElement e, double delayMs = 0, double distance = 12, double durationMs = 260)
    {
        if (!Enabled)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            return;
        }
        var move = e.RenderTransform as TranslateTransform;
        if (move == null || move.IsFrozen)
        {
            move = new TranslateTransform();
            e.RenderTransform = move;
        }
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var dur = TimeSpan.FromMilliseconds(durationMs);
        if (delayMs <= 0)
        {
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, dur) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        }
        else
        {
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                FillBehavior = FillBehavior.Stop,
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(begin + dur), Ease),
                },
            });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingKeyFrames
            {
                FillBehavior = FillBehavior.Stop,
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(distance, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(distance, KeyTime.FromTimeSpan(begin)),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin + dur), Ease),
                },
            });
        }
    }

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
        "Reveal", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnReveal));

    public static bool GetReveal(DependencyObject d) => (bool)d.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject d, bool value) => d.SetValue(RevealProperty, value);

    static void OnReveal(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        el.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) Enter(el, distance: 8, durationMs: 220);
        };
    }

    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnStagger));

    public static bool GetStagger(DependencyObject d) => (bool)d.GetValue(StaggerProperty);
    public static void SetStagger(DependencyObject d, bool value) => d.SetValue(StaggerProperty, value);

    static void OnStagger(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel || e.NewValue is not true) return;
        void Run()
        {
            int i = 0;
            foreach (UIElement child in panel.Children)
            {
                if (child.Visibility != Visibility.Visible) continue;
                Enter(child, delayMs: Math.Min(i, 8) * 40, distance: 14);
                i++;
            }
        }
        panel.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) Run();
        };
        panel.Loaded += (_, _) =>
        {
            if (panel.IsVisible) Run();
        };
    }

    public static readonly DependencyProperty EnterOnLoadProperty = DependencyProperty.RegisterAttached(
        "EnterOnLoad", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterOnLoad));

    public static bool GetEnterOnLoad(DependencyObject d) => (bool)d.GetValue(EnterOnLoadProperty);
    public static void SetEnterOnLoad(DependencyObject d, bool value) => d.SetValue(EnterOnLoadProperty, value);

    static void OnEnterOnLoad(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && e.NewValue is true) fe.Loaded += (_, _) => Enter(fe, distance: 6, durationMs: 200);
    }

    sealed class SmoothState
    {
        public double Target;
        public bool Running;
    }

    static readonly DependencyProperty SmoothStateProperty = DependencyProperty.RegisterAttached(
        "SmoothState", typeof(SmoothState), typeof(Motion));

    public static void InstallScrolling()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (sv.TemplatedParent is TextBoxBase) return;
        // Only the innermost scroll viewer under the mouse decides; outer ones see the event first while tunnelling.
        if (!IsInnermost(sv, e.OriginalSource as DependencyObject)) return;

        var target = sv;
        while (target != null && !CanScroll(target, e.Delta))
            target = FindParent<ScrollViewer>(target);
        if (target == null)
        {
            e.Handled = true;
            return;
        }

        if (!Enabled || target.CanContentScroll && !PixelScrolling(target))
        {
            if (target == sv) return;
            e.Handled = true;
            target.ScrollToVerticalOffset(target.VerticalOffset - e.Delta);
            return;
        }

        e.Handled = true;
        var state = (SmoothState?)target.GetValue(SmoothStateProperty);
        if (state == null)
        {
            state = new SmoothState();
            target.SetValue(SmoothStateProperty, state);
        }
        if (!state.Running) state.Target = target.VerticalOffset;
        state.Target = Math.Clamp(state.Target - e.Delta * 0.9, 0, target.ScrollableHeight);
        if (state.Running) return;
        state.Running = true;
        var host = target;
        void Step(object? s, EventArgs a)
        {
            double now = host.VerticalOffset, diff = state.Target - now;
            if (Math.Abs(diff) < 0.5 || !host.IsVisible)
            {
                host.ScrollToVerticalOffset(state.Target);
                state.Running = false;
                CompositionTarget.Rendering -= Step;
                return;
            }
            host.ScrollToVerticalOffset(now + diff * 0.22);
        }
        CompositionTarget.Rendering += Step;
    }

    static bool PixelScrolling(ScrollViewer sv) =>
        sv.TemplatedParent is ItemsControl ic && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Pixel;

    static bool CanScroll(ScrollViewer sv, int delta) =>
        sv.ScrollableHeight > 0 && (delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight - 0.5);

    static bool IsInnermost(ScrollViewer sv, DependencyObject? source)
    {
        for (var d = source; d != null && d != sv; d = Parent(d))
            if (d is ScrollViewer inner && inner.TemplatedParent is not TextBoxBase) return false;
        return true;
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = Parent(d); p != null; p = Parent(p))
            if (p is T t) return t;
        return null;
    }

    static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
