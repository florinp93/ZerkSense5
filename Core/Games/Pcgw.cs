using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZerkSense5.Games;

enum Support { Unknown, Yes, No, Limited, Modded, NotApplicable }

sealed class PcgwInfo
{
    public string Page { get; set; } = "";
    public DateTime Fetched { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();

    public string Url => "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(Page.Replace(' ', '_'));

    string Get(string key) => Fields.TryGetValue(key, out var v) ? v : "";

    static Support Parse(string v) => v.Trim().ToLowerInvariant() switch
    {
        "true" => Support.Yes,
        "false" => Support.No,
        "limited" => Support.Limited,
        "hackable" => Support.Modded,
        "n/a" => Support.NotApplicable,
        _ => Support.Unknown,
    };

    public Support ControllerSupport => Parse(Get("controller support"));
    public Support XInput => Parse(Get("xinput controllers"));
    public Support PlayStation => Parse(Get("playstation controllers"));
    public Support PlayStationPrompts => Parse(Get("playstation prompts"));
    public Support LightBar => Parse(Get("light bar support"));
    public Support AdaptiveTriggers => Parse(Get("dualsense adaptive trigger support"));
    public Support Haptics => Parse(Get("dualsense haptics support"));
    public Support Speaker => Parse(Get("playstation speaker"));
    public Support Motion => Parse(Get("playstation motion sensors"));

    public string[] PlayStationModels =>
        Get("playstation controller models").Split([',', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool SupportsDualSense =>
        PlayStationModels.Any(m => m.Equals("DS", StringComparison.OrdinalIgnoreCase) ||
                                   m.Equals("DSE", StringComparison.OrdinalIgnoreCase) ||
                                   m.Contains("dualsense", StringComparison.OrdinalIgnoreCase))
        || Haptics == Support.Yes || AdaptiveTriggers == Support.Yes;

    public string ConnectionModes => Get("playstation connection modes");

    public bool WiredOnly =>
        ConnectionModes.Contains("wired", StringComparison.OrdinalIgnoreCase) &&
        !ConnectionModes.Contains("wireless", StringComparison.OrdinalIgnoreCase) &&
        !ConnectionModes.Contains("bluetooth", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, string> ParseInputTemplate(string wikitext)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int start = wikitext.IndexOf("{{Input", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return fields;
        foreach (var raw in wikitext[start..].Split('\n').Skip(1))
        {
            string line = raw.Trim();
            if (line.StartsWith("}}")) break;
            var m = Regex.Match(line, @"^\|\s*([^=]+?)\s*=\s*(.*)$");
            if (!m.Success) continue;
            string value = Regex.Replace(m.Groups[2].Value, @"<!--.*?-->|<ref.*?(/>|</ref>)", "").Trim();
            fields[m.Groups[1].Value.Trim()] = value;
        }
        return fields;
    }
}

sealed class PcgwClient
{
    const string Api = "https://www.pcgamingwiki.com/w/api.php";
    static readonly TimeSpan CacheAge = TimeSpan.FromDays(30);

    readonly HttpClient _http;
    readonly HttpClient _noRedirect;
    readonly string _cachePath;
    readonly ConcurrentDictionary<string, PcgwInfo?> _cache;
    readonly object _saveLock = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public PcgwClient(string cachePath)
    {
        _cachePath = cachePath;
        _http = Create(true);
        _noRedirect = Create(false);
        _cache = Load();
    }

    static HttpClient Create(bool redirect)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirect }) { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZerkSense5/0.3 (DualSense bridge; game controller feature lookup)");
        return h;
    }

    ConcurrentDictionary<string, PcgwInfo?> Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, PcgwInfo?>>(File.ReadAllText(_cachePath), Json);
                if (d != null) return new ConcurrentDictionary<string, PcgwInfo?>(d);
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        return new();
    }

    void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache, Json));
            }
            catch (IOException)
            {
            }
        }
    }

    public PcgwInfo? Cached(GameInfo game) => Cached(game.Key);
    public PcgwInfo? Cached(string key) => _cache.TryGetValue(key, out var i) ? i : null;

    public async Task<PcgwInfo?> LookupAsync(GameInfo game, bool refresh = false)
    {
        if (!refresh && _cache.TryGetValue(game.Key, out var hit) && hit != null && DateTime.UtcNow - hit.Fetched < CacheAge)
            return hit;

        try
        {
            string? page = null;
            if (game.SteamAppId is int appId and > 0)
            {
                page = await PageFromSteamAsync(appId);
                if (page != null && !Plausible(page, game.Name))
                {
                    var exact = await PageFromSearchAsync(game.Name, exactOnly: true);
                    if (exact == null) Log.Info($"PCGamingWiki: ignoring unrelated page \"{page}\" for \"{game.Name}\"");
                    page = exact;
                    if (page == null) return null;
                }
            }
            page ??= await PageFromSearchAsync(game.Name);
            if (page == null)
            {
                Log.Info($"PCGamingWiki: no page found for \"{game.Name}\"");
                return null;
            }

            var info = await FetchAsync(page);
            if (info != null && !Plausible(info.Page, game.Name))
            {
                Log.Info($"PCGamingWiki: ignoring unrelated page \"{info.Page}\" for \"{game.Name}\"");
                return null;
            }
            if (info != null)
            {
                _cache[game.Key] = info;
                Save();
                Log.Info($"PCGamingWiki: loaded \"{info.Page}\"");
            }
            return info;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn($"PCGamingWiki lookup failed for \"{game.Name}\": {e.Message}");
            return _cache.TryGetValue(game.Key, out var stale) ? stale : null;
        }
    }

    async Task<string?> PageFromSteamAsync(int appId)
    {
        using var r = await _noRedirect.GetAsync($"https://www.pcgamingwiki.com/api/appid.php?appid={appId}");
        if (r.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.Found)) return null;
        var loc = r.Headers.Location?.ToString();
        if (loc == null) return null;
        int i = loc.IndexOf("/wiki/", StringComparison.Ordinal);
        if (i < 0) return null;
        string page = Uri.UnescapeDataString(loc[(i + 6)..]).Replace('_', ' ');
        return page.Length == 0 || page.StartsWith("Special:") ? null : page;
    }

    async Task<string?> PageFromSearchAsync(string name, bool exactOnly = false)
    {
        string clean = Regex.Replace(name, @"[™®©]", "").Trim();
        string want = Normalize(clean);
        var titles = await OpenSearchAsync(clean);
        var exact = titles.FirstOrDefault(t => Normalize(t) == want);
        if (exact == null)
        {
            titles.AddRange(await FullTextSearchAsync(clean));
            exact = titles.FirstOrDefault(t => Normalize(t) == want);
        }
        if (exact != null || exactOnly) return exact;
        return titles.FirstOrDefault(t => Plausible(t, clean) && want.Length >= 4);
    }

    async Task<List<string>> OpenSearchAsync(string q)
    {
        var json = await _http.GetStringAsync($"{Api}?action=opensearch&format=json&limit=5&namespace=0&search={Uri.EscapeDataString(q)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[1].EnumerateArray().Select(e => e.GetString() ?? "").ToList();
    }

    async Task<List<string>> FullTextSearchAsync(string q)
    {
        var json = await _http.GetStringAsync($"{Api}?action=query&list=search&format=json&srlimit=5&srnamespace=0&srsearch={Uri.EscapeDataString(q)}");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query)) return new();
        return query.GetProperty("search").EnumerateArray().Select(e => e.GetProperty("title").GetString() ?? "").ToList();
    }

    static readonly Dictionary<string, string> Roman = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
    };

    static string Normalize(string s)
    {
        string words = Regex.Replace(s.ToLowerInvariant(), @"\b(ii|iii|iv|v|vi|vii|viii|ix)\b", m => Roman[m.Value]);
        return Regex.Replace(words, @"[^a-z0-9]", "");
    }

    static bool Plausible(string page, string game)
    {
        string p = Normalize(Regex.Replace(page, @"\s*\(.*?\)", "")), g = Normalize(game);
        return p == g || g.StartsWith(p) || (p.StartsWith(g) && p.Length - g.Length <= 3);
    }

    async Task<PcgwInfo?> FetchAsync(string page)
    {
        var json = await _http.GetStringAsync($"{Api}?action=parse&format=json&redirects=1&prop=wikitext&page={Uri.EscapeDataString(page)}");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("parse", out var parse)) return null;
        string title = parse.GetProperty("title").GetString() ?? page;
        string text = parse.GetProperty("wikitext").GetProperty("*").GetString() ?? "";
        return new PcgwInfo { Page = title, Fetched = DateTime.UtcNow, Fields = PcgwInfo.ParseInputTemplate(text) };
    }
}
