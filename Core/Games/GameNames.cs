using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZerkSense5.Games;

sealed record GameIdentity(string Name, int? SteamAppId, string[] Aliases);

static class GameNames
{
    static readonly Regex Brackets = new(@"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}");
    static readonly Regex ReleaseGroup = new(@"-[A-Z0-9]{2,12}$");
    static readonly Regex Version = new(
        @"\b(v|ver|version)\.?\s*\d[\w.\-]*|\bbuild\s*\d+|\bupdate\s*\d+|\b\d+(\.\d+)+[\w.\-]*",
        RegexOptions.IgnoreCase);
    static readonly Regex Noise = new(
        @"\b(repack|multi\d*|x64|x86|win64|win32|windows|portable|incl|sp|mp|pc|rip|proper|internal|alpha|beta|retail|launcher|shipping)\b",
        RegexOptions.IgnoreCase);
    static readonly Regex CamelSplit = new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z]{2})(?=\d)|(?<=\d)(?=[A-Za-z]{2})");

    static readonly HashSet<string> GenericDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "binaries", "win64", "win32", "x64", "x86", "bin", "bin64", "game", "content", "retail", "engine", "build",
        "release", "shipping", "client", "app", "data", "_retail_", "system", "program", "exe",
    };

    static readonly HashSet<string> RootDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "games", "game", "jocuri", "gry", "spiele", "jeux", "juegos", "giochi", "program files", "program files (x86)",
        "steamapps", "common", "xboxgames", "epic games", "gog games", "gog galaxy",
    };

    public static string Clean(string raw)
    {
        string s = Brackets.Replace(Regex.Replace(raw, @"[™®©]", ""), " ").Trim();
        bool releaseStyle = !s.Contains(' ');
        if (releaseStyle) s = ReleaseGroup.Replace(s, "");
        s = Version.Replace(s, " ");
        if (releaseStyle || s.Contains('_')) s = Regex.Replace(s, @"[._]", " ");
        if (!s.Contains(' ')) s = CamelSplit.Replace(s, " ");
        s = Noise.Replace(s, " ");
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '_', '.', ':');
    }

    public static Dictionary<string, string> UninstallNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new (RegistryKey hive, string path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            using var root = hive.OpenSubKey(path);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("InstallLocation") is not string dir || dir.Length < 4 || k.GetValue("DisplayName") is not string name) continue;
                names[dir.Trim('"').TrimEnd('\\')] = name;
            }
        }
        return names;
    }

    public static GameIdentity Identify(string dir, IReadOnlyDictionary<string, string> uninstall)
    {
        var names = new List<string>();
        int? appId = null;

        foreach (var info in SafeFiles(dir, "goggame-*.info"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } gog) names.Add(gog);
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
            }
        }

        appId = SteamAppIdIn(dir);

        if (uninstall.TryGetValue(dir.TrimEnd('\\'), out var display)) names.Add(Clean(display));
        names.Add(Clean(Path.GetFileName(dir.TrimEnd('\\'))));

        var distinct = names.Where(n => n.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string best = distinct.FirstOrDefault() ?? Path.GetFileName(dir.TrimEnd('\\'));
        return new GameIdentity(best, appId, distinct);
    }

    public static int? SteamAppIdIn(string dir)
    {
        foreach (var file in SafeFiles(dir, "steam_appid.txt"))
            if (int.TryParse(File.ReadAllText(file).Trim(), out int id) && id > 0) return id;
        foreach (var file in SafeFiles(dir, "steam_emu.ini").Concat(SafeFiles(dir, "steam_api.ini")))
            foreach (var line in File.ReadLines(file))
            {
                var m = Regex.Match(line, @"^\s*app_?id\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int id) && id > 0) return id;
            }
        return null;
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Take(4).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string GameRoot(string exe)
    {
        string dir = Path.GetDirectoryName(exe)!;
        string best = dir;
        for (var d = new DirectoryInfo(dir); d?.Parent != null; d = d.Parent)
        {
            if (RootDirs.Contains(d.Parent.Name) || d.Parent.Parent == null) return d.FullName;
            if (!GenericDirs.Contains(d.Name)) best = d.FullName;
            if (File.Exists(Path.Combine(d.FullName, "unins000.exe")) || Directory.EnumerateFiles(d.FullName, "goggame-*.info").Any()) return d.FullName;
        }
        return best;
    }
}
