using System.Text;
using System.Windows;
using System.Windows.Controls;
using Nightrunner.Core.Games;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>
/// A runtime mod: its manifest, switch and order, whether it loads, the runtime check, items, overrides and problems. The
/// "on" box switches it in <c>nightrunner.json</c> (<see cref="Workspace.SetModEnabled"/>).
/// </summary>
public sealed class ModInspector : UserControl
{
    private readonly Workspace _ws;
    private readonly CheckBox _on = new() { Content = "on", VerticalAlignment = VerticalAlignment.Center, ToolTip = "applies at next game start" };
    private readonly TextBlock _note = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private RuntimeMod? _mod;

    private readonly TextBox _info = new()
    {
        IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Top,
    };

    public ModInspector(Workspace ws)
    {
        _ws = ws;
        _info.SetResourceReference(StyleProperty, "Mono");
        _note.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        ToolTipService.SetShowOnDisabled(_on, true);
        _on.Click += Switch_Click;
        Loaded += (_, _) => _ws.ModSwitched += OnModSwitched;
        Unloaded += (_, _) => _ws.ModSwitched -= OnModSwitched;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_on);
        bar.Children.Add(_note);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_info);
        Content = root;
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_mod is not { } mod) return;
        _on.IsEnabled = false;
        await _ws.SetModEnabled(mod.Id, _on.IsChecked == true);   // OnModSwitched shows the outcome
    }

    /// <summary>A switch from here or from the Mods window: say what happened; the box shows what the file now says.</summary>
    private void OnModSwitched(RuntimeSwitch result)
    {
        if (_mod is null || !result.ModId.Equals(_mod.Id, StringComparison.OrdinalIgnoreCase)) return;
        _on.IsChecked = result.Written ? result.Enabled : _mod.Enabled;
        _on.IsEnabled = true;
        Note(result);
    }

    private void Note(RuntimeSwitch? last)
    {
        _note.Text = last is null || _mod is null || !last.ModId.Equals(_mod.Id, StringComparison.OrdinalIgnoreCase) ? "" : last.Message;
        _note.SetResourceReference(TextBlock.ForegroundProperty, last?.State == RuntimeSwitchState.Refused ? "Warn" : "FgDim");
    }

    public ModInspector With(Selected.Mod what)
    {
        var m = what.Value;
        _mod = m;
        _on.IsChecked = m.Enabled;
        string? locked = !_ws.RuntimeModding ? "runtime modding off" : what.Runtime is not { Found: true } ? "runtime not installed"
            : m.Blocked is not null ? _ws.Runtime.Settings.Problem ?? m.Blocked : null;
        _on.IsEnabled = locked is null;
        _on.ToolTip = locked ?? "applies at next game start";
        Note(_ws.LastSwitch);
        var s = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrEmpty(value)) s.AppendLine($"{label,-10} {value}"); }
        Line("id", m.Id);
        Line("name", m.Name);
        Line("version", m.Version);
        Line("author", m.Author);
        Line("about", m.Description);
        Line("folder", m.Folder);
        Line("valid", m.Valid ? "ok" : $"no · {m.Invalid}");
        Line("switch", m.Blocked is { } blocked ? $"? · {blocked}" : (m.Enabled ? "on" : "off") + (m.Listed ? "" : " (not listed: on, last)"));
        Line("order", m.Listed ? m.Order.ToString() : "last");
        Line("loads", what.Loads);
        Line("runtime", what.Runtime is not { } r ? null
            : r.Flavor == RuntimeFlavor.Proxy ? (r.Found ? "ok" : "incomplete") + $" · {r.Summary}"
            : $"none · {r.Summary}");
        Line("boot", what.Runtime?.Boot is { } boot ? $"{boot.Text} · {boot.Log}" : null);
        s.AppendLine();
        s.AppendLine($"items {m.Items.Count}");
        foreach (var i in m.Items)
            s.AppendLine($"  {i.Kind.ToString().ToLowerInvariant(),-6} {i.Name}  {i.At}  {(i.Loads ? "loads" : i.Problem ?? "not found")}");
        if (what.Overrides is { } o)
        {
            s.AppendLine();
            s.AppendLine($"overrides {o.Count:N0}");
            foreach (var g in o.GroupBy(x => x.Stock).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                s.AppendLine($"  {g.Key}: {g.Count():N0}");
        }
        if (m.Problems.Count > 0)
        {
            s.AppendLine();
            s.AppendLine($"problems {m.Problems.Count}");
            foreach (var p in m.Problems) s.AppendLine($"  {p}");
        }
        _info.Text = s.ToString();
        return this;
    }
}
