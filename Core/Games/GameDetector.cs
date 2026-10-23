using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZerkSense5.Games;

sealed record GameInfo(string Key, string Name, string Source, int? SteamAppId, string ExePath, int ProcessId);

sealed class GameDetector : IDisposable
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "ApplicationFrameHost",
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "msedgewebview2", "Discord", "Spotify",
        "vlc", "mpv", "mpc-hc64", "PotPlayerMini64", "Netflix", "Teams", "ms-teams", "slack", "Zoom",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "GalaxyClient", "GOG Galaxy Notifications Renderer",
        "Battle.net", "EADesktop", "Origin", "upc", "UbisoftConnect", "RiotClientServices", "Playnite.DesktopApp", "Playnite.FullscreenApp",
        "UnityCrashHandler64", "UnityCrashHandler32", "CrashReportClient", "crashpad_handler", "ZerkSense5", "dotnet",
        "obs64", "LosslessScaling", "devenv", "Code", "WindowsTerminal", "powershell", "cmd", "mstsc", "vmware-vmx", "VirtualBoxVM",
    };

    static readonly string[] GraphicsModules = ["d3d9.dll", "d3d11.dll", "d3d12.dll", "vulkan-1.dll", "opengl32.dll"];

    readonly Func<bool> _detectFullscreen;
    readonly Timer _timer;
    readonly Dictionary<int, GameInfo?> _seen = new();
    readonly List<(string dir, string name, string appName)> _epic = new();
    DateTime _epicLoaded;
    GameInfo? _current;
    Process? _currentProcess;

    public event Action<GameInfo>? GameStarted;
    public event Action<GameInfo>? GameStopped;
    public GameInfo? Current => _current;

    public GameDetector(Func<bool> detectFullscreenApps)
    {
        _detectFullscreen = detectFullscreenApps;
        _timer = new Timer(_ => Tick(), null, 1000, 2000);
    }

    int _busy;

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            TickCore();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    void TickCore()
    {
        try
        {
            if (_current != null && (_currentProcess == null || _currentProcess.HasExited))
            {
                var ended = _current;
                _current = null;
                _currentProcess?.Dispose();
                _currentProcess = null;
                GameStopped?.Invoke(ended);
            }

            nint hwnd = GetForegroundWindow();
            if (hwnd == 0) return;
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0 || pid == Environment.ProcessId || pid == _current?.ProcessId) return;

            if (!_seen.TryGetValue(pid, out var game))
            {
                game = Classify(pid, hwnd);
                if (game != null) _seen[pid] = game;
                if (_seen.Count > 512) _seen.Clear();
            }
            if (game == null) return;

            if (_current != null) GameStopped?.Invoke(_current);
            _currentProcess?.Dispose();
            _currentProcess = Process.GetProcessById(pid);
            _current = game;
            GameStarted?.Invoke(game);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
        }
    }

    GameInfo? Classify(int pid, nint hwnd)
    {
        using var p = Process.GetProcessById(pid);
        if (NotGames.Contains(p.ProcessName)) return null;
        string? exe;
        try
        {
            exe = p.MainModule?.FileName;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        if (string.IsNullOrEmpty(exe)) return null;

        return FromSteam(exe, pid) ?? FromEpic(exe, pid) ?? FromGog(exe, pid) ?? FromFullscreen(p, exe, hwnd);
    }

    static GameInfo? FromSteam(string exe, int pid)
    {
        var m = Regex.Match(exe, @"^(.*\\steamapps)\\common\\([^\\]+)\\", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string steamapps = m.Groups[1].Value, folder = m.Groups[2].Value;
        foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
        {
            string text;
            try
            {
                text = File.ReadAllText(acf);
            }
            catch (IOException)
            {
                continue;
            }
            if (!string.Equals(Vdf(text, "installdir"), folder, StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(Vdf(text, "appid"), out int appId);
            string name = Vdf(text, "name") ?? folder;
            return new GameInfo($"steam:{appId}", name, "Steam", appId, exe, pid);
        }
        return new GameInfo("exe:" + Path.GetFileName(exe).ToLowerInvariant(), folder, "Steam", null, exe, pid);
    }

    static string? Vdf(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    GameInfo? FromEpic(string exe, int pid)
    {
        if (DateTime.UtcNow - _epicLoaded > TimeSpan.FromMinutes(5)) LoadEpic();
        foreach (var (dir, name, appName) in _epic)
            if (exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return new GameInfo($"epic:{appName}", name, "Epic Games", null, exe, pid);
        return null;
    }

    void LoadEpic()
    {
        _epicLoaded = DateTime.UtcNow;
        _epic.Clear();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;
                string? loc = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                string? name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                string? app = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                if (string.IsNullOrEmpty(loc) || name == null || app == null) continue;
                _epic.Add((loc.Replace('/', '\\').TrimEnd('\\') + "\\", name, app));
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
            }
        }
    }

    static GameInfo? FromGog(string exe, int pid)
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games == null) return null;
        foreach (var id in games.GetSubKeyNames())
        {
            using var k = games.OpenSubKey(id);
            if (k?.GetValue("path") is not string path || string.IsNullOrEmpty(path)) continue;
            if (!exe.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            string name = k.GetValue("gameName") as string ?? Path.GetFileName(path.TrimEnd('\\'));
            return new GameInfo($"gog:{id}", name, "GOG", null, exe, pid);
        }
        return null;
    }

    GameInfo? FromFullscreen(Process p, string exe, nint hwnd)
    {
        if (!_detectFullscreen() || !IsFullscreen(hwnd)) return null;
        bool graphics;
        try
        {
            graphics = p.Modules.Cast<ProcessModule>().Any(m => GraphicsModules.Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        if (!graphics) return null;

        return new GameInfo("exe:" + Path.GetFileName(exe).ToLowerInvariant(), NameFor(p, exe), "Fullscreen app", null, exe, p.Id);
    }

    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "unrealgame", "unity", "unity player", "launcher", "application", "client", "main", "app", "default",
    };

    static string NameFor(Process p, string exe)
    {
        var candidates = new List<string?> { p.MainWindowTitle };
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            candidates.Add(v.FileDescription);
            candidates.Add(v.ProductName);
        }
        catch (FileNotFoundException)
        {
        }
        candidates.Add(Path.GetFileName(Path.GetDirectoryName(exe)));
        foreach (var c in candidates)
        {
            string n = (c ?? "").Trim();
            if (n.Length is > 1 and < 80 && !GenericNames.Contains(n) && !n.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase))
                return n;
        }
        return Path.GetFileNameWithoutExtension(exe);
    }

    static bool IsFullscreen(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref mi)) return false;
        return r.L <= mi.rcMonitor.L && r.T <= mi.rcMonitor.T && r.R >= mi.rcMonitor.R && r.B >= mi.rcMonitor.B;
    }

    public static (int left, int top, int right, int bottom)? MonitorOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            nint hwnd = p.MainWindowHandle;
            if (hwnd == 0) return null;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref mi)) return null;
            return (mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R, mi.rcMonitor.B);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _currentProcess?.Dispose();
    }
}
