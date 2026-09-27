using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Nightrunner.Core.Export;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.UI.Views.Inspectors;

public sealed record MeshPartRow(int Entry, int Lod, int Submesh, string Material, int Format, string Tris, int Bones);

public sealed record MeshMaterialRow(int Slot, string Name, string Kind, string Sdb, SdbMaterial? Resolved);

public sealed record MeshTextureRow(string Name, string Slot, string Status, int? Gid);

/// <summary>A skin in the Inspector's list: colour swatch (ColorI hypothesis, see docs/formats.md) and counts.</summary>
public sealed class MeshSkinRow(Nightrunner.Core.Mesh.Skin skin)
{
    public int Index => skin.Index;
    public string Name => skin.NameStr.Length > 0 ? skin.NameStr : $"#{skin.Index}";
    public double Opacity => skin.FilterInEditor ? 0.45 : 1.0;
    public string Tip => skin.FilterInEditor ? "FilterInEditor" : Name;
    public System.Windows.Media.Brush? Swatch => skin.HasColor && skin.Color is { } c ? Brush(c) : null;
    public string ColorTip => skin.HasColor && skin.Color is { } c ? $"ColorI(0, {c[0]}, {c[1]}, {c[2]})" : "";
    public int Uses => skin.UseSkin.Count;
    public int Replaces => skin.Replace.Count;
    public int Surfaces => skin.ReplaceSurface.Count;

    public static System.Windows.Media.Brush Brush(byte[] c)
    {
        var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c[0], c[1], c[2]));
        b.Freeze();
        return b;
    }
}

public sealed record SkinDetailRow(string Kind, string Text, System.Windows.Media.Brush? Swatch, string? Material);

/// <summary>
/// A mesh: its numbers, its submeshes (select one to isolate it in the Viewport), its materials resolved through the
/// SDB to the textures they draw with, and its skins as <c>.skn</c> text.
/// </summary>
public partial class MeshInspector : UserControl
{
    private static readonly Dictionary<string, SurfaceDefs?> Surfaces = [];

    private readonly Workspace _ws;
    private readonly IPanelHost _host;
    private (RpackCatalog Catalog, int Gid)? _shown;
    private int _loads;
    private string _name = "";

    public MeshInspector(Workspace workspace, IPanelHost host)
    {
        _ws = workspace;
        _host = host;
        InitializeComponent();
        Parts.SizeChanged += (_, _) => Stretch(Parts, ColPartMaterial);
        Materials.SizeChanged += (_, _) => Stretch(Materials, ColMaterial);
        Textures.SizeChanged += (_, _) => Stretch(Textures, ColTexture);
        SkinList.SizeChanged += (_, _) => Stretch(SkinList, ColSkin);
        SkinDetail.SizeChanged += (_, _) => Stretch(SkinDetail, ColSkinDetail);
        _ws.SkinSelected += OnSkinSelected;
        BoneRows.Attach(workspace);
    }

    private static void Stretch(ListView list, GridViewColumn first)
    {
        if (list.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != first).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        first.Width = Math.Max(110, list.ActualWidth - others - 40);
    }

    public MeshInspector With(Selected.Mesh what)
    {
        if (_shown is { } s && ReferenceEquals(s.Catalog, what.Catalog) && s.Gid == what.Gid) return this;
        _shown = (what.Catalog, what.Gid);
        _ = Load(what.Catalog, what.Gid);
        return this;
    }

    private async Task Load(RpackCatalog catalog, int gid)
    {
        int load = ++_loads;
        _name = catalog.Name(gid);
        Info.Text = $"{_name}\ndecoding...";
        Parts.ItemsSource = null;
        Materials.ItemsSource = null;
        Textures.ItemsSource = null;
        Skn.Text = "";
        MeshModel model;
        SdbFile? sdb;
        try
        {
            model = await _ws.Mesh(gid);
            sdb = (await _ws.Sdb.EnsureAsync())?.File;
        }
        catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or RpackFormatException or ObjectDisposedException)
        {
            if (load == _loads) Info.Text = $"{_name}\n{e.Message}";
            return;
        }
        if (load != _loads) return;

        var parts = model.GeometryEntries.SelectMany(e => e.Submeshes.Select(s => new MeshPartRow(
            e.Index, e.Element, s.Index, model.MaterialName(s.MaterialSlot), e.Format, s.TriangleCount.ToString("N0"),
            s.Palette.Length))).ToList();
        Parts.ItemsSource = parts;
        PartsNote.Text = $"{parts.Count} submeshes";

        var table = model.FullMaterialTable();
        var materials = await Task.Run(() => table.Select((name, slot) =>
        {
            var r = MeshScenes.Resolve(sdb, name);
            return new MeshMaterialRow(slot, name, slot < model.Materials.Length ? "mesh" : "skin",
                                       r.Problem ?? $"{r.Material!.Textures.Count} tex", r.Material);
        }).ToList());
        if (load != _loads) return;
        Materials.ItemsSource = materials;
        if (materials.Count > 0) Materials.SelectedIndex = 0;

        int lods = model.GeometryEntries.Length == 0 ? 0 : model.GeometryEntries.Max(e => e.Element) + 1;
        var formats = string.Join(", ", model.GeometryEntries.Select(e => e.Format).Distinct().Order()
            .Select(f => Vertex.Supported(f) ? $"{f} ({Vertex.Stride(f)} B{(Vertex.IsSkinned(f) ? ", skinned" : "")})" : $"{f} (unsupported)"));
        var info = new StringBuilder();
        info.AppendLine($"{_name}   layout {model.Layout}");
        info.AppendLine($"embedded {MeshModel.Text(model.EmbeddedName)}");
        info.AppendLine($"bones {model.Entities.Length} · entries {model.GeometryEntries.Length} · lods {lods} · " +
                        $"submeshes {model.SubmeshCount} · verts {model.VertexCount:N0} · tris {model.TriangleCount:N0}");
        info.AppendLine($"vertex {(formats.Length == 0 ? "none" : formats)}");
        info.Append($"materials {model.Materials.Length} + {table.Length - model.Materials.Length} skin · warnings {model.Warnings.Count}");
        if (model.ClothRaw is not null)
        {
            try
            {
                var c = ClothData.Decode(model)!;
                info.Append($"\ncloth {c.ParticleCount} particles · {c.IndexCount / 3} tris · {c.Mappings.Count} maps · " +
                            $"{c.Mappings.Sum(m => (long)m.MappedCount):N0} bound verts · {c.Colliders.Count} colliders");
            }
            catch (MeshFormatException e) { info.Append($"\ncloth {e.Message}"); }
        }
        foreach (var w in model.Warnings) info.Append($"\n  {w}");
        Info.Text = info.ToString();

        ShowSkins(model);
        BoneRows.Show(model.Entities.Length > 0 ? Nightrunner.Core.Model.ModelSkeleton.FromMesh(model, _name) : null);
    }

    private MeshModel? _model;
    private MeshSkins? _skins;
    private bool _picking;

    private void ShowSkins(MeshModel model)
    {
        _model = model;
        _skins = null;
        SkinList.ItemsSource = null;
        SkinDetail.ItemsSource = null;
        if (model.SkinRaw is not { } raw)
        {
            SkinsNote.Text = "no skin part";
            Skn.Text = "";
            return;
        }
        var skins = MeshSkins.Decode(raw);
        if (skins.Error is { } err)
        {
            SkinsNote.Text = err;
            Skn.Text = "";
            return;
        }
        _skins = skins;
        var defs = SurfaceDefsFor(_ws.Install);
        Skn.Text = SknWriter.Write(skins, model, defs);
        SkinsNote.Text = $"{skins.Skins.Count} skins" + (defs is null ? " · surface ids numeric" : "");
        SkinList.ItemsSource = skins.Skins.Select(k => new MeshSkinRow(k)).ToList();
        if (_shown is { } s) SelectSkinRow(_ws.SkinOf(s.Catalog, s.Gid) ?? skins.DefaultIndex);
    }

    private void OnSkinSelected(RpackCatalog catalog, int gid, int skin)
    {
        if (_shown is { } s && ReferenceEquals(s.Catalog, catalog) && s.Gid == gid) SelectSkinRow(skin);
    }

    private void SelectSkinRow(int skin)
    {
        if (SkinList.ItemsSource is not List<MeshSkinRow> rows) return;
        _picking = true;
        SkinList.SelectedItem = rows.FirstOrDefault(r => r.Index == skin);
        if (SkinList.SelectedItem is { } item) SkinList.ScrollIntoView(item);
        _picking = false;
        ShowSkinDetail(skin);
    }

    private void SkinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkinList.SelectedItem is not MeshSkinRow row) return;
        ShowSkinDetail(row.Index);
        if (!_picking && _shown is { } s) _ws.SelectSkin(s.Catalog, s.Gid, row.Index);
    }

    /// <summary>
    /// One skin spelled out: what it pulls in (UseSkin), what it replaces, the physics surfaces it changes, its
    /// colour, and — last — what every slot ends up drawing with, which is exactly what the Viewport shows.
    /// </summary>
    private void ShowSkinDetail(int index)
    {
        if (_skins is not { } skins || _model is not { } model || index < 0 || index >= skins.Skins.Count)
        {
            SkinDetail.ItemsSource = null;
            return;
        }
        var k = skins.Skins[index];
        var table = model.FullMaterialTable();
        string Mat(int i) => i < table.Length ? table[i] : $"material_{i}";
        string Tag(int i) => i >= model.Materials.Length ? "  (skin-only)" : "";
        var defs = SurfaceDefsFor(_ws.Install);
        string Srf(int id) => defs is not null && defs.Surfaces.TryGetValue(id, out var n) ? n : id.ToString();
        string Flags(int f) => defs is null ? $"0x{f:X}" : string.Join(" | ", Enumerable.Range(0, 16).Select(b => 1 << b)
            .Where(m => (f & m) != 0).Select(m => defs.Flags.TryGetValue(m, out var n) ? n : $"0x{m:X}"));
        var rows = new List<SkinDetailRow>();
        if (k.FilterInEditor) rows.Add(new("FilterInEditor", "hidden in the editor", null, null));
        foreach (var u in k.UseSkin)
            rows.Add(new("UseSkin", u.Record < skins.Skins.Count ? skins.Skins[u.Record].NameStr : $"#{u.Record}", null, null));
        if (k.HasColor && k.Color is { } c)
            rows.Add(new("ColorI", $"0, {c[0]}, {c[1]}, {c[2]}   raw {Convert.ToHexString(c)}", MeshSkinRow.Brush(c), null));
        foreach (var r in k.Replace)
            rows.Add(new("Replace", $"{Mat(r.Slot)} → {Mat(r.Material)}{Tag(r.Material)}", null, Mat(r.Material)));
        foreach (var r in k.ReplaceSurface)
            rows.Add(new("Surface", $"{Srf(r.Old)} → {(r.New == r.Old ? "same" : Srf(r.New))}" +
                                    (r.SurfaceFlags != 0 ? $"  {Flags(r.SurfaceFlags)}" : ""), null, null));
        var map = skins.MaterialsFor(index, model.Materials.Length);
        for (int slot = 0; slot < map.Length; slot++)
            rows.Add(new("Draws", $"{slot}: {Mat(map[slot])}{Tag(map[slot])}", null, Mat(map[slot])));
        SkinDetail.ItemsSource = rows;
    }

    private void SkinDetail_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SkinDetail.SelectedItem is SkinDetailRow { Material: { } name }) _host.Reveal("materials", name);
    }

    private static SurfaceDefs? SurfaceDefsFor(GameInstall? install)
    {
        if (install is null) return null;
        lock (Surfaces)
        {
            if (!Surfaces.TryGetValue(install.Root, out var d)) Surfaces[install.Root] = d = SurfaceDefs.Load(install);
            return d;
        }
    }

    private void Parts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shown is not { } s || Parts.SelectedItem is not MeshPartRow row) return;
        _host.Reveal("viewport", new IsolateRequest(s.Catalog, s.Gid, row.Entry, row.Submesh));
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        Parts.SelectedItem = null;
        if (_shown is { } s) _host.Reveal("viewport", new IsolateRequest(s.Catalog, s.Gid, null, null));
    }

    private void Materials_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Materials.SelectedItem is not MeshMaterialRow { Resolved: { } mat } || _shown is not { } s)
        {
            Textures.ItemsSource = null;
            return;
        }
        var slots = mat.Routes.SelectMany(r => r.Variants).SelectMany(v => v.Bindings)
            .Where(b => !string.IsNullOrEmpty(b.Texture))
            .GroupBy(b => b.Texture!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(b => b.Parameter).Where(p => p is { Length: > 0 }).Distinct()),
                          StringComparer.OrdinalIgnoreCase);
        var catalog = s.Catalog;
        Textures.ItemsSource = mat.Textures.Select(t =>
        {
            int[] hits = [];
            try { hits = catalog.Lookup(t, 0x20); }
            catch (ObjectDisposedException) { }
            return new MeshTextureRow(t, slots.GetValueOrDefault(t) ?? "", hits.Length == 0 ? "no" : hits.Length.ToString(),
                                      hits.Length == 0 ? null : hits[0]);
        }).ToList();
    }

    private void Textures_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowTexture();

    private void ShowTexture_Click(object sender, RoutedEventArgs e) => ShowTexture();

    private void ShowTexture()
    {
        if (Textures.SelectedItem is MeshTextureRow { Gid: { } gid }) _host.Reveal("textures", gid);
    }

    private void CopyTexture_Click(object sender, RoutedEventArgs e)
    {
        if (Textures.SelectedItem is MeshTextureRow row) Clipboard.SetText(row.Name);
    }

    private void CopySkn_Click(object sender, RoutedEventArgs e)
    {
        if (Skn.Text.Length > 0) Clipboard.SetText(Skn.Text);
    }

    private void ExportSkn_Click(object sender, RoutedEventArgs e)
    {
        if (Skn.Text.Length == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export skins",
            Filter = "Skins (*.skn)|*.skn",
            FileName = RawExporter.SafeName(_name.Trim()) + ".skn",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string path = dlg.FileName, text = Skn.Text;
        Jobs.Run($"export skins {_name} -> {path}", "mesh", _ =>
        {
            File.WriteAllText(path, text);
            return $"{text.Length:N0} chars";
        });
    }
}
