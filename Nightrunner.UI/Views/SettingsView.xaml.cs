using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Nightrunner.Core.Backends;
using Nightrunner.Core.Games;

namespace Nightrunner.UI.Views;

/// <summary>One line of a validation result.</summary>
public sealed class CheckRow(PathCheck check)
{
    public string Label => check.Label;
    public string State => check.State;
    public string Path => check.Path ?? "not defined for this game";

    public Brush StateBrush => Skin.Brush(check.State switch
    {
        "ok" => "Fg",
        "missing" => "Error",
        _ => "FgFaint",
    });
}

/// <summary>One path of the installed runtime.</summary>
public sealed class RuntimePathRow(RuntimePath path)
{
    public string Label => path.Label;
    public string State => path.State;
    public string Path => path.Path ?? "";

    public Brush StateBrush => Skin.Brush(path.State switch
    {
        "ok" => "Fg",
        "missing" or "broken" => "Error",
        "foreign" => "Warn",
        _ => "FgFaint",
    });
}

/// <summary>One game's row in Settings: its root, what validating it found, and whether it can be opened.</summary>
public sealed class GameRow(GameProfile profile, GameSettings settings) : INotifyPropertyChanged
{
    private string _root = settings.Root(profile.Id) ?? "";
    private string _status = "";
    private string _statusKey = "FgDim";
    private List<CheckRow> _checks = [];
    private bool _canOpen;

    public GameProfile Profile { get; } = profile;
    public string Id => Profile.Id;
    public string Name => Profile.Name;
    public Brush Accent { get; } = GameAccent.BrushFor(profile.Id);

    public string Support => GameBackends.For(Profile).Implemented ? "" : "backend not implemented";
    public Brush SupportBrush => Skin.Brush("Warn");

    // ---- Nightrunner Runtime Modding --------------------------------------------------------------------

    /// <summary>Only shown for games whose content the runtime could load.</summary>
    public Visibility RuntimeVisibility =>
        GameBackends.For(Profile).Implemented ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the app shows what the runtime loads for this game (the one runtime setting Nightrunner keeps).</summary>
    public bool RuntimeEnabled
    {
        get => settings.RuntimeModdingFor(Id);
        set
        {
            settings.SetRuntimeModding(Id, value);
            settings.Save();
            Raise(nameof(RuntimeEnabled));
            Raise(nameof(RuntimeToggleText));
            ShowRuntime();
        }
    }

    public string RuntimeToggleText => RuntimeEnabled ? "on" : "off";

    /// <summary>NightrunnerProxy has a module for this game; without one the toggle and Validate stay disabled.</summary>
    public bool RuntimeAvailable => RuntimeModding.HasModule(Profile);

    private RuntimeReport? _runtime;
    private string _runtimeStatus = "";
    private string _runtimeMods = "";
    private List<RuntimePathRow> _runtimePaths = [];

    public string RuntimeName => _runtime?.Layout ?? "";

    public string RuntimeStatus
    {
        get => _runtimeStatus;
        private set { _runtimeStatus = value; Raise(nameof(RuntimeStatus)); }
    }

    /// <summary>What the mods folder holds: mods, how many are on, how many items load.</summary>
    public string RuntimeMods
    {
        get => _runtimeMods;
        private set { _runtimeMods = value; Raise(nameof(RuntimeMods)); }
    }

    public List<RuntimePathRow> RuntimePaths
    {
        get => _runtimePaths;
        private set { _runtimePaths = value; Raise(nameof(RuntimePaths)); }
    }

    /// <summary>Warn when the runtime is installed but hidden, shown but not installed, or its last start failed.</summary>
    public Brush RuntimeBrush => Skin.Brush(_runtime is null ? "FgDim"
        : _runtime.Check.Found != RuntimeEnabled || _runtime.Check.Boot is not null ? "Warn"
        : _runtime.Check.Found ? "Fg" : "FgDim");

    /// <summary>Look for the runtime next to this game's executable and switch the flag to what was found.</summary>
    public RuntimeCheck ValidateRuntime()
    {
        var check = DescribeRuntime();
        RuntimeEnabled = check.Found;
        return check;
    }

    /// <summary>Read what is installed for the runtime (the flag is left as it is). Reads only.</summary>
    public RuntimeCheck DescribeRuntime()
    {
        if (Install is null && !string.IsNullOrWhiteSpace(Root) && Directory.Exists(Root))
            Validate();
        var install = Install ?? new GameInstall(Root, Profile);
        _runtime = RuntimeModding.Describe(install);
        var check = _runtime.Check;
        RuntimePaths = _runtime.Paths.Select(p => new RuntimePathRow(p)).ToList();
        if (RuntimeAvailable && (check.Found || check.DllPath is not null))
        {
            var content = RuntimeContent.Read(install);
            int mods = content.Mods.Count, on = content.Mods.Count(m => m.Valid && m.Enabled), bad = content.Mods.Count(m => !m.Valid);
            int loads = content.Items.Count(i => i.Loads);
            RuntimeMods = (content.NoMods is { } none ? none + Environment.NewLine : "") +
                          $"{mods} mods, {on} on" + (bad > 0 ? $", {bad} invalid" : "") + $", {loads} items load" +
                          (content.Problems.Count > 0 ? $", {content.Problems.Count} problems" : "");
        }
        else RuntimeMods = "";
        ShowRuntime();
        return check;
    }

    private void ShowRuntime()
    {
        if (_runtime is not { Check: var check }) return;
        RuntimeStatus = check.DllPath is null ? check.Summary
            : $"{check.Summary} — {Format.Size(check.Bytes)}, {check.Modified:yyyy-MM-dd HH:mm}" +
              (check.Boot is { } boot ? $"{Environment.NewLine}{boot.Text} · {boot.Log}" : "");
        Raise(nameof(RuntimeName));
        Raise(nameof(RuntimeBrush));
    }

    public string Root
    {
        get => _root;
        set
        {
            _root = value ?? "";
            Raise(nameof(Root));
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise(nameof(Status));
        }
    }

    public Brush StatusBrush => Skin.Brush(_statusKey);

    public List<CheckRow> Checks
    {
        get => _checks;
        private set
        {
            _checks = value;
            Raise(nameof(Checks));
        }
    }

    /// <summary>Bound to a Visibility so the button only exists once validation succeeded.</summary>
    public Visibility CanOpen => _canOpen ? Visibility.Visible : Visibility.Collapsed;

    public GameInstall? Install { get; private set; }

    /// <summary>Find this game in the Steam libraries and fill in the root.</summary>
    public bool Detect()
    {
        var found = GameInstall.Detect(Profile);
        if (found is null)
        {
            Set("not found in any Steam library", "Error");
            return false;
        }
        Root = found.Root;
        Validate();
        return true;
    }

    /// <summary>Validate the root, then read what is installed for the runtime.</summary>
    public void Refresh()
    {
        Validate();
        if (Install is not null && RuntimeVisibility == Visibility.Visible) DescribeRuntime();
    }

    /// <summary>Executable first, then the content folders (the runtime's folders: <see cref="DescribeRuntime"/>).</summary>
    public void Validate()
    {
        _canOpen = false;
        Install = null;
        Checks = [];
        _runtime = null;
        RuntimePaths = [];
        RuntimeStatus = RuntimeMods = "";
        Raise(nameof(RuntimeName));
        if (string.IsNullOrWhiteSpace(Root))
        {
            Set("no path set", "FgDim");
            return;
        }
        if (!Directory.Exists(Root))
        {
            Set("folder does not exist", "Error");
            return;
        }

        var install = new GameInstall(Root, Profile);
        var report = install.Validate();
        Checks = report.Checks.Select(c => new CheckRow(c)).ToList();

        if (!report.ExeFound)
        {
            Set($"no {Profile.Name} executable under this root", "Error");
            return;
        }
        if (Profile.Folders.Count == 0)
        {
            Install = install;
            Set($"executable found ({System.IO.Path.GetFileName(report.Exe)}) — folder layout not defined yet",
                "Warn");
            return;
        }
        if (!report.Ok)
        {
            Set($"executable found, {string.Join(", ", report.Missing.Select(m => m.Label))} missing", "Error");
            return;
        }

        Install = install;
        _canOpen = GameBackends.For(Profile).Implemented;
        Set($"ok — {install.DataName}, {install.Rpacks().Length} packs, {install.Paks().Length} paks", "Fg");
    }

    private void Set(string text, string brushKey)
    {
        _statusKey = brushKey;
        Status = text;
        Raise(nameof(StatusBrush));
        Raise(nameof(CanOpen));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Settings: where each game lives, with detect and validate per game.</summary>
public partial class SettingsView : UserControl
{
    private readonly GameSettings _settings;
    private readonly List<GameRow> _rows;

    /// <summary>Raised when a root is saved, so the game selector can pick it up.</summary>
    public event Action? RootsChanged;

    /// <summary>Raised when the user opens a game straight from Settings.</summary>
    public event Action<GameInstall>? PlayRequested;

    /// <summary>Raised when runtime modding is switched for a game, so the open packs can be reloaded.</summary>
    public event Action<string>? RuntimeChanged;

    /// <summary>The per-game rows, for the runtime check.</summary>
    public IReadOnlyList<GameRow> GameRows => _rows;

    public SettingsView(GameSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _rows = GameProfile.All.Select(p => new GameRow(p, settings)).ToList();
        Rows.ItemsSource = _rows;
        SettingsPath.Text = GameSettings.FilePath;
        Loaded += (_, _) =>
        {
            foreach (var r in _rows)
            {
                if (!string.IsNullOrWhiteSpace(r.Root)) r.Refresh();
            }
        };
    }

    private static GameRow Row(object sender) => (GameRow)((FrameworkElement)sender).Tag;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var row = Row(sender);
        var dlg = new OpenFolderDialog { Title = $"{row.Name} folder" };
        if (!string.IsNullOrWhiteSpace(row.Root) && Directory.Exists(row.Root)) dlg.InitialDirectory = row.Root;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        row.Root = GameInstall.NormalizeRoot(dlg.FolderName);
        Save(row);
        row.Refresh();
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        var row = Row(sender);
        if (!row.Detect()) return;
        Save(row);
        row.Refresh();
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var row = Row(sender);
        Save(row);
        row.Refresh();
    }

    private void Root_LostFocus(object sender, RoutedEventArgs e) => Save(Row(sender));

    private void RuntimeValidate_Click(object sender, RoutedEventArgs e)
    {
        var row = Row(sender);
        row.ValidateRuntime();
        RuntimeChanged?.Invoke(row.Id);
    }

    private void RuntimeToggle_Click(object sender, RoutedEventArgs e)
    {
        var row = Row(sender);
        row.RuntimeEnabled = ((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked == true;
        RuntimeChanged?.Invoke(row.Id);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender).Install is { } install) PlayRequested?.Invoke(install);
    }

    private void Save(GameRow row)
    {
        _settings.SetRoot(row.Id, row.Root);
        _settings.Save();
        RootsChanged?.Invoke();
    }
}
