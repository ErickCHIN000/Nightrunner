using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Nightrunner.Core.Backends;
using Nightrunner.Core.Games;

namespace Nightrunner.UI.Views;

/// <summary>One detected install, with what it holds. The accent is the game's colour.</summary>
public sealed class GameCard(GameInstall install) : INotifyPropertyChanged
{
    public GameInstall Install { get; } = install;
    public string Name => Install.Name;
    public string Id => Install.Id;
    public string Short => Install.Profile.Short;
    public string Root => Install.Root;
    public Brush Accent { get; } = GameAccent.BrushFor(install.Id);
    public bool Usable { get; private set; } = true;
    public bool Implemented { get; } = GameBackends.For(install.Profile).Implemented;

    private string _detail = "";

    /// <summary>Only says something when the game cannot be opened — the tool is not only about packs.</summary>
    public string Detail
    {
        get => _detail;
        private set
        {
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailVisibility)));
        }
    }

    public Visibility DetailVisibility => Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Decide whether the card can be clicked. No content is counted here.</summary>
    public void Check()
    {
        if (!Implemented)
        {
            Usable = false;
            Detail = "backend not implemented yet";
            return;
        }
        Usable = Install.IsValid;
        Detail = Usable ? "" : "install folder is incomplete — check it in Settings";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The first screen: which game are we working on.</summary>
public partial class GameSelectView : UserControl
{
    /// <summary>Raised when the user picks an install.</summary>
    public event Action<GameInstall>? GameChosen;

    /// <summary>Raised when the user opens loose packs instead of a game.</summary>
    public event Action<string[]>? PacksChosen;

    /// <summary>Raised when the user asks for the settings page (nothing was found, or a path needs fixing).</summary>
    public event Action? SettingsRequested;

    private readonly GameSettings _settings;
    private readonly List<GameCard> _cards = [];

    public GameSelectView(GameSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        _cards.Clear();
        var found = GameInstall.FindInstalls(savedRoots: _settings.Roots);
        foreach (var profile in GameProfile.All)
            if (found.TryGetValue(profile.Id, out var install))
            _cards.Add(new GameCard(install));
        Games.ItemsSource = null;
        Games.ItemsSource = _cards;
        Empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in _cards) c.Check();
    }

    private void Game_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is GameCard card) GameChosen?.Invoke(card.Install);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Game folder (or its data folder)" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var install = new GameInstall(dlg.FolderName);
        if (!install.IsValid)
        {
            Note.Text = $"no assets folder under {dlg.FolderName}";
            Note.Visibility = Visibility.Visible;
            return;
        }
        GameChosen?.Invoke(install);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OpenPacks_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open packs",
            Filter = "RPACK (*.rpack)|*.rpack|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) PacksChosen?.Invoke(dlg.FileNames);
    }
}
