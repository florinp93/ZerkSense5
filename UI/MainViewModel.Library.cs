using System.Collections.ObjectModel;
using System.Windows.Input;
using ZerkSense5.Games;

namespace ZerkSense5.UI;

sealed record GameFolderItem(string Path, bool Auto)
{
    public string Detail => Auto ? "Found automatically" : "Added by you";
}

partial class MainViewModel
{
    public ObservableCollection<GameFolderItem> GameFolders { get; } = new();
    public ICommand AddGameFolder { get; private set; } = null!;
    public ICommand RemoveGameFolder { get; private set; } = null!;
    public ICommand RescanGames { get; private set; } = null!;
    public ICommand UnhideGames { get; private set; } = null!;

    void InitLibrary()
    {
        AddGameFolder = new Relay(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder that contains your games" };
            if (dlg.ShowDialog() != true) return;
            lock (_cfg.GameFolders)
                if (!_cfg.GameFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) _cfg.GameFolders.Add(dlg.FolderName);
            LibraryEdited();
        });
        RemoveGameFolder = new Relay(p =>
        {
            if (p is not GameFolderItem { Auto: false } item) return;
            lock (_cfg.GameFolders) _cfg.GameFolders.RemoveAll(f => string.Equals(f, item.Path, StringComparison.OrdinalIgnoreCase));
            LibraryEdited();
        });
        RescanGames = new Relay(_ => _games.RescanLibrary());
        UnhideGames = new Relay(_ =>
        {
            _games.UnhideAll();
            Notify(nameof(HiddenGamesText));
            Notify(nameof(HasHiddenGames));
        });
        RefreshFolders();
    }

    void LibraryEdited()
    {
        _cfg.Save();
        RefreshFolders();
        _games.RescanLibrary();
    }

    void RefreshFolders()
    {
        GameFolders.Clear();
        foreach (var f in GameLibrary.AutoGameFolders()) GameFolders.Add(new GameFolderItem(f, true));
        lock (_cfg.GameFolders)
            foreach (var f in _cfg.GameFolders.Where(f => GameFolders.All(g => !string.Equals(g.Path, f, StringComparison.OrdinalIgnoreCase))))
                GameFolders.Add(new GameFolderItem(f, false));
        Notify(nameof(HasGameFolders));
    }

    public bool HasGameFolders => GameFolders.Count > 0;

    public bool HasHiddenGames
    {
        get
        {
            lock (_cfg.IgnoredGames) return _cfg.IgnoredGames.Count > 0;
        }
    }

    public string HiddenGamesText
    {
        get
        {
            int n;
            lock (_cfg.IgnoredGames) n = _cfg.IgnoredGames.Count;
            return n == 1 ? "1 app is marked as not a game." : $"{n} apps are marked as not a game.";
        }
    }
}
