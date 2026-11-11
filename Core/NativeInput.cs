using System.Runtime.InteropServices;
using System.Text;

namespace ZerkSense5;

enum MouseButton { Left, Right, Middle, Back, Forward }

static class NativeInput
{
    const int InputMouse = 0, InputKeyboard = 1;
    const uint KeyFlagExtended = 0x0001, KeyFlagUp = 0x0002, KeyFlagScanCode = 0x0008;
    const uint MouseMove = 0x0001, MouseWheel = 0x0800, MouseHWheel = 0x1000;
    const uint XButton1 = 0x0001, XButton2 = 0x0002;
    public const int WheelDelta = 120;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, StringBuilder name, int size);

    static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0) SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT Key(int vk, bool up)
    {
        uint scan = MapVirtualKey((uint)vk, 4);
        uint flags = up ? KeyFlagUp : 0;
        if (scan != 0)
        {
            flags |= KeyFlagScanCode;
            if ((scan & 0xFF00) is 0xE000 or 0xE100 || IsExtended(vk)) flags |= KeyFlagExtended;
        }
        return new INPUT { type = InputKeyboard, u = { ki = { wVk = (ushort)(scan != 0 ? 0 : vk), wScan = (ushort)(scan & 0xFF), dwFlags = flags } } };
    }

    // Keys whose scan code needs the E0 prefix even when MapVirtualKey doesn't report it.
    static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E
        or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5 or 0x2C;

    public static void KeyDown(int vk) => Send(Key(vk, false));
    public static void KeyUp(int vk) => Send(Key(vk, true));

    public static void MoveMouse(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Send(new INPUT { type = InputMouse, u = { mi = { dx = dx, dy = dy, dwFlags = MouseMove } } });
    }

    public static void Wheel(int delta, bool horizontal = false)
    {
        if (delta == 0) return;
        Send(new INPUT { type = InputMouse, u = { mi = { mouseData = unchecked((uint)delta), dwFlags = horizontal ? MouseHWheel : MouseWheel } } });
    }

    public static void Mouse(MouseButton button, bool down)
    {
        var (flags, data) = button switch
        {
            MouseButton.Left => (down ? 0x0002u : 0x0004u, 0u),
            MouseButton.Right => (down ? 0x0008u : 0x0010u, 0u),
            MouseButton.Middle => (down ? 0x0020u : 0x0040u, 0u),
            MouseButton.Back => (down ? 0x0080u : 0x0100u, XButton1),
            _ => (down ? 0x0080u : 0x0100u, XButton2),
        };
        Send(new INPUT { type = InputMouse, u = { mi = { mouseData = data, dwFlags = flags } } });
    }

    public static string KeyName(int vk)
    {
        if (vk == 0) return "";
        uint scan = MapVirtualKey((uint)vk, 4);
        if (scan != 0)
        {
            int lParam = (int)((scan & 0xFF) << 16);
            if ((scan & 0xFF00) != 0 || IsExtended(vk)) lParam |= 1 << 24;
            var sb = new StringBuilder(64);
            if (GetKeyNameText(lParam, sb, sb.Capacity) > 0) return sb.ToString();
        }
        return vk switch
        {
            0xAD => "Volume mute",
            0xAE => "Volume down",
            0xAF => "Volume up",
            0xB0 => "Next track",
            0xB1 => "Previous track",
            0xB3 => "Play/pause",
            _ => $"Key 0x{vk:X2}",
        };
    }
}

static class BluetoothRadio
{
    const uint IoctlBthDisconnectDevice = 0x41000C;

    [StructLayout(LayoutKind.Sequential)]
    struct FindRadioParams
    {
        public uint dwSize;
    }

    [DllImport("BluetoothApis.dll", SetLastError = true)] static extern nint BluetoothFindFirstRadio(ref FindRadioParams p, out nint radio);
    [DllImport("BluetoothApis.dll", SetLastError = true)] static extern bool BluetoothFindNextRadio(nint find, out nint radio);
    [DllImport("BluetoothApis.dll")] static extern bool BluetoothFindRadioClose(nint find);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(nint device, uint code, ref ulong inBuf, int inSize, nint outBuf, int outSize, out int returned, nint overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);

    public static bool Disconnect(ulong address)
    {
        var p = new FindRadioParams { dwSize = (uint)Marshal.SizeOf<FindRadioParams>() };
        nint find = BluetoothFindFirstRadio(ref p, out nint radio);
        if (find == 0) return false;
        bool done = false;
        try
        {
            do
            {
                ulong addr = address;
                if (DeviceIoControl(radio, IoctlBthDisconnectDevice, ref addr, sizeof(ulong), 0, 0, out _, 0)) done = true;
                CloseHandle(radio);
            }
            while (!done && BluetoothFindNextRadio(find, out radio));
        }
        finally
        {
            BluetoothFindRadioClose(find);
        }
        return done;
    }
}
