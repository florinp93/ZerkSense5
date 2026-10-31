using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZerkSense5.UI;

static class GameArt
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, nint[] icons, uint[]? ids, uint count, uint flags);

    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZerkSense5", "art");
    static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new();
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZerkSense5/0.6 (DualSense bridge)");
        return h;
    }

    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..16];

    public static Task<ImageSource?> IconAsync(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return Task.FromResult<ImageSource?>(null);
        return Cache.GetOrAdd("icon:" + exe, _ => Task.Run(() => LoadIcon(exe)));
    }

    public static Task<ImageSource?> CoverAsync(int? steamAppId, string? pcgwUrl) =>
        Cache.GetOrAdd($"cover:{steamAppId}:{pcgwUrl}", _ => LoadBestCoverAsync(steamAppId, pcgwUrl));

    static async Task<ImageSource?> LoadBestCoverAsync(int? steamAppId, string? pcgwUrl)
    {
        if (steamAppId is int id)
        {
            if (SteamLibraryCover(id) is { } local && FromFile(local) is { } image) return image;
            if (await LoadCoverAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900.jpg") is { } cdn) return cdn;
        }
        return string.IsNullOrEmpty(pcgwUrl) ? null : await LoadCoverAsync(pcgwUrl);
    }

    static string? SteamLibraryCover(int appId)
    {
        string? steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam == null) return null;
        var cache = Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache");
        foreach (var candidate in new[] { Path.Combine(cache, appId.ToString(), "library_600x900.jpg"), Path.Combine(cache, $"{appId}_library_600x900.jpg") })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    static ImageSource? LoadIcon(string exe)
    {
        string file = Path.Combine(Dir, "icon-" + Hash(exe) + ".png");
        if (File.Exists(file)) return FromFile(file);
        if (!File.Exists(exe)) return null;

        var handles = new nint[1];
        if (PrivateExtractIcons(exe, 0, 64, 64, handles, null, 1, 0) == 0 || handles[0] == 0) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
            Directory.CreateDirectory(Dir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(file)) encoder.Save(stream);
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is IOException or COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    static async Task<ImageSource?> LoadCoverAsync(string url)
    {
        string file = Path.Combine(Dir, "cover-" + Hash(url) + Path.GetExtension(new Uri(url).AbsolutePath));
        try
        {
            if (!File.Exists(file))
            {
                var bytes = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(Dir);
                await File.WriteAllBytesAsync(file, bytes);
            }
            return FromFile(file);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    static ImageSource? FromFile(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }
}
