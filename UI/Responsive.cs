using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ZerkSense5.UI;

static class Responsive
{

    public static readonly DependencyProperty AutoStackProperty = DependencyProperty.RegisterAttached(
        "AutoStack", typeof(bool), typeof(Responsive), new PropertyMetadata(false, OnAutoStack));

    public static bool GetAutoStack(DependencyObject d) => (bool)d.GetValue(AutoStackProperty);
    public static void SetAutoStack(DependencyObject d, bool value) => d.SetValue(AutoStackProperty, value);

    const double MinLabelWidth = 230;

    sealed class RowState
    {
        public bool Stacked;
        public GridLength[] Columns = [];
        public readonly Dictionary<UIElement, (int col, int span, Thickness margin)> Children = new();
        public readonly Dictionary<UIElement, double> NaturalWidth = new();
    }

    static readonly DependencyProperty RowStateProperty = DependencyProperty.RegisterAttached(
        "RowState", typeof(RowState), typeof(Responsive));

    static void OnAutoStack(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid || e.NewValue is not true) return;
        grid.SizeChanged += (_, _) => UpdateRow(grid);
        grid.Loaded += (_, _) => grid.Dispatcher.BeginInvoke(() => UpdateRow(grid), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    static void UpdateRow(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 2 || grid.ActualWidth <= 0) return;
        var state = (RowState?)grid.GetValue(RowStateProperty);
        if (state == null)
        {
            if (grid.RowDefinitions.Count > 0) return;
            state = new RowState { Columns = grid.ColumnDefinitions.Select(c => c.Width).ToArray() };
            foreach (UIElement child in grid.Children)
                state.Children[child] = (Grid.GetColumn(child), Grid.GetColumnSpan(child), child is FrameworkElement fe ? fe.Margin : default);
            grid.SetValue(RowStateProperty, state);
        }

        if (!state.Stacked)
            foreach (var (child, (col, _, _)) in state.Children)
                if (col >= 1 && child.Visibility != Visibility.Collapsed)
                    state.NaturalWidth[child] = Math.Max(child.DesiredSize.Width, state.NaturalWidth.GetValueOrDefault(child));

        double controls = 0;
        for (int c = 1; c < state.Columns.Length; c++)
        {
            var w = state.Columns[c];
            double colWidth = w.IsAbsolute ? w.Value : 0;
            foreach (var (child, (col, _, _)) in state.Children)
                if (col == c && child.Visibility != Visibility.Collapsed)
                    colWidth = Math.Max(colWidth, w.IsAbsolute ? w.Value + Horizontal(state.Children[child].margin) : state.NaturalWidth.GetValueOrDefault(child));
            controls += colWidth;
        }

        bool stack = grid.ActualWidth - controls < MinLabelWidth;
        if (stack == state.Stacked) return;
        state.Stacked = stack;
        Apply(grid, state);
    }

    static double Horizontal(Thickness t) => t.Left + t.Right;

    static void Apply(Grid grid, RowState state)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (!state.Stacked)
        {
            foreach (var w in state.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            foreach (var (child, (col, span, margin)) in state.Children)
            {
                Grid.SetRow(child, 0);
                Grid.SetColumn(child, col);
                Grid.SetColumnSpan(child, span);
                if (child is FrameworkElement fe) fe.Margin = margin;
            }
            return;
        }

        var moved = state.Columns.Skip(1).Select(w => w.IsAbsolute ? new GridLength(1, GridUnitType.Star) : w).ToList();
        if (!moved.Any(w => w.IsStar)) moved.Add(new GridLength(1, GridUnitType.Star));
        foreach (var w in moved) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool first = true;
        foreach (var (child, (col, _, margin)) in state.Children.OrderBy(kv => kv.Value.col))
        {
            if (col == 0)
            {
                Grid.SetRow(child, 0);
                Grid.SetColumn(child, 0);
                Grid.SetColumnSpan(child, moved.Count);
                continue;
            }
            Grid.SetRow(child, 1);
            Grid.SetColumn(child, col - 1);
            Grid.SetColumnSpan(child, 1);
            if (child is FrameworkElement fe)
            {
                fe.Margin = new Thickness(first ? 0 : margin.Left, 10, margin.Right, 0);
                if (fe.HorizontalAlignment == HorizontalAlignment.Right && fe is not TextBlock) fe.HorizontalAlignment = HorizontalAlignment.Left;
            }
            if (child.Visibility != Visibility.Collapsed) first = false;
        }
    }

    public static readonly DependencyProperty ItemMinWidthProperty = DependencyProperty.RegisterAttached(
        "ItemMinWidth", typeof(double), typeof(Responsive), new PropertyMetadata(0.0, OnItemMinWidth));

    public static double GetItemMinWidth(DependencyObject d) => (double)d.GetValue(ItemMinWidthProperty);
    public static void SetItemMinWidth(DependencyObject d, double value) => d.SetValue(ItemMinWidthProperty, value);

    static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.RegisterAttached(
        "MaxColumns", typeof(int), typeof(Responsive), new PropertyMetadata(0));

    static void OnItemMinWidth(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged += (_, _) => UpdateColumns(fe);
        fe.Loaded += (_, _) => UpdateColumns(fe);
    }

    static void UpdateColumns(FrameworkElement fe)
    {
        var grid = fe as UniformGrid ?? FindPanel<UniformGrid>(fe);
        if (grid == null || fe.ActualWidth <= 0) return;
        int max = (int)fe.GetValue(MaxColumnsProperty);
        if (max == 0)
        {
            max = Math.Max(grid.Columns, 1);
            fe.SetValue(MaxColumnsProperty, max);
        }
        int cols = Math.Clamp((int)(fe.ActualWidth / GetItemMinWidth(fe)), 1, max);
        int items = grid.Children.Count;
        while (cols > 1 && items > 0 && items % cols != 0) cols--;
        if (grid.Columns != cols) grid.Columns = cols;
    }

    static T? FindPanel<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindPanel<T>(child) is { } found) return found;
        }
        return null;
    }

    public static readonly DependencyProperty HideBelowProperty = DependencyProperty.RegisterAttached(
        "HideBelow", typeof(double), typeof(Responsive), new PropertyMetadata(0.0, OnHideBelow));

    public static double GetHideBelow(DependencyObject d) => (double)d.GetValue(HideBelowProperty);
    public static void SetHideBelow(DependencyObject d, double value) => d.SetValue(HideBelowProperty, value);

    static void OnHideBelow(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        void Update()
        {
            if (Window.GetWindow(fe) is not { } w) return;
            fe.Visibility = w.ActualWidth < GetHideBelow(fe) ? Visibility.Collapsed : Visibility.Visible;
        }
        fe.Loaded += (_, _) =>
        {
            if (Window.GetWindow(fe) is { } w) w.SizeChanged += (_, _) => Update();
            Update();
        };
    }
}
