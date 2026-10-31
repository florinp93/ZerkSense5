using System.Runtime.InteropServices;
using ZerkSense5.UI;

namespace ZerkSense5;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        var cfg = Config.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

        if (args.Contains("--probe") || args.Contains("--selftest") || args.Contains("--games") || args.Contains("--capture"))
        {
            AttachConsole(-1);
            if (args.Contains("--capture"))
            {
                var pidArg = args.SkipWhile(a => a != "--capture").Skip(1).FirstOrDefault();
                return Diagnostics.Capture(cfg, int.TryParse(pidArg, out int pid) ? pid : null);
            }
            if (args.Contains("--games")) return Diagnostics.Games(args.SkipWhile(a => a != "--games").Skip(1).ToArray());
            return args.Contains("--probe") ? Diagnostics.Probe() : Diagnostics.SelfTest(new Config());
        }

        using var single = new Mutex(true, @"Local\ZerkSense5", out bool first);
        if (!first)
        {
            SingleInstance.SignalExisting();
            return 0;
        }

        using var service = new BridgeService(cfg);
        var app = new App(service, startHidden: cfg.StartMinimized || args.Contains("--minimized"));
        service.Start();
        return app.Run();
    }
}
