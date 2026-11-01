using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ZerkSense5.Games;

namespace ZerkSense5;

static class LatencyTest
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int index, out XInputState state);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, nint overlapped);
    [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, nint overlapped);

    const int WarmupMs = 2000, SettleMs = 100;

    static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;

    public static int Run(Config cfg, int seconds, bool xbox)
    {
        if (Mutex.TryOpenExisting(@"Local\ZerkSense5", out var running))
        {
            running.Dispose();
            Console.WriteLine("Zerk Sense 5 is running. Exit it from the tray first; the test runs its own bridge.");
            return 1;
        }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("Run this from an administrator terminal: plugging in the virtual controller needs admin rights.");
            return 1;
        }

        using var bridge = new BridgeService(cfg);
        bridge.SetMode(xbox ? ControllerMode.Xbox360 : ControllerMode.DualSense);

        var btTimes = new List<long>();
        var sensor = new List<uint>();
        var btByKey = new Dictionary<ulong, long>();
        var stateSeen = new Dictionary<string, long>();
        var gate = new object();
        bool recording = false;
        long started = 0;
        bool Settling(long now) => (now - started) * TickUs < SettleMs * 1000;

        bridge.RawInput += (buf, len) =>
        {
            long now = Stopwatch.GetTimestamp();
            if (!recording || len < 2 + 63 || buf[0] != 0x31 || (buf[1] & 0x02) != 0) return;
            var s = buf.AsSpan(2, 63);
            lock (gate)
            {
                btTimes.Add(now);
                sensor.Add(BitConverter.ToUInt32(s[27..31]));
                if (xbox) stateSeen[Convert.ToHexString(XboxState(s))] = now;
                else btByKey[Key(s)] = now;
            }
        };

        Console.WriteLine($"Starting the bridge in {(xbox ? "Xbox 360" : "DualSense")} mode. Turn the controller on if it isn't already.");
        bridge.Start();
        var deadline = Environment.TickCount64 + 30000;
        while (bridge.State != BridgeState.Attached && Environment.TickCount64 < deadline) Thread.Sleep(100);
        if (bridge.State != BridgeState.Attached)
        {
            Console.WriteLine($"The virtual controller never attached (state: {bridge.State}). Is the DualSense on and usbip-win2 installed?");
            return 1;
        }

        Thread.Sleep(WarmupMs);
        var outTimes = new List<long>();
        var latency = new List<double>();
        int unmatched = 0, readError = 0;
        SafeFileHandle? cleanup = null;
        using var stop = new CancellationTokenSource();
        Thread reader;
        string source;

        if (xbox)
        {
            int index = Enumerable.Range(0, 4).FirstOrDefault(i => XInputGetState(i, out _) == 0, -1);
            if (index < 0)
            {
                Console.WriteLine("No XInput controller found.");
                return 1;
            }
            source = $"XInput slot {index}, polled continuously";
            reader = new Thread(() =>
            {
                uint lastPacket = 0;
                while (!stop.IsCancellationRequested)
                {
                    if (XInputGetState(index, out var st) != 0 || st.PacketNumber == lastPacket) continue;
                    long now = Stopwatch.GetTimestamp();
                    lastPacket = st.PacketNumber;
                    string key = Convert.ToHexString(XboxState(st));
                    lock (gate)
                    {
                        if (Settling(now) || !recording) continue;
                        outTimes.Add(now);
                        if (stateSeen.TryGetValue(key, out long t)) latency.Add((now - t) * TickUs);
                        else unmatched++;
                    }
                }
            });
        }
        else
        {
            var info = HidNative.Enumerate(UsbDescriptors.VendorId).FirstOrDefault(d => d.InputLength == 64);
            if (info == null)
            {
                Console.WriteLine("The virtual wired DualSense didn't show up as a HID device.");
                return 1;
            }
            source = "virtual wired DualSense, read as a HID device";
            var handle = HidNative.Open(info.Path, overlapped: false);
            if (handle.IsInvalid)
            {
                Console.WriteLine($"Could not open the virtual wired DualSense (error {Marshal.GetLastWin32Error()}).");
                return 1;
            }
            cleanup = handle;
            HidNative.SetInputBuffers(handle, 64);
            reader = new Thread(() =>
            {
                var report = new byte[64];
                while (!stop.IsCancellationRequested)
                {
                    if (!ReadFile(handle, report, 64, out int read, 0))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 995) readError = error;
                        break;
                    }
                    if (read < 64 || report[0] != 0x01) continue;
                    long now = Stopwatch.GetTimestamp();
                    var s = report.AsSpan(1, 63);
                    lock (gate)
                    {
                        bool matched = btByKey.Remove(Key(s), out long t);
                        if (Settling(now) || !recording && !matched) continue;
                        outTimes.Add(now);
                        if (matched) latency.Add((now - t) * TickUs);
                        else unmatched++;
                    }
                }
            });
            stop.Token.Register(() => CancelIoEx(handle, 0));
        }

        Console.WriteLine($"Measuring for {seconds} s. {(xbox ? "Keep moving both sticks the whole time." : "Hold the controller normally; moving the sticks is fine.")}");
        reader.Priority = ThreadPriority.AboveNormal;
        reader.IsBackground = true;
        lock (gate)
        {
            recording = true;
            started = Stopwatch.GetTimestamp();
        }
        reader.Start();
        Thread.Sleep(seconds * 1000);
        lock (gate) recording = false;
        Thread.Sleep(100);
        stop.Cancel();
        reader.Join(1000);
        cleanup?.Dispose();

        int missing;
        double sensorTickUs;
        lock (gate)
        {
            missing = xbox ? 0 : btByKey.Count;
            sensorTickUs = SensorTick(btTimes, sensor);
        }

        var text = new StringBuilder();
        text.AppendLine($"Zerk Sense 5 {Updater.Current} latency test, {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine($"Mode: {(xbox ? "Xbox 360" : "DualSense")} · measured at: {source} · {seconds} s");
        text.AppendLine();
        text.AppendLine("Added latency (Bluetooth report in -> same input out of the virtual controller), microseconds");
        text.AppendLine(Row("", "count", "median", "p95", "p99", "max"));
        text.AppendLine(Stats("added", latency, stdev: false));
        text.AppendLine();
        text.AppendLine("Report intervals (jitter), microseconds");
        text.AppendLine(Row("", "count", "median", "p95", "p99", "max", "stdev"));
        if (sensorTickUs > 0) text.AppendLine(Stats("controller sent", Deltas(sensor, sensorTickUs)));
        text.AppendLine(Stats("Bluetooth in", Deltas(btTimes)));
        text.AppendLine(Stats(xbox ? "XInput changes" : "virtual out", Deltas(outTimes)));
        text.AppendLine();
        if (xbox)
            text.AppendLine($"XInput states with no matching Bluetooth report: {unmatched}");
        else
            text.AppendLine($"Bluetooth reports never seen on the virtual controller: {missing} of {btTimes.Count} · unmatched virtual reports: {unmatched}");
        if (readError != 0) text.AppendLine($"Reading the virtual controller stopped early: Windows error {readError}");
        if (sensorTickUs > 0) text.AppendLine($"Controller timestamp unit: {sensorTickUs:F3} µs (derived from the run)");

        Console.WriteLine();
        Console.Write(text);
        string file = Path.Combine(AppContext.BaseDirectory, $"latency-{(xbox ? "xbox360" : "dualsense")}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            File.WriteAllText(file, text.ToString());
            Console.WriteLine($"Saved to {file}");
        }
        catch (IOException e)
        {
            Console.WriteLine($"Could not save the report: {e.Message}");
        }
        return 0;
    }

    static ulong Key(ReadOnlySpan<byte> s) => (ulong)BitConverter.ToUInt32(s[27..31]) << 8 | s[6];

    static byte[] XboxState(ReadOnlySpan<byte> s)
    {
        var r = VirtualXbox360.Report(s).AsSpan(2, 12).ToArray();
        r[1] &= 0xFB;
        return r;
    }

    static byte[] XboxState(XInputState st)
    {
        var r = new byte[12];
        BitConverter.TryWriteBytes(r.AsSpan(0), st.Buttons);
        r[1] &= 0xFB;
        r[2] = st.LeftTrigger;
        r[3] = st.RightTrigger;
        BitConverter.TryWriteBytes(r.AsSpan(4), st.ThumbLX);
        BitConverter.TryWriteBytes(r.AsSpan(6), st.ThumbLY);
        BitConverter.TryWriteBytes(r.AsSpan(8), st.ThumbRX);
        BitConverter.TryWriteBytes(r.AsSpan(10), st.ThumbRY);
        return r;
    }

    static double SensorTick(List<long> host, List<uint> sensor)
    {
        if (sensor.Count < 100) return 0;
        long ticks = 0;
        for (int i = 1; i < sensor.Count; i++) ticks += (uint)(sensor[i] - sensor[i - 1]);
        return ticks == 0 ? 0 : (host[^1] - host[0]) * TickUs / ticks;
    }

    static List<double> Deltas(List<long> times)
    {
        var d = new List<double>(times.Count);
        for (int i = 1; i < times.Count; i++) d.Add((times[i] - times[i - 1]) * TickUs);
        return d;
    }

    static List<double> Deltas(List<uint> ticks, double unitUs)
    {
        var d = new List<double>(ticks.Count);
        for (int i = 1; i < ticks.Count; i++) d.Add((uint)(ticks[i] - ticks[i - 1]) * unitUs);
        return d;
    }

    static string Stats(string name, List<double> v, bool stdev = true)
    {
        if (v.Count == 0) return Row(name, "0");
        var sorted = v.Order().ToArray();
        double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        double mean = v.Average(), sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
        var cells = new List<string> { v.Count.ToString(), $"{P(0.5):F0}", $"{P(0.95):F0}", $"{P(0.99):F0}", $"{sorted[^1]:F0}" };
        if (stdev) cells.Add($"{sd:F0}");
        return Row(name, cells.ToArray());
    }

    static string Row(string name, params string[] cells) =>
        name.PadRight(18) + string.Concat(cells.Select(c => c.PadLeft(9)));
}
