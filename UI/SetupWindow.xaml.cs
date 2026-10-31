using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ZerkSense5.UI;

public sealed class SetupItem : INotifyPropertyChanged
{
    readonly Func<bool> _installed;
    bool _selected;
    bool _busy;
    string _status = "";

    internal SetupItem(Dependency dependency, Func<bool> installed)
    {
        Dependency = dependency;
        _installed = installed;
        _selected = !installed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Dependency Dependency { get; }
    public string Name => Dependency.Name;
    public string Purpose => Dependency.Purpose;
    public bool Installed => _installed();
    public bool CanSelect => !Installed && !_busy;
    public string StateText => Installed ? "Installed" : "Not installed";
    public Brush StateBrush => (Brush)Application.Current.Resources[Installed ? "Success" : "Warning"];
    public bool HasStatus => _status.Length > 0;

    public bool Selected
    {
        get => _selected && !Installed;
        set
        {
            _selected = value;
            Notify();
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            Refresh();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            Notify();
            Notify(nameof(HasStatus));
        }
    }

    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Refresh()
    {
        foreach (var p in new[] { nameof(Installed), nameof(CanSelect), nameof(StateText), nameof(StateBrush), nameof(Selected) }) Notify(p);
    }
}

partial class SetupWindow : Window, INotifyPropertyChanged
{
    readonly Config _cfg;
    bool _working;

    internal SetupWindow(Config cfg)
    {
        _cfg = cfg;
        Items =
        [
            new SetupItem(DependencyInstaller.Usbip, () => ZerkSense5.Usbip.Locate(cfg) != null),
            new SetupItem(DependencyInstaller.HidHide, () => ZerkSense5.HidHide.Locate() != null),
        ];
        foreach (var item in Items) item.PropertyChanged += (_, _) => Notify(nameof(CanInstall));
        DataContext = this;
        InitializeComponent();
        SourceInitialized += (_, _) => MainWindow.DarkTitleBar(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public List<SetupItem> Items { get; }
    public bool CanInstall => !_working && Items.Any(i => i.Selected);
    public string CloseText => Items.All(i => i.Installed) ? "Done" : "Not now";

    public bool DontAskAgain
    {
        get => _cfg.SkipSetupPrompt;
        set
        {
            _cfg.SkipSetupPrompt = value;
            _cfg.Save();
        }
    }

    public string Footer => Items.Any(i => i.Dependency == DependencyInstaller.HidHide && i.Installed) && _installedThisSession
        ? "Restart Windows to finish installing HidHide."
        : "You can also install them later from the Overview page.";

    bool _installedThisSession;

    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static bool AnyMissing(Config cfg) => ZerkSense5.Usbip.Locate(cfg) == null || ZerkSense5.HidHide.Locate() == null;

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        _working = true;
        Notify(nameof(CanInstall));
        foreach (var item in Items.Where(i => i.Selected).ToList())
        {
            item.Busy = true;
            try
            {
                await DependencyInstaller.InstallAsync(item.Dependency, new Progress<string>(s => item.Status = s));
                if (item.Installed)
                {
                    item.Status = "Installed";
                    _installedThisSession = true;
                    Log.Info($"{item.Name} installed");
                }
                else if (!item.Status.StartsWith("The installer ended"))
                {
                    item.Status = "The installer closed without installing.";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                item.Status = "Couldn't install: " + ex.Message;
                Log.Warn($"{item.Name} install failed: {ex.Message}");
            }
            item.Busy = false;
        }
        _working = false;
        foreach (var p in new[] { nameof(CanInstall), nameof(CloseText), nameof(Footer) }) Notify(p);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
