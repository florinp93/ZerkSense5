using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;

namespace ZerkSense5.UI;

partial class UpdateWindow : Window, INotifyPropertyChanged
{
    readonly Config _cfg;
    readonly UpdateInfo _update;
    bool _working;
    string _status = "";

    internal UpdateWindow(Config cfg, UpdateInfo update)
    {
        _cfg = cfg;
        _update = update;
        DataContext = this;
        InitializeComponent();
        SourceInitialized += (_, _) => MainWindow.DarkTitleBar(this);
        Closing += (_, e) => e.Cancel = _working;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Installing { get; private set; }

    public string Heading => $"Zerk Sense 5 {_update.Version} is available";
    public string Subheading => $"You have {Updater.Current}. The update is downloaded from GitHub, checked against its published checksum, and the app restarts on the new version.";
    public string Notes => Summarize(_update.Notes);
    public bool CanAct => !_working;

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Notify();
        }
    }

    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    static string Summarize(string markdown)
    {
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('|')) continue;
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            line = Regex.Replace(line, @"^#+\s*", "");
            line = Regex.Replace(line, @"^[-*]\s+", "• ");
            line = line.Replace("**", "").Replace("`", "");
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        _working = true;
        Notify(nameof(CanAct));
        try
        {
            string exe = await Updater.DownloadAsync(_update, new Progress<string>(s => Status = s));
            Status = "Restarting";
            Updater.Install(exe);
            Log.Info($"Installing {_update.Version} and restarting");
            Installing = true;
            _working = false;
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = "Update failed: " + ex.Message;
            Log.Warn(Status);
            _working = false;
            Notify(nameof(CanAct));
        }
    }

    void Later_Click(object sender, RoutedEventArgs e) => Close();

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        _cfg.SkippedVersion = _update.Version.ToString();
        _cfg.Save();
        Close();
    }
}
