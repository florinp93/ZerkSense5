using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZerkSense5;

sealed record SemVersion(int Major, int Minor, int Patch, string[] Pre) : IComparable<SemVersion>
{
    public bool IsPrerelease => Pre.Length > 0;

    public static SemVersion? Parse(string? text)
    {
        if (text == null) return null;
        var m = Regex.Match(text.Trim(), @"^v?\.?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$");
        if (!m.Success) return null;
        return new SemVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            m.Groups[4].Success ? m.Groups[4].Value.Split('.') : []);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other == null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre.Length == 0 || other.Pre.Length == 0) return other.Pre.Length.CompareTo(Pre.Length);
        for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            bool aNum = int.TryParse(Pre[i], out int a), bNum = int.TryParse(other.Pre[i], out int b);
            c = aNum && bNum ? a.CompareTo(b) : aNum ? -1 : bNum ? 1 : string.CompareOrdinal(Pre[i], other.Pre[i]);
            if (c != 0) return c;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Length > 0 ? "-" + string.Join('.', Pre) : "");
}

sealed record UpdateInfo(SemVersion Version, string Tag, string Notes, string PageUrl, string? AssetUrl, string? AssetName, string? Digest, string? SumsUrl);

static class Updater
{
    public const string Repo = "florinp93/ZerkSense5";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZerkSense5-updater");
        return h;
    }

    public static SemVersion Current { get; } =
        SemVersion.Parse(typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new SemVersion(0, 0, 0, []);

    static bool SingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "ZerkSense5.dll"));

    public static bool CanInstall => !string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    public static string AssetName(SemVersion v) => SingleFile ? $"ZerkSense5-{v}-win-x64.zip" : $"ZerkSense5-{v}-win-x64-framework-dependent.zip";

    public static async Task<UpdateInfo?> CheckAsync(bool includePrereleases, SemVersion? newerThan = null)
    {
        var baseline = newerThan ?? Current;
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
        using var doc = JsonDocument.Parse(json);
        UpdateInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            if (r.GetProperty("prerelease").GetBoolean() && !includePrereleases) continue;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            if (SemVersion.Parse(tag) is not { } version || !(version > baseline) || best != null && !(version > best.Version)) continue;

            string wanted = AssetName(version);
            string? url = null, digest = null, sums = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = a.GetProperty("browser_download_url").GetString();
                }
            }
            best = new UpdateInfo(version, tag, r.GetProperty("body").GetString() ?? "", r.GetProperty("html_url").GetString() ?? "",
                url, url == null ? null : wanted, digest, sums);
        }
        return best;
    }

    public static void Install(string newExe)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Process.Start(new ProcessStartInfo(newExe, $"--apply-update \"{installDir}\" {Environment.ProcessId}") { UseShellExecute = true });
    }

    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<string> status)
    {
        if (update.AssetUrl == null || update.AssetName == null) throw new InvalidOperationException("This release has no package for this build.");
        string work = Path.Combine(Path.GetTempPath(), "ZerkSense5", "update", update.Version.ToString());
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, update.AssetName);

        using (var response = await Http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read, last = -1;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != last)
                {
                    last = percent;
                    status.Report($"Downloading {percent}%");
                }
            }
        }

        status.Report("Checking the download");
        string expected = await ExpectedHashAsync(update) ?? throw new InvalidOperationException("The release has no checksum, so the update wasn't installed.");
        await using (var stream = File.OpenRead(zip))
        {
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The download doesn't match its published checksum.");
        }

        status.Report("Unpacking");
        string staging = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zip, staging);
        string newExe = Directory.EnumerateFiles(staging, "ZerkSense5.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new InvalidOperationException("The package doesn't contain ZerkSense5.exe.");

        return newExe;
    }

    static async Task<string?> ExpectedHashAsync(UpdateInfo update)
    {
        if (update.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return d[7..];
        if (update.SumsUrl == null) return null;
        var sums = await Http.GetStringAsync(update.SumsUrl);
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim().Equals(update.AssetName, StringComparison.OrdinalIgnoreCase)) return parts[0];
        }
        return null;
    }

    public static int Apply(string installDir, int oldPid)
    {
        try
        {
            using var old = Process.GetProcessById(oldPid);
            old.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
        }

        string source = AppContext.BaseDirectory.TrimEnd('\\');
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.Equals("config.json", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(installDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(500);
                }
            }
        }
        Process.Start(new ProcessStartInfo(Path.Combine(installDir, "ZerkSense5.exe")) { UseShellExecute = true });
        return 0;
    }
}
