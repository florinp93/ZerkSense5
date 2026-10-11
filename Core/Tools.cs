using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ZerkSense5;

static class Log
{
    static readonly object Lock = new();

    public static event Action<DateTime, string, string>? Message;

    static void Write(ConsoleColor color, string tag, string msg)
    {
        var now = DateTime.Now;
        Message?.Invoke(now, tag, msg);
        lock (Lock)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{now:HH:mm:ss} ");
                Console.ForegroundColor = color;
                Console.Write($"[{tag}]");
                Console.ResetColor();
                Console.WriteLine(" " + msg);
            }
            catch (IOException)
            {
            }
        }
    }

    public static void Info(string msg) => Write(ConsoleColor.Cyan, "info", msg);
    public static void Warn(string msg) => Write(ConsoleColor.Yellow, "warn", msg);
    public static void Error(string msg) => Write(ConsoleColor.Red, "fail", msg);
}

static class Proc
{
    public static (int code, string output) Run(string exe, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            return (-1, "timed out");
        }
        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    public static string? FindExe(string configured, string name, params string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), name);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }
}

sealed class Usbip(string exe, int tcpPort)
{
    int? _port;

    public static Usbip? Locate(Config cfg)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var exe = Proc.FindExe(cfg.UsbipPath, "usbip.exe", Path.Combine(pf, "USBip", "usbip.exe"));
        return exe == null ? null : new Usbip(exe, cfg.UsbipPort);
    }

    string Global => tcpPort == 3240 ? "" : $"--tcp-port {tcpPort} ";

    public bool Attach()
    {
        var (code, output) = Proc.Run(exe, $"{Global}attach -r 127.0.0.1 -b {UsbIpServer.BusId}");
        var m = Regex.Match(output, @"port\s+(\d+)", RegexOptions.IgnoreCase);
        if (code == 0 && m.Success) _port = int.Parse(m.Groups[1].Value);
        if (code != 0) Log.Error($"usbip attach failed ({code}): {output}");
        return code == 0;
    }

    public void Detach()
    {
        if (_port is not int port) return;
        _port = null;
        Proc.Run(exe, $"detach -p {port}");
    }
}

sealed class HidHide(string cli)
{
    readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

    public static HidHide? Locate()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var cli = Proc.FindExe("", "HidHideCLI.exe",
            Path.Combine(pf, "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe"));
        return cli == null ? null : new HidHide(cli);
    }

    public void Hide(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId) || _hidden.Contains(instanceId)) return;
        Proc.Run(cli, $"--app-reg \"{Environment.ProcessPath}\"");
        var (code, output) = Proc.Run(cli, $"--dev-hide \"{instanceId}\"");
        Proc.Run(cli, "--cloak-on");
        if (code == 0)
        {
            _hidden.Add(instanceId);
            Log.Info("HidHide: Bluetooth controller hidden from other applications");
        }
        else
        {
            Log.Warn($"HidHide: could not hide controller: {output}");
        }
    }

    public void UnhideAll()
    {
        foreach (var id in _hidden) Proc.Run(cli, $"--dev-unhide \"{id}\"");
        _hidden.Clear();
    }
}
