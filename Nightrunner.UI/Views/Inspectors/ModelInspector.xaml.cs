using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>A row of the model tree. <see cref="Panel"/>/<see cref="Payload"/> is where Reveal (double-click) goes.</summary>
public sealed class ModelNode(string kind, string text, string detail = "")
{
    public string Kind { get; } = kind;
    public string Text { get; } = text;
    public string Detail { get; init; } = detail;
    public string? Tip { get; init; }
    public double Opacity { get; init; } = 1.0;
    public Brush? Brush { get; init; }
    public bool Expanded { get; set; }
    public string? Panel { get; init; }
    public object? Payload { get; init; }
    public List<ModelNode> Children { get; } = [];
}

/// <summary>
/// A <c>.model</c>: slots → mesh entries (the first is the one the game draws; the rest are dimmed) → submeshes →
/// the material each ends up with and its rttiValues → textures. Every row reveals into Meshes, Materials or Textures.
/// </summary>
public partial class ModelInspector : UserControl
{
    private readonly Workspace _ws;
    private readonly IPanelHost _host;
    private ModelEntry? _shown;
    private int _loads;

    public ModelInspector(Workspace workspace, IPanelHost host)
    {
        _ws = workspace;
        _host = host;
        InitializeComponent();
        Bones.Attach(workspace);
    }

    public ModelInspector With(Selected.Model what)
    {
        if (ReferenceEquals(_shown, what.Entry)) return this;
        _shown = what.Entry;
        _ = Load(what.Models, what.Entry);
        return this;
    }

    private MeshModel Decode(int gid) => _ws.Mesh(gid).GetAwaiter().GetResult();

    private async Task Load(ModelCatalog models, ModelEntry entry)
    {
        int load = ++_loads;
        Info.Text = $"{entry.Basename}\nresolving...";
        Tree.ItemsSource = null;
        Bones.Show(null);
        TreeNote.Text = BonesNote.Text = "";
        var catalog = _ws.Catalog;
        try
        {
            var sdb = (await _ws.Sdb.EnsureAsync())?.File;
            var loaded = await Task.Run(() => models.Load(entry));
            await _ws.Prefetch(loaded);
            var (res, skeleton) = await Task.Run(() =>
            {
                var doc = loaded;
                var res = ModelResolver.Resolve(doc, catalog, sdb, Decode, (gid, mesh) =>
                    _ws.SkinOf(catalog, gid) ?? (mesh.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } k ? k.DefaultIndex : -1));
                var parts = res.Slots.SelectMany(s => s.Meshes.Where(m => m.Drawn && m.Found && m.Error is null))
                    .Select(m => (catalog.Name(m.Gids[0]), Decode(m.Gids[0]))).ToList();
                var skel = res.SkeletonGids.Length > 0 ? Decode(res.SkeletonGids[0]) : null;
                return (res, ModelSkeleton.Merge(skel, res.Skeleton ?? "", parts));
            });
            if (load != _loads) return;
            Show(entry, res, skeleton, catalog);
        }
        catch (Exception e) when (e is ModelFormatException or MeshFormatException or RpackFormatException or ObjectDisposedException or System.IO.IOException)
        {
            if (load == _loads) Info.Text = $"{entry.Basename}\n{e.Message}";
        }
    }

    private void Show(ModelEntry entry, ResolvedModel res, ModelSkeleton skeleton, RpackCatalog catalog)
    {
        var doc = res.Doc;
        var accent = Skin.Brush("Accent");
        var warn = Skin.Brush("Warn");
        var nodes = new List<ModelNode>();
        int drawn = 0, missing = 0;
        foreach (var rs in res.Slots)
        {
            var slot = new ModelNode("slot", rs.Slot.Name, rs.Meshes.Count == 1 ? "" : $"{rs.Meshes.Count} entries")
            {
                Expanded = true,
                Tip = rs.Problem ?? rs.Slot.FilterText,
                Brush = rs.Problem is null ? null : warn,
            };
            if (rs.Slot.HasCloth)
                slot.Children.Add(new ModelNode("cloth", rs.Slot.Json["clothResources"] is System.Text.Json.Nodes.JsonObject co
                                                    ? Nightrunner.Core.Model.ModelDocument.Str(co["name"]) ?? "?" : "?")
                {
                    Tip = rs.Slot.Json["clothResources"]?.ToJsonString(),
                });
            foreach (var m in rs.Meshes)
            {
                if (m.Drawn && m.Found) drawn++;
                if (!m.Found) missing++;
                var flags = new List<string>();
                if (m.Drawn) flags.Add("drawn");
                else flags.Add("not drawn");
                if (m.Entry.Selected) flags.Add("selected");
                if (!m.Found) flags.Add("missing");
                if (m.Error is not null) flags.Add("error");
                if (m.Skin >= 0 && m.Found && Decode(m.Gids[0]).SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } skins && m.Skin < skins.Skins.Count)
                    flags.Add($"skin {skins.Skins[m.Skin].NameStr}");
                var mesh = new ModelNode("mesh", m.Entry.Name, string.Join(" · ", flags))
                {
                    Opacity = m.Drawn ? 1.0 : 0.45,
                    Brush = !m.Found || m.Error is not null ? warn : m.Drawn ? accent : null,
                    Tip = m.Error ?? (m.Drawn ? "the game draws this entry" : "the game draws the first entry of a slot; this one is ignored"),
                    Panel = "meshes",
                    Payload = m.Entry.MeshName,
                };
                foreach (var p in m.Problems) mesh.Children.Add(new ModelNode("note", p) { Brush = warn });
                foreach (var s in m.Submeshes) mesh.Children.Add(Submesh(s, catalog, warn));
                slot.Children.Add(mesh);
            }
            nodes.Add(slot);
        }
        Tree.ItemsSource = nodes;
        TreeNote.Text = $"{res.Slots.Count} slots · {drawn} drawn" + (missing > 0 ? $" · {missing} missing" : "");

        Bones.Show(skeleton);
        BonesNote.Text = $"{skeleton.Names.Count} bones · {skeleton.Overrides.Count} rest poses from parts";

        var info = new StringBuilder();
        info.AppendLine($"{entry.Basename}   {System.IO.Path.GetFileName(entry.Pak)}{(entry.Custom ? " (custom)" : "")}{(entry.Wins ? "" : "  overridden")}");
        info.AppendLine($"member {entry.Name}");
        info.AppendLine($"skeleton {doc.Skeleton ?? "none"}{(doc.Skeleton is not null && res.SkeletonGids.Length == 0 ? " (missing)" : "")}");
        info.AppendLine($"slots {doc.Slots.Count} · drawn {drawn} · bones {skeleton.Names.Count} · properties {doc.Properties.Count}" +
                        (doc.HasPoseItems ? " · poseItems" : ""));
        info.Append($"version {doc.Version}");
        foreach (var n in res.Notes) info.Append($"\n  {n}");
        Info.Text = info.ToString();
    }

    private static ModelNode Submesh(ResolvedSubmesh s, RpackCatalog catalog, Brush warn)
    {
        string where = s.Entry is null ? "" : $"e{s.Entry}.s{s.Submesh} · {s.Triangles:N0} tris · ";
        var node = new ModelNode("material", s.BaseMaterial, where + s.MaterialSource + (s.InSdb ? "" : " · not in sdb"))
        {
            Tip = s.SkinMaterial is not null ? $"embedded {s.Embedded}, skin {s.SkinMaterial}" : $"embedded {s.Embedded}",
            Brush = s.JoinError is not null || !s.InSdb ? warn : null,
            Panel = "materials",
            Payload = s.BaseMaterial,
        };
        if (s.JoinError is not null) node.Children.Add(new ModelNode("note", s.JoinError) { Brush = warn });
        foreach (var r in s.Rtti.Where(r => r.Type != 7))
            node.Children.Add(new ModelNode("rtti", r.Name, $"{r.Kind} {r.Value}"));
        foreach (var t in s.Textures)
            node.Children.Add(new ModelNode("texture", t.Texture, $"{t.Param} · {t.Source}" + (t.Gids.Length == 0 ? " · missing" : ""))
            {
                Tip = t.Original is not null ? $"replaces {t.Original}" : null,
                Brush = t.Gids.Length == 0 ? warn : null,
                Panel = "textures",
                Payload = t.Gids.Length > 0 ? t.Gids[0] : t.Texture,
            });
        return node;
    }

    private void Tree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ModelNode n } && ReferenceEquals(n, Tree.SelectedItem)) Reveal(n);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is ModelNode n) Reveal(n);
    }

    private void Reveal(ModelNode n)
    {
        if (n.Panel is { } panel && n.Payload is { } payload) _host.Reveal(panel, payload);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is ModelNode n) Clipboard.SetText(n.Text);
    }
}
