using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZerkSense5;

sealed record Dependency(string Name, string Repo, string AssetPattern, string Purpose, string Page);

static class DependencyInstaller
{
    public static readonly Dependency Usbip = new(
        "usbip-win2", "vadimgrn/usbip-win2", @"^USBip-[\d.]+-x64\.exe$",
        "Adds the virtual USB port that lets games see a wired DualSense or an Xbox 360 controller. Needed for haptics, speaker, mic and triggers.",
        "https://github.com/vadimgrn/usbip-win2/releases");

    public static readonly Dependency HidHide = new(
        "HidHide", "nefarius/HidHide", @"^HidHide_[\d.]+_x64\.exe$",
        "Hides the Bluetooth controller from games so they only see the virtual one. Recommended; needs a restart after installing.",
        "https://github.com/nefarius/HidHide/releases");

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ZerkSense5 (dependency installer)");
        return h;
    }

    public static async Task InstallAsync(Dependency dep, IProgress<string> status)
    {
        status.Report("Looking up the latest release…");
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{dep.Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => Regex.IsMatch(a.GetProperty("name").GetString() ?? "", dep.AssetPattern, RegexOptions.IgnoreCase));
        if (asset.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("No Windows x64 installer in the latest release.");

        string name = asset.GetProperty("name").GetString()!;
        string url = asset.GetProperty("browser_download_url").GetString()!;
        string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

        string dir = Path.Combine(Path.GetTempPath(), "ZerkSense5");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, name);

        status.Report($"Downloading {name}…");
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int read, lastPercent = -1;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != lastPercent && percent % 5 == 0)
                {
                    lastPercent = percent;
                    status.Report($"Downloading {name}… {percent}%");
                }
            }
        }

        status.Report("Checking the download…");
        if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(file);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidOperationException("The download doesn't match the checksum GitHub published for it.");
            }
        }
        if (!Authenticode.IsValid(file))
        {
            File.Delete(file);
            throw new InvalidOperationException("The installer isn't validly signed, so it wasn't run.");
        }

        status.Report($"Running the {dep.Name} installer…");
        using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })
                            ?? throw new InvalidOperationException("The installer didn't start.");
        await process.WaitForExitAsync();
        status.Report(process.ExitCode == 0 ? "Installed" : $"The installer ended with code {process.ExitCode}");
    }
}

static class Authenticode
{
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WINTRUST_DATA data);

    static Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        public string pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public int cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public nint pFile;
        public int dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
        public nint pSignatureSettings;
    }

    public static bool IsValid(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        nint pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,
                dwUnionChoice = 1,
                pFile = pFile,
                dwStateAction = 1,
            };
            int result = WinVerifyTrust(-1, ref GenericVerifyV2, ref data);
            data.dwStateAction = 2;
            WinVerifyTrust(-1, ref GenericVerifyV2, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
