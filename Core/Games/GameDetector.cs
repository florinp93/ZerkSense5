using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ZerkSense5.Games;

sealed record GameInfo(string Key, string Name, string Source, int? SteamAppId, string ExePath, int ProcessId, string[]? Aliases = null);

sealed class GameDetector : IDisposable
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(nint h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

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

    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "unrealgame", "unity", "unity player", "launcher", "application", "client", "main", "app", "default",
    };

    readonly Config _cfg;
    readonly Timer _timer;
    readonly object _lock = new();
    readonly HashSet<int> _notFullscreenGames = new();
    List<LibraryGame> _library = new();
    IReadOnlyDictionary<string, string> _uninstall = new Dictionary<string, string>();
    DateTime _libraryScanned;
    GameInfo? _current;
    Process? _currentProcess;
    int _busy;

    public GameDetector(Config cfg)
    {
        _cfg = cfg;
        _timer = new Timer(_ => Tick(), null, 1000, 2000);
    }

    public event Action<GameInfo>? GameStarted;
    public event Action<GameInfo>? GameStopped;
    public event Action? LibraryChanged;

    public GameInfo? Current => _current;

    public IReadOnlyList<LibraryGame> Library
    {
        get
        {
            lock (_lock) return _library;
        }
    }

    public void RescanLibrary()
    {
        List<LibraryGame> games;
        List<string> folders;
        lock (_cfg.GameFolders) folders = _cfg.GameFolders.ToList();
        lock (_cfg.CustomGames) games = GameLibrary.Scan(_cfg.CustomGames.ToList(), folders);
        _uninstall = GameNames.UninstallNames();
        lock (_lock)
        {
            _library = games;
            _libraryScanned = DateTime.UtcNow;
        }
        LibraryChanged?.Invoke();
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (DateTime.UtcNow - _libraryScanned > TimeSpan.FromMinutes(5))
            {
                RescanLibrary();
                _notFullscreenGames.Clear();
            }
            Detect();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    void Detect()
    {
        if (_current != null && (_currentProcess == null || _currentProcess.HasExited))
        {
            var ended = _current;
            _current = null;
            _currentProcess?.Dispose();
            _currentProcess = null;
            GameStopped?.Invoke(ended);
        }

        var found = FromForeground() ?? FromSteamRunning() ?? FromLibraryProcesses();
        if (found == null || found.Key == _current?.Key && found.ProcessId == _current.ProcessId) return;
        if (_current != null && found.Key != _current.Key && !IsForeground(found.ProcessId)) return;

        if (_current != null) GameStopped?.Invoke(_current);
        _currentProcess?.Dispose();
        _currentProcess = Process.GetProcessById(found.ProcessId);
        _current = found;
        GameStarted?.Invoke(found);
    }

    bool Ignored(string key)
    {
        lock (_cfg.IgnoredGames) return _cfg.IgnoredGames.Contains(key);
    }

    static bool IsForeground(int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out int fg);
        return fg == pid;
    }

    GameInfo? FromForeground()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        if (_current?.ProcessId == pid) return _current;
        string? exe = PathOf(pid);
        if (exe == null || NotGames.Contains(Path.GetFileNameWithoutExtension(exe))) return null;
        return FromLibrary(exe, pid) ?? FromFullscreen(pid, exe, hwnd);
    }

    GameInfo? FromSteamRunning()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0) is not int appId || appId == 0) return null;
        var game = Library.FirstOrDefault(g => g.SteamAppId == appId);
        if (game == null || Ignored(game.Key)) return null;
        var main = MainProcessIn(game);
        return main == null ? null : Info(game, main.Value.exe, main.Value.pid);
    }

    GameInfo? FromLibraryProcesses()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId || p.MainWindowHandle == 0) continue;
                string? exe = PathOf(p.Id);
                if (exe == null) continue;
                var game = FromLibrary(exe, p.Id);
                if (game != null) return game;
            }
        }
        return null;
    }

    (int pid, string exe)? MainProcessIn(LibraryGame game)
    {
        (int pid, string exe, long memory)? best = null;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.MainWindowHandle == 0) continue;
                string? exe = PathOf(p.Id);
                if (exe == null || !game.Contains(exe) || GameLibrary.IsHelper(exe)) continue;
                long memory = p.WorkingSet64;
                if (best == null || memory > best.Value.memory) best = (p.Id, exe, memory);
            }
        }
        return best is { } b ? (b.pid, b.exe) : null;
    }

    GameInfo? FromLibrary(string exe, int pid)
    {
        if (GameLibrary.IsHelper(exe)) return null;
        var game = Library.FirstOrDefault(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase))
                   ?? Library.FirstOrDefault(g => g.Contains(exe));
        if (game == null || Ignored(game.Key)) return null;
        return Info(game, exe, pid);
    }

    static GameInfo Info(LibraryGame g, string exe, int pid) => new(g.Key, g.Name, g.Source, g.SteamAppId, exe, pid, g.Aliases);

    GameInfo? FromFullscreen(int pid, string exe, nint hwnd)
    {
        if (!_cfg.DetectFullscreenApps || _notFullscreenGames.Contains(pid) || !IsFullscreen(hwnd)) return null;
        string key = GameLibrary.CustomKey(exe);
        if (Ignored(key)) return null;
        using var p = Process.GetProcessById(pid);
        bool graphics;
        try
        {
            graphics = p.Modules.Cast<ProcessModule>().Any(m => GraphicsModules.Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            graphics = false;
        }
        if (!graphics)
        {
            _notFullscreenGames.Add(pid);
            return null;
        }
        string seen = NameFor(p, exe);
        var id = GameNames.Identify(GameNames.GameRoot(exe), _uninstall);
        var aliases = id.Aliases.Append(GameNames.Clean(seen)).Append(GameNames.Clean(p.MainWindowTitle))
            .Where(n => n.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new GameInfo(key, aliases.FirstOrDefault() ?? seen, "Fullscreen app", id.SteamAppId, exe, pid, aliases);
    }

    static string? PathOf(int pid)
    {
        nint h = OpenProcess(0x1000, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

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
