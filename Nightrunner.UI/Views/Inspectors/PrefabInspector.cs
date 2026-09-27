using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Prefab;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>
/// A binary or text prefab: its components (class, pcid, component class, transform, property values), child entities (the
/// prefab each instances, where, with which presets — double-click opens it), fields, virtual fields, bindings and the
/// preset sets of its class (double-click a preset: the Viewport draws the prefab with it). Mesh names reveal into
/// Meshes. A selected component or entity can be edited — transform, mesh, skin, active — and applied to an in-memory
/// copy of its pack's <c>Prefabs</c> resource (the Viewport redraws the edited prefab); Save records the applied edits
/// in the open project (<see cref="Nightrunner.Core.Project.ProjectAssets.AddPrefabEdits"/>).
/// </summary>
public sealed class PrefabInspector : UserControl
{
    private readonly TextBox _info = new() { IsReadOnly = true, Height = 96, Margin = new Thickness(0, 0, 0, 6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly PanelContext _ctx;
    private Selected.Prefab? _what;
    private PrefabEntry? _shown;

    // edit strip
    private readonly StackPanel _edit = new() { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBox _t = Box(), _r = Box(), _s = Box(), _mesh = Box(220), _skin = Box(220);
    private readonly CheckBox _active = new() { Content = "Active", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private PrefabComponent? _comp;
    private string? _meshField, _skinField;

    /// <summary>Applied, not yet saved: per pack label, the edited resource and the edits in order.</summary>
    private readonly Dictionary<string, (PrefabContainer C, List<PrefabEditOp> Ops)> _pending = new(StringComparer.OrdinalIgnoreCase);

    public PrefabInspector(PanelContext ctx)
    {
        _ctx = ctx;
        _info.SetResourceReference(StyleProperty, "Mono");
        _tree.SetResourceReference(BackgroundProperty, "Bg");
        _tree.SetResourceReference(ForegroundProperty, "Fg");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _tree.MouseDoubleClick += (_, e) =>
        {
            if (_tree.SelectedItem is not TreeViewItem item) return;
            if (item.Tag is (string panel, object payload)) _ctx.Host.Reveal(panel, payload);
            else if (item.DataContext is string preset && _what is { } w)
                _ctx.Selection.Set("Prefabs", w with { Presets = preset == w.Presets ? null : preset });
        };
        _tree.SelectedItemChanged += (_, _) => ShowEdit(Owner(_tree.SelectedItem as TreeViewItem));

        var apply = new Button { Content = "Apply", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 2, 10, 2) };
        var save = new Button { Content = "Save", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 2, 10, 2) };
        var revert = new Button { Content = "Revert", Padding = new Thickness(10, 2, 10, 2) };
        apply.Click += async (_, _) => await ApplyAsync();
        save.Click += (_, _) => Save();
        revert.Click += (_, _) => Revert();
        _edit.Children.Add(Line(("Translate", _t)));
        _edit.Children.Add(Line(("Rotate", _r)));
        _edit.Children.Add(Line(("Scale", _s)));
        _edit.Children.Add(Line(("Mesh", _mesh)));
        _edit.Children.Add(Line(("Skin", _skin)));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _active.Margin = new Thickness(0, 0, 12, 0);
        _active.SetResourceReference(ForegroundProperty, "Fg");
        buttons.Children.Add(_active);
        buttons.Children.Add(apply);
        buttons.Children.Add(save);
        buttons.Children.Add(revert);
        _edit.Children.Add(buttons);
        _edit.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(_edit, Dock.Top);
        root.Children.Add(_info);
        root.Children.Add(_edit);
        root.Children.Add(_tree);
        Content = root;
    }

    public PrefabInspector With(Selected.Prefab what)
    {
        _what = what;
        if (ReferenceEquals(_shown, what.Entry)) return this;
        _shown = what.Entry;
        var entry = what.Entry;
        _comp = null;
        _edit.Visibility = Visibility.Collapsed;
        _tree.Items.Clear();
        string where = entry.Source == PrefabSource.Pak
            ? $"{System.IO.Path.GetFileName(entry.Pack)}/{entry.Member}" + (entry.Index > 0 ? $" #{entry.Index}" : "")
            : $"{System.IO.Path.GetFileNameWithoutExtension(entry.Pack)} #{entry.Index}";
        string shadow = entry.Wins ? "" : $"  (shadowed by {entry.ShadowedBy?.ToString() ?? "an earlier registration"})";
        if (entry.TryRoot(out var error) is not { } p)
        {
            _info.Text = $"{entry.Name}   {where}{shadow}{Environment.NewLine}{error}";
            return this;
        }
        bool edited = Editable(entry) && _pending.TryGetValue(entry.Pack, out var pend) && pend.Ops.Count > 0;
        var info = new StringBuilder();
        info.AppendLine($"{entry.Name}   {where}{shadow}" + (edited ? "  (edited)" : "") + (what.Presets is { } ps ? $"  · {ps}" : ""));
        if (entry.Document.TextSource is { } src)
            info.AppendLine($"{src.Format.ToString().ToLowerInvariant()} · version {src.Version?.ToString() ?? "-"} · sub-prefabs {entry.Document.Prefabs.Count - 1}");
        else
            info.AppendLine($"base {p.BaseClass ?? "?"} · dom {p.DomFormat} · flags 0x{p.Flags:X2}" + (entry.Document.Layout is { IsDl2: true } ? " · dl2" : ""));
        info.AppendLine($"components {p.Components.Count} · entities {p.Entities.Count()} · fields {p.Fields.Count} · virtual {p.VirtualFields.Count}");
        info.Append($"interfaces {p.Interfaces.Count} · pipes {p.PipesIn.Count}/{p.PipesOut.Count} · bindings {p.PropertyBindings.Count}/{p.PipeBindings.Count}");
        _info.Text = info.ToString();

        var comps = Group("components", $"{p.Components.Count(c => c.Entity is null)}", expanded: true);
        foreach (var c in p.Components.Where(c => c.Entity is null)) comps.Items.Add(Component(c));
        var ents = Group("entities", $"{p.Entities.Count()}", expanded: true);
        foreach (var c in p.Entities) ents.Items.Add(Entity(c, what.Prefabs));
        var fields = Group("fields", $"{p.Fields.Count}");
        foreach (var f in p.Fields)
        {
            var node = Row("field", f.Name ?? "?", $"{f.TypeName}{(f.Init is { Length: > 0 } i ? " · " + i : "")}");
            foreach (var d in f.Destinations ?? []) node.Items.Add(Row("to", d.Field ?? "?", $"pcid {d.Pcid}"));
            fields.Items.Add(node);
        }
        var virt = Group("virtual", $"{p.VirtualFields.Count}");
        foreach (var v in p.VirtualFields)
        {
            var node = Row("virtual", v.Name ?? "?", v.Init ?? "");
            foreach (var d in v.Destinations) node.Items.Add(Row("to", d.Field ?? "?", $"pcid {d.Pcid}"));
            virt.Items.Add(node);
        }
        var binds = Group("bindings", $"{p.PropertyBindings.Count + p.PipeBindings.Count}");
        foreach (var b in p.PropertyBindings) binds.Items.Add(Row("property", $"{b.Source} → {b.Target}", $"pcid {b.SourcePcid} → {b.TargetPcid}"));
        foreach (var b in p.PipeBindings) binds.Items.Add(Row("pipe", $"{b.Source} → {b.Target}", $"pcid {b.SourcePcid} → {b.TargetPcid}"));
        var presets = what.Entry.Document.PresetSets.Where(s => s.ClassPrefab == p.Index).ToList();
        var pre = Group("presets", $"{presets.Sum(s => s.Groups.Sum(g => g.Presets.Count))}");
        foreach (var set in presets)
            foreach (var g in set.Groups)
            {
                var gn = Row("group", g.Name ?? g.Key ?? "?", $"{g.Presets.Count}");
                foreach (var pr in g.Presets)
                {
                    var pn = Row("preset", pr.Name ?? pr.Key ?? "?", $"{pr.Values.Count} values");
                    pn.DataContext = $"{g.Key ?? g.Name};{pr.Key ?? pr.Name}";
                    foreach (var v in pr.Values) pn.Items.Add(Row("value", v.Field ?? "?", v.Text ?? Convert.ToHexStringLower(v.Union)));
                    gn.Items.Add(pn);
                }
                pre.Items.Add(gn);
            }
        foreach (var g in new[] { comps, ents, fields, virt, binds, pre }) _tree.Items.Add(g);
        if (p.TextOther.Count > 0)
        {
            var other = Group("other", $"{p.TextOther.Count}");
            foreach (var (k, v) in p.TextOther) other.Items.Add(Row("raw", k, Brief(v)));
            _tree.Items.Add(other);
        }
        return this;
    }

    private TreeViewItem Component(PrefabComponent c)
    {
        var node = Row(c.IsText ? "component" : c.ClassName, c.ComponentClass ?? "?", $"pcid {c.Pcid}" + (c.Xform is { } x ? $" · at {x.Translate} rot {x.Rotate}" : "") +
                                                                                    (c.XformComponent is { } xc ? $" · xform {xc}" : ""));
        node.DataContext = c;
        foreach (var v in (c.Values?.Entries ?? []).Concat(c.ProxyValues?.Entries ?? []))
        {
            var row = Row("value", v.Key ?? "?", Value(v));
            if (v.Text is { } t && t.EndsWith(".msh", StringComparison.OrdinalIgnoreCase)) row.Tag = ("meshes", (object)t[..^4]);
            node.Items.Add(row);
        }
        Text(node, c);
        return node;
    }

    private TreeViewItem Entity(PrefabComponent c, PrefabCatalog catalog)
    {
        var e = c.Entity!;
        string child = e.PrefabName ?? e.EntityPrefabClass ?? "?";
        var node = Row("entity", e.Name ?? "?", $"{child}" + (c.Xform is { } x ? $" · at {x.Translate} rot {x.Rotate} scale {x.Scale}" : "") +
                                              (e.PresetNames is { Length: > 0 } ps ? $" · presets {ps}" : ""));
        node.DataContext = c;
        if (catalog.Find(child) is not null) node.Tag = ("prefabs", (object)child);
        else node.ToolTip = "not registered by any loaded pack";
        foreach (var v in (c.Values?.Entries ?? [])) node.Items.Add(Row("value", v.Key ?? "?", Value(v)));
        Text(node, c);
        return node;
    }

    // ---- editing ------------------------------------------------------------------------------------------------

    private static PrefabComponent? Owner(TreeViewItem? item)
    {
        for (DependencyObject? d = item; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is TreeViewItem { DataContext: PrefabComponent c }) return c;
        return null;
    }

    private static string? TextField(PrefabComponent c, string suffix) =>
        (c.Values?.Entries ?? []).FirstOrDefault(v => v.Form == PrefabValueForm.Text && v.Key is { } k && (k == suffix || k.EndsWith("::" + suffix, StringComparison.Ordinal)))?.Key;

    /// <summary>Edits are written for binary DLTB prefabs only (DL2 and text prefabs are shown, not edited).</summary>
    private static bool Editable(PrefabEntry e) => e.Source == PrefabSource.Rpack && e.Document.Layout?.IsDl2 != true;

    private void ShowEdit(PrefabComponent? c)
    {
        _comp = c;
        if (c is null || _what is not { } w || !Editable(w.Entry)) { _edit.Visibility = Visibility.Collapsed; return; }
        _edit.Visibility = Visibility.Visible;
        _status.Text = "";
        bool xf = c.Xform is not null;
        foreach (var (box, v) in new[] { (_t, c.Xform?.Translate), (_r, c.Xform?.Rotate), (_s, c.Xform?.Scale) })
        {
            box.IsEnabled = xf;
            box.Text = v is { } x ? F(x) : "";
        }
        _meshField = TextField(c, "m_MeshName") ?? TextField(c, "MeshName");
        _skinField = TextField(c, "m_SkinName") ?? TextField(c, "SkinName");
        _mesh.IsEnabled = _meshField is not null;
        _mesh.Text = _meshField is null ? "" : ValueText(c, _meshField) ?? "";
        _skin.IsEnabled = _skinField is not null;
        _skin.Text = _skinField is null ? "" : ValueText(c, _skinField) ?? "";
        _active.IsChecked = c.SelfActive ?? true;
    }

    private static string? ValueText(PrefabComponent c, string key) => c.Values?.Entries.FirstOrDefault(v => v.Key == key)?.Text;

    private static string F(Vec3 v) => string.Join(" ", new[] { v.X, v.Y, v.Z }.Select(f => f.ToString("R", CultureInfo.InvariantCulture)));

    private static Vec3? Parse(string text)
    {
        var parts = text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var f = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return null;
        return new Vec3(f[0], f[1], f[2]);
    }

    /// <summary>The edits the strip asks for, against the component as shown.</summary>
    private List<PrefabEditOp>? Ops(PrefabComponent c, string prefab)
    {
        var ops = new List<PrefabEditOp>();
        if (c.Xform is { } x)
        {
            if (Parse(_t.Text) is not { } t || Parse(_r.Text) is not { } r || Parse(_s.Text) is not { } s)
            {
                _status.Text = "transform: three numbers each";
                return null;
            }
            if (t != x.Translate || r != x.Rotate || s != x.Scale)
                ops.Add(new PrefabEditOp("transform", prefab, c.Pcid) { Translate = t, Rotate = r, Scale = s });
        }
        foreach (var (field, box) in new[] { (_meshField, _mesh), (_skinField, _skin) })
            if (field is not null && box.Text != (ValueText(c, field) ?? ""))
                ops.Add(new PrefabEditOp("text", prefab, c.Pcid) { Field = field.Contains("::") ? field[(field.LastIndexOf("::") + 2)..] : field, Text = box.Text });
        bool want = _active.IsChecked == true;
        if (want != (c.SelfActive ?? true)) ops.Add(new PrefabEditOp("active", prefab, c.Pcid) { Active = want });
        return ops;
    }

    private async Task ApplyAsync()
    {
        if (_comp is not { } c || _what is not { } what) return;
        var entry = what.Entry;
        if (Ops(c, entry.Name) is not { } ops) return;
        if (ops.Count == 0) { _status.Text = "unchanged"; return; }
        var catalog = _ctx.Workspace.Catalog;
        _status.Text = "applying…";
        try
        {
            var (container, doc) = await Task.Run(() =>
            {
                PrefabContainer basis;
                if (_pending.TryGetValue(entry.Pack, out var have)) basis = have.C.Clone();
                else
                {
                    var pe = catalog.Packs.FirstOrDefault(p => p.Label.Equals(entry.Pack, StringComparison.OrdinalIgnoreCase))?.Pack
                             ?? throw new PrefabFormatException($"pack {entry.Pack} is not open");
                    basis = PrefabContainer.Read(pe, PrefabContainer.ResourcesIn(pe).Single());
                }
                PrefabEdits.Apply(basis, ops);
                var doc = PrefabDecoder.Decode(basis);
                foreach (var op in ops)
                    if (PrefabEdits.Check(doc, op) is { } bad) throw new PrefabFormatException($"{op}: {bad}");
                return (basis, doc);
            });
            var list = _pending.TryGetValue(entry.Pack, out var prev) ? prev.Ops : [];
            list.AddRange(ops);
            _pending[entry.Pack] = (container, list);
            var edited = new PrefabEntry(entry.Name, entry.Pack, entry.Index, doc.Prefabs[entry.Index], doc);
            foreach (var op in ops) Log.Info("prefab", $"applied {op}");
            _shown = null;
            _ctx.Selection.Set("Prefabs", what with { Entry = edited });
            _status.Text = $"applied {ops.Count}";
        }
        catch (PrefabFormatException e)
        {
            _status.Text = e.Message;
            Log.Warn("prefab", e.Message);
        }
    }

    private void Save()
    {
        if (_what is not { } what) return;
        if (!_pending.TryGetValue(what.Entry.Pack, out var pend) || pend.Ops.Count == 0) { _status.Text = "nothing applied"; return; }
        if (_ctx.Workspace.Project is not { } project) { _status.Text = "no project"; return; }
        var catalog = _ctx.Workspace.Catalog;
        var ops = pend.Ops.ToList();
        string pack = what.Entry.Pack;
        // edits start from the stock pack's prefabs, never a mod's; refused before the pending edits are dropped
        if (catalog.Packs.FirstOrDefault(p => p.Label.Equals(pack, StringComparison.OrdinalIgnoreCase)) is { } source)
        {
            try { Nightrunner.Core.Games.StockCopy.RequireStock(source, _ctx.Workspace.Origins); }
            catch (Nightrunner.Core.Project.ProjectException e)
            {
                _status.Text = e.Message;
                Log.Warn("prefab", e.Message);
                return;
            }
        }
        _pending.Remove(pack);
        _status.Text = $"saving {ops.Count}";
        Jobs.Run($"add {ops.Count} prefab edit(s) to {project.Name}", "project", _ =>
            $"-> {Nightrunner.Core.Project.ProjectAssets.AddPrefabEdits(project, catalog, pack, ops).Folder}");
    }

    private void Revert()
    {
        if (_what is not { } what) return;
        _pending.Remove(what.Entry.Pack);
        _status.Text = "";
        if (_ctx.Workspace.Prefabs?.Find(what.Entry.Name) is { } original && original.Pack == what.Entry.Pack)
        {
            _shown = null;
            _ctx.Selection.Set("Prefabs", what with { Entry = original });
        }
    }

    // ---- rows ---------------------------------------------------------------------------------------------------

    /// <summary>A text component's native fields (with their ERTTIType) and the members kept as DOM text.</summary>
    private static void Text(TreeViewItem node, PrefabComponent c)
    {
        foreach (var v in c.NativeFields) node.Items.Add(Row("native", v.Key ?? "?", (v.Type is { } t ? $"[{t}] " : "") + (v.Text ?? "")));
        foreach (var (k, v) in c.TextOther) node.Items.Add(Row("raw", k, Brief(v)));
    }

    private static string Brief(string s) => s.Length > 200 ? s[..200] + "…" : s;

    private static string Value(PrefabValue v) => v.Form switch
    {
        PrefabValueForm.Text => v.Text ?? "",
        PrefabValueForm.Scalar => v.Scalar?.ToString("G6", CultureInfo.InvariantCulture) ?? "",
        PrefabValueForm.OutOfLine when v.Floats is { } f => string.Join(", ", f.Select(x => x.ToString("G6", CultureInfo.InvariantCulture))),
        _ => $"{v.Form.ToString().ToLowerInvariant()} 0x{v.Payload:X16}",
    };

    private static TextBox Box(double width = 150)
    {
        var b = new TextBox { Width = width, Margin = new Thickness(0, 1, 0, 1) };
        b.SetResourceReference(StyleProperty, "Mono");
        return b;
    }

    private static StackPanel Line((string Label, TextBox Box) item)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var k = new TextBlock { Text = item.Label, Width = 70, VerticalAlignment = VerticalAlignment.Center };
        k.SetResourceReference(StyleProperty, "CellType");
        panel.Children.Add(k);
        panel.Children.Add(item.Box);
        return panel;
    }

    private static TreeViewItem Group(string text, string detail, bool expanded = false)
    {
        var item = Row("", text, detail);
        item.IsExpanded = expanded;
        return item;
    }

    private static TreeViewItem Row(string kind, string text, string detail)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var k = new TextBlock { Text = kind, Width = 110, TextTrimming = TextTrimming.CharacterEllipsis };
        k.SetResourceReference(StyleProperty, "CellType");
        var t = new TextBlock { Text = text };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBright");
        var d = new TextBlock { Text = detail, Margin = new Thickness(8, 0, 0, 0) };
        d.SetResourceReference(StyleProperty, "CellDim");
        panel.Children.Add(k);
        panel.Children.Add(t);
        panel.Children.Add(d);
        var item = new TreeViewItem { Header = panel };
        item.SetResourceReference(ForegroundProperty, "Fg");
        return item;
    }
}
