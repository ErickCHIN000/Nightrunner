using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Nightrunner.Core.Project;

namespace Nightrunner.UI.Views.Inspectors;

public sealed record BuildResourceRow(string Resource, string Last, string Check);

/// <summary>
/// A project item from the Build window: its output, form and destination, template, the source file the build reads,
/// edits pending, and — per output resource — what the last build and the last check found (result, diff, warnings).
/// Model overrides and scenes offer Edit, which opens their editor here.
/// </summary>
public sealed class BuildItemInspector : UserControl
{
    private readonly PanelContext _ctx;
    private readonly TextBox _info = new() { IsReadOnly = true, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Button _edit = new() { Content = "Edit", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 6),
                                            HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ListView _resources = new() { MaxHeight = 220, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _warnings = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private Selected.BuildItem? _shown;

    public BuildItemInspector(PanelContext ctx)
    {
        _ctx = ctx;
        _info.SetResourceReference(StyleProperty, "Mono");
        _warnings.SetResourceReference(StyleProperty, "Mono");
        var grid = new GridView();
        grid.Columns.Add(Column("resource", nameof(BuildResourceRow.Resource), 280, "CellName"));
        grid.Columns.Add(Column("last", nameof(BuildResourceRow.Last), 80, "CellDim"));
        grid.Columns.Add(Column("check", nameof(BuildResourceRow.Check), 80, "CellDim"));
        _resources.View = grid;
        _edit.Click += (_, _) =>
        {
            if (_shown is not { } s) return;
            switch (s.Info.Item)
            {
                case ModelItem m: _ctx.Selection.Set("Build", new Selected.ProjectModel(s.Owner, m)); break;
                case SceneItem sc: _ctx.Selection.Set("Build", new Selected.ProjectScene(s.Owner, sc)); break;
            }
        };
        var root = new DockPanel();
        foreach (var top in new FrameworkElement[] { _edit, _info, _resources })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }
        root.Children.Add(_warnings);
        Content = root;
    }

    private static GridViewColumn Column(string header, string path, double width, string style)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetResourceReference(StyleProperty, style);
        return new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = text } };
    }

    public BuildItemInspector With(Selected.BuildItem what)
    {
        _shown = what;
        var i = what.Info;
        _edit.Visibility = i.Item is ModelItem or SceneItem ? Visibility.Visible : Visibility.Collapsed;
        string Rel(string path) => Path.GetRelativePath(what.Owner.Folder, path).Replace('\\', '/');
        var info = new StringBuilder();
        info.AppendLine($"output    {i.Name}");
        info.AppendLine($"form      {i.Form}{(i.Pack.Length > 0 ? $" → {i.Pack}" : "")}");
        if (i.Template.Length > 0) info.AppendLine($"template  {i.Template}");
        if (i.Source is { } src)
            info.AppendLine($"source    {Rel(src)}{i.Changed switch { true => "  (edited)", false => "  (as exported)", _ => "" }}");
        if (i.Edits.Length > 0) info.AppendLine($"edits     {i.Edits}");
        if (i.Refusal is { } refusal) info.AppendLine($"refused   {refusal}");
        if (what.Last is { } last) info.AppendLine($"last      {Join(last.Result, last.Diff)}");
        if (what.Check is { } check) info.AppendLine($"check     {Join(check.Result, check.Diff)}");
        _info.Text = info.ToString().TrimEnd();

        var keys = (what.Last?.Resources ?? []).Select(r => r.Key).Concat((what.Check?.Resources ?? []).Select(r => r.Key))
            .Concat(i.Outputs).Distinct(StringComparer.Ordinal).ToList();
        string State(BuildItemState? s, string key) => s?.Resources.FirstOrDefault(r => r.Key == key).Value ?? "";
        _resources.ItemsSource = keys.Select(k => new BuildResourceRow(k, State(what.Last, k), State(what.Check, k))).ToList();
        _resources.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var w = new StringBuilder();
        foreach (var (label, state) in new[] { ("last", what.Last), ("check", what.Check) })
        {
            if (state is not { Warnings.Count: > 0 }) continue;
            w.AppendLine(label);
            foreach (var line in state.Warnings) w.AppendLine("  " + line);
        }
        _warnings.Text = w.ToString().TrimEnd();
        return this;
    }

    private static string Join(string result, string diff) => diff.Length > 0 ? $"{result} · {diff}" : result;
}
