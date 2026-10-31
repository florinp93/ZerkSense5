using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZerkSense5.Games;

sealed record WikiLink(string Label, string Url)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : Url;
}

static class WikiText
{
    static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "ii", "mm", "++", "--", "+-", "~", "cn", "citation needed", "refurl", "refcheck", "refsnip", "note", "dead link",
        "fixbox/fix", "clarify", "needs update", "verify",
    };

    public static string? Section(string wikitext, string anchor)
    {
        string name = Regex.Escape(anchor.Replace('_', ' ').Trim());
        var heading = Regex.Match(wikitext, @"(?m)^(={2,6})\s*" + name + @"\s*\1\s*$", RegexOptions.IgnoreCase);
        if (!heading.Success) return null;
        int level = heading.Groups[1].Value.Length;
        var next = Regex.Match(wikitext[(heading.Index + heading.Length)..], @"(?m)^={2," + level + @"}[^=]");
        int end = next.Success ? heading.Index + heading.Length + next.Index : wikitext.Length;
        return wikitext[(heading.Index + heading.Length)..end].Trim();
    }

    public static (string Text, List<WikiLink> Links) Render(string wiki)
    {
        var links = new List<WikiLink>();
        string s = Regex.Replace(wiki, @"<!--.*?-->", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<ref[^>]*/>|<ref[^>]*>.*?</ref>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = ExpandTemplates(s);

        s = Regex.Replace(s, @"\[\[(?:File|Image):[^\]]*\]\]", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[\[([^|\]]*)\|([^\]]*)\]\]", m => m.Groups[2].Value);
        s = Regex.Replace(s, @"\[\[#?([^\]]*)\]\]", m => m.Groups[1].Value);
        s = Regex.Replace(s, @"\[(https?://[^\s\]]+)(?:\s+([^\]]*))?\]", m =>
        {
            string url = m.Groups[1].Value, label = m.Groups[2].Value.Trim();
            if (links.All(l => l.Url != url)) links.Add(new WikiLink(label.Length > 0 ? StripTags(label) : url, url));
            return label.Length > 0 ? label : url;
        });

        s = Regex.Replace(s, @"'{2,5}", "");
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = StripTags(s);
        s = WebUtility.HtmlDecode(s);

        var lines = new List<string>();
        int step = 0;
        foreach (var raw in s.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                step = 0;
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                continue;
            }
            if (line.StartsWith('#'))
            {
                line = $"{++step}. {line.TrimStart('#', ' ')}";
            }
            else if (line.StartsWith('*'))
            {
                line = "• " + line.TrimStart('*', ' ');
            }
            else
            {
                step = 0;
                line = line.TrimStart(':', ' ');
            }
            if (line is "•" or "") continue;
            lines.Add(Regex.Replace(line, @"\s{2,}", " "));
        }
        return (string.Join("\n", lines).Trim(), links);
    }

    static string StripTags(string s) => Regex.Replace(s, @"</?[a-zA-Z][^>]*>", "");

    static string ExpandTemplates(string s)
    {
        var inner = new Regex(@"\{\{([^{}]*)\}\}");
        for (int guard = 0; guard < 50 && inner.IsMatch(s); guard++)
            s = inner.Replace(s, m => Expand(m.Groups[1].Value));
        return s;
    }

    static string Expand(string body)
    {
        var parts = SplitArgs(body);
        string name = parts[0].Trim();
        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Skip(1))
        {
            var m = Regex.Match(p, @"^\s*([A-Za-z0-9 _-]+?)\s*=(.*)$", RegexOptions.Singleline);
            if (m.Success) named[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            else positional.Add(p.Trim());
        }

        switch (name.ToLowerInvariant())
        {
            case "file" or "folder" or "code" or "key" or "term" or "keypress":
                return positional.Count > 0 ? string.Join("+", positional) : "";
            case "p":
                return positional.Count > 0 ? PathPlaceholder(positional[0]) : "";
            case "fixbox":
            {
                var sb = new StringBuilder();
                if (named.TryGetValue("description", out var d) && d.Length > 0) sb.Append(d).Append('\n');
                if (named.TryGetValue("fix", out var f) && f.Length > 0) sb.Append(f).Append('\n');
                return "\n" + sb + "\n";
            }
        }
        if (Dropped.Contains(name)) return "";
        return positional.Count == 1 && !name.Contains(' ') ? positional[0] : "";
    }

    static string PathPlaceholder(string name) => name.ToLowerInvariant() switch
    {
        "game" => "[game folder]",
        "steam" => "[Steam folder]",
        "uid" => "[user ID]",
        "appdata" => "%APPDATA%",
        "localappdata" => "%LOCALAPPDATA%",
        "userprofile" => "%USERPROFILE%",
        "userprofile\\documents" or "documents" => "%USERPROFILE%\\Documents",
        "programfiles" => "%PROGRAMFILES%",
        "hkcu" => "HKEY_CURRENT_USER",
        "hklm" => "HKEY_LOCAL_MACHINE",
        var other => $"[{other}]",
    };

    static List<string> SplitArgs(string body)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '[') depth++;
            else if (c == ']') depth = Math.Max(0, depth - 1);
            if (c == '|' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }
}
