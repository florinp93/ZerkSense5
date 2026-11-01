using System.Runtime.InteropServices;
using ZerkSense5.UI;

namespace ZerkSense5;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        int apply = Array.IndexOf(args, "--apply-update");
        if (apply >= 0 && args.Length > apply + 2 && int.TryParse(args[apply + 2], out int oldPid))
            return Updater.Apply(args[apply + 1], oldPid);

        if (args.Contains("--demo"))
        {
            using var demoService = new BridgeService(Demo.CreateConfig());
            return new App(demoService, startHidden: false, demo: true).Run();
        }

        var cfg = Config.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));

        if (args.Contains("--probe") || args.Contains("--selftest") || args.Contains("--games") || args.Contains("--capture") || args.Contains("--check-update") || args.Contains("--speaker-test"))
        {
            AttachConsole(-1);
            if (args.Contains("--speaker-test")) return Diagnostics.SpeakerTest(cfg, 5);
            if (args.Contains("--capture"))
            {
                var pidArg = args.SkipWhile(a => a != "--capture").Skip(1).FirstOrDefault();
                return Diagnostics.Capture(cfg, int.TryParse(pidArg, out int pid) ? pid : null);
            }
            if (args.Contains("--check-update")) return Diagnostics.CheckUpdate(args.Contains("--force"));
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
