using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZerkSense5.Games;

enum Support { Unknown, Yes, No, Limited, Modded, NotApplicable }

sealed record GameRef(string Key, string Name, int? SteamAppId, string? PageOverride, string[]? Aliases = null);

sealed record PromptMod(string Text, IReadOnlyList<WikiLink> Links);

sealed class PcgwInfo
{
    public const int CurrentVersion = 2;

    public int Version { get; set; }
    public string Page { get; set; } = "";
    public DateTime Fetched { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();
    public string? CoverUrl { get; set; }
    public string PromptModWiki { get; set; } = "";

    [JsonIgnore] public string Url => "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(Page.Replace(' ', '_'));

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

    PromptMod? _promptMod;

    [JsonIgnore]
    public PromptMod? PromptMod
    {
        get
        {
            if (_promptMod != null || PromptModWiki.Length == 0) return _promptMod;
            var (text, links) = WikiText.Render(PromptModWiki);
            if (links.Count == 0 && PlayStationPrompts != Support.Modded) return null;
            if (text.Length == 0 && links.Count == 0) return null;
            return _promptMod = new PromptMod(text, links);
        }
    }

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

    public static string ExtractPromptMod(string wikitext, Dictionary<string, string> fields)
    {
        var parts = new List<string>();
        string notes = fields.TryGetValue("playstation prompts notes", out var n) ? n.Trim() : "";
        if (notes.Length > 0)
        {
            parts.Add(notes);
            foreach (Match m in Regex.Matches(notes, @"\[\[#([^|\]]+)"))
                if (WikiText.Section(wikitext, m.Groups[1].Value) is { } section) parts.Add(section);
        }

        var promptLine = new Regex(@"prompt", RegexOptions.IgnoreCase);
        var playstation = new Regex(@"PlayStation|DualShock|DualSense|\bPS[345]\b", RegexOptions.IgnoreCase);
        foreach (var line in wikitext.Split('\n'))
        {
            if (parts.Count >= 4) break;
            if (!line.Contains("[http") || !promptLine.IsMatch(line) || !playstation.IsMatch(line)) continue;
            if (line.TrimStart().StartsWith('|') || parts.Any(p => p.Contains(line.Trim()))) continue;
            parts.Add(line.Trim());
        }
        return string.Join("\n\n", parts);
    }
}

sealed class PcgwClient
{
    const string Api = "https://www.pcgamingwiki.com/w/api.php";
    public const string NoPage = "-";
    static readonly TimeSpan CacheAge = TimeSpan.FromDays(30);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    static readonly Dictionary<string, string> Roman = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
    };

    readonly HttpClient _http;
    readonly HttpClient _noRedirect;
    readonly string _cachePath;
    readonly ConcurrentDictionary<string, PcgwInfo?> _cache;
    readonly object _saveLock = new();

    public PcgwClient(string cachePath)
    {
        _cachePath = cachePath;
        _http = Create(true);
        _noRedirect = Create(false);
        _cache = Load();
    }

    public HttpClient Http => _http;

    static HttpClient Create(bool redirect)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirect }) { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZerkSense5/0.6 (DualSense bridge; game controller feature lookup)");
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

    public PcgwInfo? Cached(string key) => _cache.TryGetValue(key, out var i) ? i : null;

    public void Forget(string key)
    {
        _cache.TryRemove(key, out _);
        Save();
    }

    public async Task<PcgwInfo?> LookupAsync(GameRef game, bool refresh = false)
    {
        if (game.PageOverride == NoPage) return null;
        if (!refresh && _cache.TryGetValue(game.Key, out var hit) && hit != null && hit.Version == PcgwInfo.CurrentVersion &&
            DateTime.UtcNow - hit.Fetched < CacheAge && (game.PageOverride == null || hit.Page == game.PageOverride))
            return hit;

        try
        {
            string? page = game.PageOverride ?? await FindPageAsync(game);
            if (page == null)
            {
                Log.Info($"PCGamingWiki: no page found for \"{game.Name}\"");
                return null;
            }

            var info = await FetchAsync(page);
            var names = info == null || game.PageOverride != null ? null : await CandidateNamesAsync(game);
            if (info != null && names != null && !names.Any(n => Plausible(info.Page, n) || Similarity(info.Page, n) >= 0.75))
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

    static readonly Regex EditionSuffix = new(
        @"\s*[-:–]?\s*\(?(game of the year|goty|definitive|deluxe|digital deluxe|ultimate|complete|gold|premium|standard|enhanced|anniversary|special|collector'?s|legendary|royal)( edition)?\)?\s*$",
        RegexOptions.IgnoreCase);

    async Task<List<string>> CandidateNamesAsync(GameRef game)
    {
        var names = new List<string>();
        if (game.SteamAppId is int appId and > 0 && await SteamNameAsync(appId) is { } steamName) names.Add(steamName);
        names.Add(game.Name);
        if (game.Aliases != null) names.AddRange(game.Aliases);
        foreach (var n in names.ToList())
        {
            string trimmed = EditionSuffix.Replace(Regex.Replace(n, @"[™®©]", ""), "").Trim();
            if (trimmed.Length > 1) names.Add(trimmed);
        }
        return names.Select(n => Regex.Replace(n, @"[™®©]", "").Trim())
            .Where(n => n.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    async Task<string?> FindPageAsync(GameRef game)
    {
        var names = await CandidateNamesAsync(game);
        if (game.SteamAppId is int appId and > 0 && await PageFromSteamAsync(appId) is { } fromSteam && names.Any(n => Plausible(fromSteam, n)))
            return fromSteam;

        var seen = new List<string>();
        foreach (var name in names)
        {
            var titles = await OpenSearchAsync(name, 8);
            var exact = titles.FirstOrDefault(t => Normalize(t) == Normalize(name));
            if (exact == null)
            {
                titles.AddRange(await FullTextSearchAsync(name, 8));
                exact = titles.FirstOrDefault(t => Normalize(t) == Normalize(name));
            }
            if (exact != null) return exact;
            seen.AddRange(titles);
        }

        var best = seen.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (title: t, score: names.Max(n => Similarity(t, n))))
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        return best.score >= 0.75 ? best.title : null;
    }

    readonly ConcurrentDictionary<int, string?> _steamNames = new();

    async Task<string?> SteamNameAsync(int appId)
    {
        if (_steamNames.TryGetValue(appId, out var cached)) return cached;
        string? name = null;
        try
        {
            var json = await _http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var app) && app.GetProperty("success").GetBoolean())
                name = app.GetProperty("data").GetProperty("name").GetString();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        _steamNames[appId] = name;
        return name;
    }

    static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) { "the", "of", "a", "an", "and", "edition", "game" };

    static HashSet<string> Tokens(string s) =>
        Regex.Split(Regex.Replace(s.ToLowerInvariant().Replace("'", ""), @"\b(ii|iii|iv|v|vi|vii|viii|ix)\b", m => Roman[m.Value]), @"[^a-z0-9]+")
            .Where(t => t.Length > 0 && !StopWords.Contains(t))
            .ToHashSet();

    public static double Similarity(string title, string name)
    {
        string bare = Regex.Replace(title, @"\s*\(.*?\)", "");
        if (Normalize(bare) == Normalize(name)) return 1;
        var a = Tokens(bare);
        var b = Tokens(name);
        if (a.Count == 0 || b.Count == 0) return 0;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }

    public async Task<List<string>> SearchAsync(string query)
    {
        string clean = Regex.Replace(query, @"[™®©]", "").Trim();
        if (clean.Length == 0) return new();
        try
        {
            var titles = await OpenSearchAsync(clean, 10);
            titles.AddRange(await FullTextSearchAsync(clean, 10));
            return titles.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn($"PCGamingWiki search failed: {e.Message}");
            return new();
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

    async Task<List<string>> OpenSearchAsync(string q, int limit)
    {
        var json = await _http.GetStringAsync($"{Api}?action=opensearch&format=json&limit={limit}&namespace=0&search={Uri.EscapeDataString(q)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[1].EnumerateArray().Select(e => e.GetString() ?? "").ToList();
    }

    async Task<List<string>> FullTextSearchAsync(string q, int limit)
    {
        var json = await _http.GetStringAsync($"{Api}?action=query&list=search&format=json&srlimit={limit}&srnamespace=0&srsearch={Uri.EscapeDataString(q)}");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query)) return new();
        return query.GetProperty("search").EnumerateArray().Select(e => e.GetProperty("title").GetString() ?? "").ToList();
    }

    static string Normalize(string s)
    {
        string words = Regex.Replace(s.ToLowerInvariant().Replace("'", ""), @"\b(ii|iii|iv|v|vi|vii|viii|ix)\b", m => Roman[m.Value]);
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
        var fields = PcgwInfo.ParseInputTemplate(text);
        return new PcgwInfo
        {
            Version = PcgwInfo.CurrentVersion,
            Page = title,
            Fetched = DateTime.UtcNow,
            Fields = fields,
            PromptModWiki = PcgwInfo.ExtractPromptMod(text, fields),
            CoverUrl = await CoverAsync(text),
        };
    }

    async Task<string?> CoverAsync(string wikitext)
    {
        var m = Regex.Match(wikitext, @"^\|\s*cover\s*=\s*(.+?)\s*$", RegexOptions.Multiline);
        if (!m.Success || m.Groups[1].Value.Length == 0) return null;
        try
        {
            var file = Uri.EscapeDataString("File:" + m.Groups[1].Value);
            var json = await _http.GetStringAsync($"{Api}?action=query&format=json&prop=imageinfo&iiprop=url&iiurlwidth=300&titles={file}");
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
                if (p.Value.TryGetProperty("imageinfo", out var ii) && ii.GetArrayLength() > 0)
                {
                    var info = ii[0];
                    return info.TryGetProperty("thumburl", out var t) ? t.GetString() : info.GetProperty("url").GetString();
                }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
        }
        return null;
    }
}
