using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ZerkSense5.Games;

sealed record LibraryGame(string Key, string Name, string Source, int? SteamAppId, string InstallDir, string? Exe = null, string[]? Aliases = null)
{
    public bool Contains(string path) =>
        path.StartsWith(InstallDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

sealed record CustomGame(string Exe, string Name);

static class GameLibrary
{
    static readonly string[] SteamTools =
    [
        "Steamworks Common Redistributables", "SteamVR", "Steam Linux Runtime", "Proton", "Wallpaper Engine",
        "Lossless Scaling", "Spacewar",
    ];

    static readonly string[] GamePublishers = ["Blizzard Entertainment", "Electronic Arts", "Riot Games", "Rockstar Games", "Bethesda"];

    public static List<string> AutoGameFolders() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "Games"))
            .Where(Directory.Exists)
            .ToList();

    public static List<LibraryGame> Scan(IEnumerable<CustomGame> custom, IEnumerable<string> gameFolders)
    {
        var games = new List<LibraryGame>();
        void Try(Action scan)
        {
            try
            {
                scan();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException or System.Security.SecurityException)
            {
            }
        }
        Try(() => games.AddRange(Steam()));
        Try(() => games.AddRange(Epic()));
        Try(() => games.AddRange(Gog()));
        Try(() => games.AddRange(Ubisoft()));
        Try(() => games.AddRange(Xbox()));
        Try(() => games.AddRange(Uninstall(games)));
        Try(() => games.AddRange(GameFolders(gameFolders, games)));
        foreach (var c in custom)
        {
            if (!File.Exists(c.Exe)) continue;
            games.Insert(0, new LibraryGame(CustomKey(c.Exe), c.Name, "Added manually", null, Path.GetDirectoryName(c.Exe)!, c.Exe));
        }
        return games
            .Where(g => Directory.Exists(g.InstallDir))
            .GroupBy(g => g.SteamAppId is int id ? "steam-app:" + id : g.Key)
            .Select(g => g.First())
            .ToList();
    }

    public static string CustomKey(string exe) => "exe:" + Path.GetFileName(exe).ToLowerInvariant();

    static IEnumerable<LibraryGame> Steam()
    {
        string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        steam ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var libraries = new List<string> { Path.Combine(steam.Replace('/', '\\'), "steamapps") };
        var vdf = Path.Combine(libraries[0], "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), @"""path""\s+""([^""]+)"""))
                libraries.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            foreach (var acf in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                if (!int.TryParse(Vdf(text, "appid"), out int id)) continue;
                string name = Vdf(text, "name") ?? "", dir = Vdf(text, "installdir") ?? "";
                if (name.Length == 0 || dir.Length == 0 || SteamTools.Any(t => name.StartsWith(t, StringComparison.OrdinalIgnoreCase))) continue;
                yield return new LibraryGame($"steam:{id}", name, "Steam", id, Path.Combine(lib, "common", dir));
            }
    }

    public static string? Vdf(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    static IEnumerable<LibraryGame> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(item));
            var r = doc.RootElement;
            bool isGame = r.TryGetProperty("AppCategories", out var cats) &&
                          cats.EnumerateArray().Any(c => c.GetString() == "games");
            string? loc = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
            string? name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
            string? app = r.TryGetProperty("AppName", out var a) ? a.GetString() : null;
            string? exe = r.TryGetProperty("LaunchExecutable", out var e) ? e.GetString() : null;
            if (!isGame || string.IsNullOrEmpty(loc) || name == null || app == null) continue;
            loc = loc.Replace('/', '\\');
            yield return new LibraryGame($"epic:{app}", name, "Epic Games", null, loc,
                string.IsNullOrEmpty(exe) ? null : Path.Combine(loc, exe.Replace('/', '\\')));
        }
    }

    static IEnumerable<LibraryGame> Gog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games == null) yield break;
        foreach (var id in games.GetSubKeyNames())
        {
            using var k = games.OpenSubKey(id);
            if (k?.GetValue("path") is not string path || path.Length == 0) continue;
            string name = k.GetValue("gameName") as string ?? Path.GetFileName(path.TrimEnd('\\'));
            yield return new LibraryGame($"gog:{id}", name, "GOG", null, path, k.GetValue("exe") as string);
        }
    }

    static IEnumerable<LibraryGame> Ubisoft()
    {
        using var installs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (installs == null) yield break;
        foreach (var id in installs.GetSubKeyNames())
        {
            using var k = installs.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is not string dir || dir.Length == 0) continue;
            dir = dir.Replace('/', '\\');
            yield return new LibraryGame($"ubisoft:{id}", Path.GetFileName(dir.TrimEnd('\\')), "Ubisoft Connect", null, dir);
        }
    }

    static IEnumerable<LibraryGame> Xbox()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var game in Directory.EnumerateDirectories(root))
            {
                var config = Path.Combine(game, "Content", "MicrosoftGame.config");
                if (!File.Exists(config)) continue;
                var xml = XDocument.Load(config);
                string name = xml.Descendants("ShellVisuals").FirstOrDefault()?.Attribute("DefaultDisplayName")?.Value
                              ?? Path.GetFileName(game);
                if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileName(game);
                string? exe = xml.Descendants("Executable").FirstOrDefault()?.Attribute("Name")?.Value;
                var content = Path.Combine(game, "Content");
                yield return new LibraryGame($"xbox:{Path.GetFileName(game).ToLowerInvariant()}", name, "Xbox", null, content,
                    exe == null ? null : Path.Combine(content, exe));
            }
        }
    }

    static IEnumerable<LibraryGame> Uninstall(List<LibraryGame> known)
    {
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("Publisher") is not string publisher || !GamePublishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (k.GetValue("InstallLocation") is not string dir || dir.Length < 4 || k.GetValue("DisplayName") is not string name) continue;
                if (name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) || name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("EA app", StringComparison.OrdinalIgnoreCase)) continue;
                dir = dir.Trim('"');
                if (known.Any(g => string.Equals(g.InstallDir.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
                yield return new LibraryGame("dir:" + Path.GetFileName(dir.TrimEnd('\\')).ToLowerInvariant(), name, publisher, null, dir);
            }
        }
    }

    static IEnumerable<LibraryGame> GameFolders(IEnumerable<string> roots, List<LibraryGame> known)
    {
        var uninstall = GameNames.UninstallNames();
        foreach (var root in roots.Concat(AutoGameFolders()).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (known.Any(g => g.Contains(dir + "\\"))) continue;
                if (!Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).Any()) continue;
                var id = GameNames.Identify(dir, uninstall);
                yield return new LibraryGame("dir:" + Path.GetFileName(dir).ToLowerInvariant(), id.Name, "Games folder", id.SteamAppId, dir, null, id.Aliases);
            }
    }

    static readonly Regex HelperExe = new(
        @"crash|report|launcher|unins|setup|redist|vcredist|dxsetup|easyanticheat|eac_|start_protected|anticheat|beservice|battleye|helper|updater|cef|webview|prereq|install|config|benchmark|editor|server|extract|tool",
        RegexOptions.IgnoreCase);

    public static bool IsHelper(string exe) => HelperExe.IsMatch(Path.GetFileNameWithoutExtension(exe));

    public static string? GuessExe(LibraryGame game)
    {
        if (game.Exe != null && File.Exists(game.Exe)) return game.Exe;
        try
        {
            return Directory.EnumerateFiles(game.InstallDir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(f => !IsHelper(f))
                .OrderBy(f => f.Count(c => c == '\\'))
                .ThenByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
