using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZerkSense5;

sealed record HidDeviceInfo(string Path, string InstanceId, ushort VendorId, ushort ProductId,
    int InputLength, int OutputLength, int FeatureLength);

static unsafe class HidNative
{
    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out nint data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(nint data, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte* buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte* buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, int n);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern nint SetupDiGetClassDevsW(ref Guid cls, nint enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SP_DEVICE_INTERFACE_DATA data, byte* detail, int size, out int required, ref SP_DEVINFO_DATA devInfo);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref SP_DEVINFO_DATA devInfo, char* id, int size, out int required);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

    public static SafeFileHandle Open(string path, bool overlapped) =>
        CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING,
            overlapped ? FILE_FLAG_OVERLAPPED : 0, 0);

    public static List<HidDeviceInfo> Enumerate(ushort vendorId)
    {
        var result = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out Guid guid);
        nint set = SetupDiGetClassDevsW(ref guid, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == -1) return result;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = sizeof(SP_DEVICE_INTERFACE_DATA) };
            byte* detail = stackalloc byte[2048];
            char* idBuf = stackalloc char[512];
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref ifData); i++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = sizeof(SP_DEVINFO_DATA) };
                *(int*)detail = IntPtr.Size == 8 ? 8 : 6;
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, detail, 2048, out _, ref devInfo)) continue;
                string path = new string((char*)(detail + 4));

                string instanceId = SetupDiGetDeviceInstanceIdW(set, ref devInfo, idBuf, 512, out _)
                    ? new string(idBuf) : "";

                var info = Query(path, instanceId);
                if (info != null && info.VendorId == vendorId) result.Add(info);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    static HidDeviceInfo? Query(string path, string instanceId)
    {
        using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;
        var attr = new HIDD_ATTRIBUTES { Size = sizeof(HIDD_ATTRIBUTES) };
        if (!HidD_GetAttributes(h, ref attr)) return null;
        if (!HidD_GetPreparsedData(h, out nint pp)) return null;
        try
        {
            if (HidP_GetCaps(pp, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) return null;
            return new HidDeviceInfo(path, instanceId, attr.VendorID, attr.ProductID,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
    }

    public static bool GetFeature(SafeFileHandle h, Span<byte> buf)
    {
        fixed (byte* p = buf) return HidD_GetFeature(h, p, buf.Length);
    }

    public static bool SetFeature(SafeFileHandle h, Span<byte> buf)
    {
        fixed (byte* p = buf) return HidD_SetFeature(h, p, buf.Length);
    }

    public static void SetInputBuffers(SafeFileHandle h, int n) => HidD_SetNumInputBuffers(h, n);
}
