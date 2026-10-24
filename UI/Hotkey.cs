using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ZerkSense5.UI;

sealed class Hotkey : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hwnd, int id);

    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, NoRepeat = 0x4000;
    const int WM_HOTKEY = 0x0312;
    const int Id = 0x5A53;

    readonly HwndSource _source;
    readonly Action _pressed;

    public bool Registered { get; }

    public Hotkey(uint modifiers, uint key, Action pressed)
    {
        _pressed = pressed;
        _source = new HwndSource(new HwndSourceParameters("ZerkSense5.Hotkey") { ParentWindow = new nint(-3) });
        _source.AddHook(Hook);
        Registered = RegisterHotKey(_source.Handle, Id, modifiers | NoRepeat, key);
        if (!Registered) Log.Warn("Ctrl+Alt+Z is used by another app; the overlay shortcut is unavailable.");
    }

    nint Hook(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == WM_HOTKEY && w == Id)
        {
            _pressed();
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        UnregisterHotKey(_source.Handle, Id);
        _source.Dispose();
    }
}
