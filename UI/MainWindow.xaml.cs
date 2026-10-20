using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZerkSense5.UI;

partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int caption = 0x1B1512;
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) => LogScroll.ScrollToEnd();
        };
    }
}
