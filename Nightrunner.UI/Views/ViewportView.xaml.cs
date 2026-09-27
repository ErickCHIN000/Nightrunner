using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;
using Nightrunner.UI.Viewport;

namespace Nightrunner.UI.Views;

/// <summary>Ask the Viewport to show one submesh of the mesh it is showing, or everything again (null entry).</summary>
public sealed record IsolateRequest(RpackCatalog Catalog, int Gid, int? Entry, int? Submesh);

/// <summary>A skin in a picker. Hidden-in-editor skins (<c>FilterInEditor</c>) are listed, dimmed.</summary>
public sealed record SkinItem(int Index, string Name, bool Filtered)
{
    public double Opacity => Filtered ? 0.45 : 1.0;
    public string Tip => Filtered ? "FilterInEditor" : Name;
    public override string ToString() => Name;
}

/// <summary>
/// The 3D view of what is selected: a mesh (drawn with the skin picked for it) or a whole <c>.model</c> (every slot's
/// drawn mesh, per-slot visibility and skin, in bind pose). Bones draws the skeleton over either.
/// </summary>
public partial class ViewportView : UserControl
{
    /// <summary>Bones is off by default and remembered for the session, across viewports.</summary>
    private static bool _bonesOn;

    private readonly Workspace _ws;
    private readonly Selection _selection;
    private object? _shown;                  // (catalog, gid) for a mesh, the ModelEntry for a model
    private int _loads;
    private bool _picking;

    // mesh mode
    private MeshModel? _model;
    private MeshSkins? _skins;
    private int _gid = -1;
    private RpackCatalog? _catalog;

    // both modes
    private List<ScenePart> _parts = [];
    private SdbFile? _sdb;
    private SceneSkeleton? _skeleton;
    /// <summary>The skeleton the shown scene's skins index (merged for a model, the mesh's own for a mesh).</summary>
    private ModelSkeleton? _bones;

    /// <summary>Mods toggle: names resolve against everything the game loads (on) or the stock packs and paks (off).</summary>
    private bool _mods;
    /// <summary>Follow toggle: a played clip loads its rig's model (on) or plays on the shown one (off).</summary>
    private bool _follow;
    /// <summary>The view the shown content was resolved against.</summary>
    private ContentView? _view;
    /// <summary>What was asked for, to show again under the other view when Mods flips.</summary>
    private ModelCatalog? _shownModels;
    private PrefabCatalog? _shownPrefabs;
    /// <summary>The base clip playing, to play again (Mods or Follow flipped).</summary>
    private Selected.Sequence? _seqSel;

    // model mode
    private ModelDocument? _doc;
    private readonly Dictionary<int, (int Gid, MeshModel Mesh)> _slotMeshes = [];
    private readonly HashSet<int> _hiddenSlots = [];

    public ViewportView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        InitializeComponent();
        ModsToggle.IsChecked = _mods = _ws.Settings.ViewportMods;
        FollowToggle.IsChecked = _follow = _ws.Settings.FollowRig;
        BonesToggle.IsChecked = _bonesOn;
        View.Bones = _bonesOn;
        View.JointClicked += j =>
        {
            if (_skeleton is { } s) _ws.SelectBone(s.Names[j]);
        };
        _selection.Changed += OnSelection;
        _ws.Opened += OnWorkspaceOpened;
        _ws.SkinSelected += OnSkinSelected;
        _ws.BoneSelected += OnBoneSelected;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            BonesToggle.IsChecked = View.Bones = _bonesOn;
            if (_shown is null) OnSelection(_selection.Current, _selection.Source);
        };
    }

    public void Detach()
    {
        _selection.Changed -= OnSelection;
        _ws.Opened -= OnWorkspaceOpened;
        _ws.SkinSelected -= OnSkinSelected;
        _ws.BoneSelected -= OnBoneSelected;
        View.Show(null);
    }

    public void FocusSearch() => View.Focus();

    /// <summary>
    /// The viewport's keys, wherever focus is inside this window — the 3D view takes focus on click, so they are
    /// handled on the way down, not by whichever control holds focus. Text boxes and pickers keep their keys.
    /// </summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is TextBoxBase or ComboBox or ComboBoxItem) return;
        switch (e.Key)
        {
            case Key.Space when _clip is not null:
                SetPlaying(!_playing);
                break;
            case Key.F:
                Frame_Click(this, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Focus the 3D view, as a click does, and press <paramref name="key"/> there (dock check).</summary>
    public void PressInView(Key key)
    {
        View.Focus();
        var target = Keyboard.FocusedElement as UIElement ?? View;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
    }

    public string StatusText => Status.Text;

    /// <summary>The skin shown (mesh mode), or −1.</summary>
    public int CurrentSkin => (SkinPicker.SelectedItem as SkinItem)?.Index ?? -1;

    public void Reveal(object payload)
    {
        switch (payload)
        {
            case IsolateRequest r when _catalog is { } c && ReferenceEquals(c, r.Catalog) && _doc is null &&
                                       (_gid == r.Gid || _shown is ValueTuple<RpackCatalog, int> s && s.Item2 == r.Gid):
                View.Isolate(r.Entry is { } e && r.Submesh is { } sm ? (0, e, sm) : null);
                break;
            case IsolateRequest r:
                _ = LoadMesh(r.Catalog, r.Gid);
                break;
        }
    }

    private void OnWorkspaceOpened()
    {
        _loads++;
        _shown = null;
        _shownModels = null;
        _shownPrefabs = null;
        _seqSel = null;
        _view = null;
        Clear();
        Shown("");
    }

    private void Clear()
    {
        _model = null;
        _skins = null;
        _doc = null;
        _gid = -1;
        _catalog = null;
        _parts = [];
        _skeleton = null;
        _bones = null;
        SyncEye();
        _ws.ShownSkeleton = null;
        StopClip();
        _slotMeshes.Clear();
        _hiddenSlots.Clear();
        View.Show(null);
        View.ShowSkeleton(null);
        ShowSkins(null, -1);
        SlotsPanel.Children.Clear();
    }

    private void OnSelection(Selected? what, string source)
    {
        switch (what)
        {
            case Selected.Mesh m: _ = LoadMesh(m.Catalog, m.Gid); break;
            case Selected.Model m: _ = LoadModel(m.Models, m.Entry); break;
            case Selected.Sequence q: _ = PlaySequence(q); break;
            case Selected.Prefab pf: _ = LoadPrefab(pf.Prefabs, pf.Entry, pf.Presets); break;
        }
    }

    private void OnSkinSelected(RpackCatalog catalog, int gid, int skin)
    {
        if (!ReferenceEquals(catalog, _catalog)) return;
        if (_doc is not null)
        {
            if (_slotMeshes.Values.Any(v => v.Gid == gid)) _ = ReapplyModelMaterials();
            return;
        }
        if (gid != _gid || skin == CurrentSkin) return;
        ShowSkins(_skins, skin);
        _ = ApplySkin(skin);
    }

    private void OnBoneSelected(string name)
    {
        if (_skeleton is not { } s) return;
        int i = Array.FindIndex(s.Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        View.HighlightJoint(i >= 0 ? i : null);
    }

    // ---- mesh ------------------------------------------------------------------------------------------------

    /// <param name="rigid">Pose an unskinned mesh by its entities (an object clip's gate, chest or door): each part follows
    /// the entity that owns it.</param>
    private async Task LoadMesh(RpackCatalog catalog, int gid, bool rigid = false)
    {
        if (_shown is ValueTuple<RpackCatalog, int> s && ReferenceEquals(s.Item1, catalog) && s.Item2 == gid && (!rigid || _bones is not null)) return;
        if (!ReferenceEquals(catalog, _ws.Catalog)) return;
        int load = ++_loads;
        _shown = (catalog, gid);
        string name = catalog.Name(gid);
        Busy(name);
        var total = Stopwatch.StartNew();
        using var op = Log.Start("viewport", name);
        // Mods off: the stock copy of a modded resource; one only a mod has is drawn as the game loads it
        var view = _ws.Content(_mods);
        string modOnly = "";
        if (view.Resource(gid) is int stock) gid = stock;
        else
        {
            view = _ws.Content(true);
            modOnly = "  ·  mod";
        }
        try
        {
            var decode = Stopwatch.StartNew();
            var model = await _ws.Mesh(gid);
            decode.Stop();
            _sdb = (await _ws.Sdb.EnsureAsync())?.File;
            if (load != _loads) return;
            Clear();
            _catalog = catalog;
            _view = view;
            _gid = gid;
            SetMode(model: false);
            var skins = model.SkinRaw is { } raw ? MeshSkins.Decode(raw) : null;
            if (skins?.Error is not null) skins = null;
            int skin = _ws.SkinOf(catalog, gid) ?? skins?.DefaultIndex ?? -1;
            _model = model;
            _skins = skins;
            ShowSkins(skins, skin);

            var tex = Stopwatch.StartNew();
            var notes = new List<string>();
            bool textured = Textured.IsChecked == true;
            bool byEntity = rigid && !model.Skinned && model.Entities.Length > 0;
            var parts = await Task.Run(() => byEntity ? RigidParts(model, MeshScenes.Parts(model)) : MeshScenes.Parts(model));
            _parts = parts;
            var materials = await Task.Run(() => MeshMaterials(skin, textured, notes));
            tex.Stop();
            if (load != _loads) return;
            _bones = model.Skinned || byEntity ? ModelSkeleton.FromMesh(model, name) : null;
            SyncEye();
            _ws.ShownSkeleton = _bones;
            _skeleton = _bones is { } mb ? MeshScenes.Skeleton(mb) : null;
            var upload = Stopwatch.StartNew();
            var meshSkins = _bones is { } sb ? new Dictionary<int, SceneSkin> { [0] = byEntity ? RigidSkin(model, sb) : MeshScenes.Skin(model, sb) } : null;
            View.Show(new SceneModel(name, parts, materials, meshSkins));
            View.ShowSkeleton(_skeleton);
            upload.Stop();
            await FirstFrame();
            View.Frame();       // bounds exist only once the scene has been attached and drawn
            total.Stop();
            int textures = materials.Values.Distinct().Sum(m => (m.Albedo is null ? 0 : 1) + (m.Normal is null ? 0 : 1));
            long tris = parts.Sum(p => (long)p.Indices.Length / 3);
            op.Result = $"{model.Layout}, {parts.Count} parts, {tris:N0} triangles, {textures} textures - decode " +
                        $"{decode.ElapsedMilliseconds} ms, textures {tex.ElapsedMilliseconds} ms, upload {upload.ElapsedMilliseconds} ms, " +
                        $"first frame {total.ElapsedMilliseconds} ms";
            foreach (var n in notes) Log.Warn("viewport", $"{name}: {n}");
            Shown($"{model.Layout}  ·  {parts.Count} parts  ·  {tris:N0} tris  ·  {textures} tex{modOnly}  ·  {total.ElapsedMilliseconds} ms");
            if (View.RenderError is { } err) Fail(err);
        }
        catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or RpackFormatException or ObjectDisposedException)
        {
            if (load != _loads) return;
            op.Failed(e.Message);
            Fail(e.Message, logged: true);
        }
    }

    /// <summary>Unskinned parts bound whole to the entity owning their geometry entry (weight 1), so a pose moves them.</summary>
    private static List<ScenePart> RigidParts(MeshModel model, List<ScenePart> parts)
    {
        var owner = model.GeometryEntries.ToDictionary(e => e.Index, e => e.OwnerEntity is int o && o >= 0 && o < model.Entities.Length ? o : 0);
        return parts.Select(p =>
        {
            if (p.Joints is not null) return p;
            int n = p.Positions.Length / 3;
            var joints = new int[n * 4];
            var weights = new float[n * 4];
            int e = owner.GetValueOrDefault(p.Entry);
            for (int v = 0; v < n; v++)
            {
                joints[v * 4] = e;
                weights[v * 4] = 1;
            }
            return p with { Joints = joints, Weights = weights };
        }).ToList();
    }

    /// <summary>The skin of <see cref="RigidParts"/>: parts are drawn at their owner's global, so its inverse is the bind.</summary>
    private static SceneSkin RigidSkin(MeshModel model, ModelSkeleton skeleton)
    {
        var g = model.EntityGlobals();
        return new SceneSkin(skeleton.Map(model), g.Select(Nightrunner.Core.Cast.ModelCast.Invert).ToArray());
    }

    /// <summary>Textures per slot under a skin; plain grey materials when Textures is off.</summary>
    private Dictionary<(int, int), SceneMaterial> MeshMaterials(int skin, bool textured, List<string> notes)
    {
        if (_model is not { } model || _view is not { } view) return [];
        var slots = _parts.Select(p => p.Slot).Distinct().ToList();
        if (!textured) return slots.ToDictionary(slot => (0, slot), _ => new SceneMaterial(null, null));
        var map = _skins?.MaterialsFor(skin, model.Materials.Length) ?? Enumerable.Range(0, model.Materials.Length).ToArray();
        return MeshScenes.Materials(model, slots, map, _sdb, view.Catalog, notes, view.Surfaces, view.Textures)
            .ToDictionary(kv => (0, kv.Key), kv => kv.Value);
    }

    private async Task ApplySkin(int skin)
    {
        if (_model is null || _catalog is not { } catalog) return;
        var notes = new List<string>();
        var sw = Stopwatch.StartNew();
        bool textured = Textured.IsChecked == true;
        int gid = _gid;
        var materials = await Task.Run(() => MeshMaterials(skin, textured, notes));
        if (gid != _gid) return;
        View.SetMaterials(materials);
        string skinName = _skins is { } k && skin >= 0 && skin < k.Skins.Count ? k.Skins[skin].NameStr : "";
        Log.Info("viewport", $"{catalog.Name(gid)}: skin '{skinName}', {sw.ElapsedMilliseconds} ms");
        foreach (var n in notes) Log.Warn("viewport", $"{catalog.Name(gid)}: {n}");
    }

    private void ShowSkins(MeshSkins? skins, int selected)
    {
        _picking = true;
        var items = skins?.Skins.Select(k => new SkinItem(k.Index, k.NameStr.Length > 0 ? k.NameStr : $"#{k.Index}", k.FilterInEditor)).ToList() ?? [];
        SkinPicker.ItemsSource = items;
        SkinPicker.SelectedItem = items.FirstOrDefault(i => i.Index == selected);
        SkinPicker.IsEnabled = items.Count > 0;
        _picking = false;
    }

    private void SkinPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_picking || SkinPicker.SelectedItem is not SkinItem item || _catalog is not { } c || _gid < 0) return;
        _ws.SelectSkin(c, _gid, item.Index);
        _ = ApplySkin(item.Index);
    }

    /// <summary>Pick a skin by index, as the Skin list does (used by the dock check).</summary>
    public void PickSkin(int index)
    {
        if (SkinPicker.ItemsSource is IEnumerable<SkinItem> items && items.FirstOrDefault(i => i.Index == index) is { } item)
            SkinPicker.SelectedItem = item;
    }

    // ---- model -----------------------------------------------------------------------------------------------

    private int SkinFor(int gid, MeshModel mesh)
    {
        if (_ws.SkinOf(_ws.Catalog, gid) is { } s) return s;
        return mesh.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } k ? k.DefaultIndex : -1;
    }

    private MeshModel Decode(int gid) => _ws.Mesh(gid).GetAwaiter().GetResult();

    private async Task LoadModel(ModelCatalog models, ModelEntry entry)
    {
        if (ReferenceEquals(_shown, entry)) return;
        int load = ++_loads;
        _shown = entry;
        _shownModels = models;
        string name = entry.Basename;
        Busy(name);
        var total = Stopwatch.StartNew();
        using var op = Log.Start("viewport", name);
        var catalog = _ws.Catalog;
        // Mods off: the stock paks' .model and the stock packs' meshes; a .model only a mod has is drawn as the game loads it
        var view = _ws.Content(_mods);
        string modOnly = "";
        if (view.Stock)
        {
            if (view.Model(entry) is { } stock) (models, entry) = (view.Models!, stock);
            else
            {
                view = _ws.Content(true);
                modOnly = "  ·  mod";
            }
        }
        else if (!ReferenceEquals(models, _ws.Models) && _ws.Models?.Find(entry.Basename) is { } loaded)
            (models, entry) = (_ws.Models, loaded);   // picked under the stock view: what the game loads now
        var lookup = view.Catalog;
        try
        {
            _sdb = (await _ws.Sdb.EnsureAsync())?.File;
            var sdb = _sdb;
            var resolve = Stopwatch.StartNew();
            var doc = await Task.Run(() => models.Load(entry));
            await _ws.Prefetch(doc, lookup);
            var res = await Task.Run(() => ModelResolver.Resolve(doc, lookup, sdb, Decode, SkinFor));
            resolve.Stop();
            if (load != _loads) return;
            Clear();
            _catalog = catalog;
            _view = view;
            _doc = doc;
            SetMode(model: true);
            for (int i = 0; i < res.Slots.Count; i++)
                if (res.Slots[i].Meshes.FirstOrDefault(m => m.Drawn) is { Found: true, Error: null } drawn)
                    _slotMeshes[i] = (drawn.Gids[0], Decode(drawn.Gids[0]));

            var tex = Stopwatch.StartNew();
            var notes = new List<string>();
            bool textured = Textured.IsChecked == true;
            var (parts, materials, skeleton) = await Task.Run(() =>
            {
                var parts = _slotMeshes.SelectMany(kv => MeshScenes.Parts(kv.Value.Mesh, 0, kv.Key)).ToList();
                var mats = ModelMaterials(res, parts, textured, notes);
                MeshModel? skel = res.SkeletonGids.Length > 0 ? Decode(res.SkeletonGids[0]) : null;
                var merged = ModelSkeleton.Merge(skel, res.Skeleton ?? "", _slotMeshes.Values.Select(v => (catalog.Name(v.Gid), v.Mesh)).ToList());
                return (parts, mats, merged);
            });
            var modelSkins = _slotMeshes.Where(kv => kv.Value.Mesh.Skinned)
                .ToDictionary(kv => kv.Key, kv => MeshScenes.Skin(kv.Value.Mesh, skeleton));
            tex.Stop();
            if (load != _loads) return;
            _parts = parts;
            _bones = skeleton;
            SyncEye();
            _ws.ShownSkeleton = skeleton;
            _skeleton = MeshScenes.Skeleton(skeleton);
            BuildSlots(res);
            var upload = Stopwatch.StartNew();
            View.Show(new SceneModel(name, parts, materials, modelSkins));
            View.ShowSkeleton(_skeleton);
            View.Visible = p => !_hiddenSlots.Contains(p.Mesh);
            upload.Stop();
            await FirstFrame();
            View.Frame();
            total.Stop();
            long tris = parts.Sum(p => (long)p.Indices.Length / 3);
            int slotsDrawn = _slotMeshes.Count;
            op.Result = $"{res.Slots.Count} slots, {slotsDrawn} drawn, {parts.Count} parts, {tris:N0} triangles, " +
                        $"{skeleton.Names.Count} bones ({skeleton.Overrides.Count} rest overrides) - resolve " +
                        $"{resolve.ElapsedMilliseconds} ms, textures {tex.ElapsedMilliseconds} ms, upload {upload.ElapsedMilliseconds} ms, " +
                        $"first frame {total.ElapsedMilliseconds} ms";
            foreach (var n in notes.Distinct()) Log.Warn("viewport", $"{name}: {n}");
            Shown($"model  ·  {slotsDrawn}/{res.Slots.Count} slots  ·  {tris:N0} tris  ·  {skeleton.Names.Count} bones{modOnly}  ·  {total.ElapsedMilliseconds} ms");
            if (View.RenderError is { } err) Fail(err);
        }
        catch (Exception e) when (e is ModelFormatException or MeshFormatException or MeshUnsupportedException or RpackFormatException or ObjectDisposedException or System.IO.IOException)
        {
            if (load != _loads) return;
            op.Failed(e.Message);
            Fail(e.Message, logged: true);
        }
    }

    /// <summary>
    /// Per (slot, material slot): the surface of the resolved model material (base .mat after the join, with the
    /// model's texture overrides); materials build in parallel and are shared for the session.
    /// </summary>
    private Dictionary<(int, int), SceneMaterial> ModelMaterials(ResolvedModel res, List<ScenePart> parts, bool textured, List<string> notes)
    {
        var result = new ConcurrentDictionary<(int, int), SceneMaterial>();
        var sdb = _sdb;
        var view = _view ?? _ws.Content(true);
        var catalog = view.Catalog;
        Parallel.ForEach(parts.Select(p => (p.Mesh, p.Slot)).Distinct().ToList(), key =>
        {
            var drawn = res.Slots[key.Mesh].Meshes.First(m => m.Drawn);
            var sub = drawn.Submeshes.FirstOrDefault(s => s.Slot == key.Slot);
            if (sub is null || !textured) { result[key] = new SceneMaterial(null, null); return; }
            var overrides = sub.Textures.Where(t => t.Source.StartsWith("model override", StringComparison.Ordinal))
                .ToDictionary(t => t.Param, t => t.Texture);
            var r = MeshScenes.Resolve(sdb, sub.BaseMaterial);
            var surface = MeshScenes.Surface(r.Material, sub.BaseMaterial, overrides, catalog, view.Surfaces, view.Textures, r.Problem);
            result[key] = surface;
            string slot = res.Slots[key.Mesh].Slot.Name;
            lock (notes) notes.AddRange(surface.Notes.Select(n => $"[{slot}] {sub.BaseMaterial}: {n}"));
        });
        return new Dictionary<(int, int), SceneMaterial>(result);
    }

    private async Task ReapplyModelMaterials()
    {
        if (_doc is not { } doc || _view is not { } view) return;
        var catalog = view.Catalog;
        var sdb = _sdb;
        bool textured = Textured.IsChecked == true;
        var notes = new List<string>();
        var parts = _parts;
        var materials = await Task.Run(() => ModelMaterials(ModelResolver.Resolve(doc, catalog, sdb, Decode, SkinFor), parts, textured, notes));
        if (!ReferenceEquals(doc, _doc)) return;
        View.SetMaterials(materials);
        foreach (var n in notes.Distinct()) Log.Warn("viewport", n);
    }

    /// <summary>The Slots popup: one row per slot — visible, the mesh the game draws, and that mesh's skin.</summary>
    private void BuildSlots(ResolvedModel res)
    {
        SlotsPanel.Children.Clear();
        for (int i = 0; i < res.Slots.Count; i++)
        {
            int slot = i;
            var rs = res.Slots[i];
            var drawn = rs.Meshes.FirstOrDefault(m => m.Drawn);
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var check = new CheckBox { IsChecked = !_hiddenSlots.Contains(slot), IsEnabled = _slotMeshes.ContainsKey(slot), Width = 22, VerticalAlignment = VerticalAlignment.Center };
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _hiddenSlots.Remove(slot); else _hiddenSlots.Add(slot);
                View.Visible = p => !_hiddenSlots.Contains(p.Mesh);
            };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            var label = new TextBlock
            {
                Text = rs.Slot.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Skin.Brush(_slotMeshes.ContainsKey(slot) ? "FgBright" : "FgDim"),
                ToolTip = drawn is null ? "no mesh" : drawn.Found ? drawn.Entry.Name : $"{drawn.Entry.Name} (missing)",
            };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            if (_slotMeshes.TryGetValue(slot, out var sm) && sm.Mesh.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } skins && skins.Skins.Count > 0)
            {
                var items = skins.Skins.Select(k => new SkinItem(k.Index, k.NameStr.Length > 0 ? k.NameStr : $"#{k.Index}", k.FilterInEditor)).ToList();
                var combo = new ComboBox { Width = 170, ItemsSource = items, ItemTemplate = SkinPicker.ItemTemplate };
                combo.SelectedItem = items.FirstOrDefault(x => x.Index == SkinFor(sm.Gid, sm.Mesh));
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is SkinItem it && _catalog is { } c) _ws.SelectSkin(c, sm.Gid, it.Index);
                };
                row.Children.Add(combo);
            }
            else row.Children.Add(new TextBlock { Text = drawn?.Entry.Name ?? "", Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center });
            SlotsPanel.Children.Add(row);
        }
    }

    /// <summary>Look straight at a bone of the shown skeleton from the front, fitting <paramref name="radius"/> around it.</summary>
    public void FrameBone(string bone, double radius)
    {
        if (_skeleton is not { } s) return;
        int i = Array.FindIndex(s.Names, n => n.Equals(bone, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) View.FrameOn(new System.Windows.Media.Media3D.Point3D(s.Positions[i * 3], s.Positions[i * 3 + 1], s.Positions[i * 3 + 2]), radius);
    }

    /// <summary>Hide or show one slot of the model shown (used by the Model inspector and the dock check).</summary>
    public void SetSlotVisible(int slot, bool visible)
    {
        if (visible) _hiddenSlots.Remove(slot); else _hiddenSlots.Add(slot);
        View.Visible = p => !_hiddenSlots.Contains(p.Mesh);
        if (_doc is not null && SlotsPanel.Children.Count > slot && SlotsPanel.Children[slot] is DockPanel row && row.Children[0] is CheckBox c)
            c.IsChecked = visible;
    }

    // ---- prefab ----------------------------------------------------------------------------------------------

    public const int MaxPrefabMeshes = 600;

    /// <summary>
    /// A prefab: every mesh its components draw, child entities expanded (<see cref="PrefabPlacement"/>), each mesh
    /// placed by its transform and drawn with its skin. Static: no skeleton, no pose.
    /// </summary>
    private async Task LoadPrefab(PrefabCatalog prefabs, PrefabEntry entry, string? presets = null)
    {
        if (_shown is ValueTuple<PrefabEntry, string?> was && ReferenceEquals(was.Item1, entry) && was.Item2 == presets) return;
        int load = ++_loads;
        _shown = (entry, presets);
        _shownPrefabs = prefabs;
        Busy(entry.Name);
        var total = Stopwatch.StartNew();
        using var op = Log.Start("viewport", $"prefab {entry.Name}");
        // Mods off: the stock packs' prefab of that name and stock meshes; a prefab only a mod has is drawn as the game loads it
        var view = _ws.Content(_mods);
        string modOnly = "";
        if (view.Stock && !PrefabIn(view, entry))
        {
            if (prefabs.Prefabs.FirstOrDefault(p => p.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) && PrefabIn(view, p)) is { } stock)
                entry = stock;
            else
            {
                view = _ws.Content(true);
                modOnly = "  ·  mod";
            }
        }
        var catalog = view.Catalog;
        try
        {
            _sdb = (await _ws.Sdb.EnsureAsync())?.File;
            var notes = new List<string>();
            var rig = _ws.PrefabRig;
            var placements = await Task.Run(() => PrefabPlacement.Meshes(prefabs, entry, notes, rig, presets));
            if (placements.Count > MaxPrefabMeshes)
            {
                notes.Add($"{placements.Count} meshes, first {MaxPrefabMeshes} shown");
                placements = placements.Take(MaxPrefabMeshes).ToList();
            }
            var gids = placements.Select(p => p.Mesh).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n, n => catalog.Lookup(n, 0x10) is { Length: > 0 } g ? g[0] : -1, StringComparer.OrdinalIgnoreCase);
            foreach (var (n, g) in gids.Where(kv => kv.Value < 0)) notes.Add($"{n}: no loaded pack provides it");
            // held here for the whole build: a prefab names up to 600 distinct meshes, far more than the Workspace keeps,
            // and asking the Workspace again decoded every evicted one a second time
            var decoded = gids.Values.Where(g => g >= 0).Distinct().ToDictionary(g => g, g => _ws.Mesh(g));
            await Task.WhenAll(decoded.Values.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            bool textured = Textured.IsChecked == true;
            var sdb = _sdb;
            var (parts, materials) = await Task.Run(() =>
            {
                var parts = new List<ScenePart>();
                var materials = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), SceneMaterial>();
                for (int k = 0; k < placements.Count; k++)
                {
                    int gid = gids[placements[k].Mesh];
                    if (gid < 0) continue;
                    MeshModel mesh;
                    try { mesh = decoded[gid].GetAwaiter().GetResult(); }
                    catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or RpackFormatException)
                    {
                        lock (notes) notes.Add($"{placements[k].Mesh}: {e.Message}");
                        continue;
                    }
                    var own = MeshScenes.Parts(mesh, 0, k).Select(p => MeshScenes.Transformed(p, placements[k].Transform)).ToList();
                    parts.AddRange(own);
                    var slots = own.Select(p => p.Slot).Distinct().ToList();
                    if (!textured) { foreach (int s in slots) materials[(k, s)] = new SceneMaterial(null, null); continue; }
                    int skin = -1;
                    var skins = mesh.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } d ? d : null;
                    if (skins is not null)
                        skin = placements[k].Skin is { Length: > 0 } sn && skins.Skins.FindIndex(x => x.NameStr.Equals(sn, StringComparison.OrdinalIgnoreCase)) is >= 0 and var si
                            ? si : skins.DefaultIndex;
                    var map = skins?.MaterialsFor(skin, mesh.Materials.Length) ?? Enumerable.Range(0, mesh.Materials.Length).ToArray();
                    foreach (var (slot, mat) in MeshScenes.Materials(mesh, slots, map, sdb, catalog, notes, view.Surfaces, view.Textures))
                        materials[(k, slot)] = mat;
                }
                return (parts, new Dictionary<(int, int), SceneMaterial>(materials));
            });
            if (load != _loads) return;
            Clear();
            _catalog = _ws.Catalog;
            _view = view;
            SetMode(model: false);
            SkinLabel.Visibility = SkinPicker.Visibility = Visibility.Collapsed;
            _parts = parts;
            View.Show(new SceneModel(entry.Name, parts, materials));
            await FirstFrame();
            View.Frame();
            total.Stop();
            long tris = parts.Sum(p => (long)p.Indices.Length / 3);
            op.Result = $"{placements.Count} meshes ({gids.Count} distinct), {parts.Count} parts, {tris:N0} triangles, {total.ElapsedMilliseconds} ms";
            foreach (var n in notes.Distinct().Take(20)) Log.Warn("viewport", $"{entry.Name}: {n}");
            Shown(placements.Count == 0
                ? "prefab · no meshes"
                : $"prefab  ·  {placements.Count} meshes  ·  {tris:N0} tris{modOnly}  ·  {total.ElapsedMilliseconds} ms");
        }
        catch (Exception e) when (e is PrefabFormatException or RpackFormatException or ObjectDisposedException)
        {
            if (load != _loads) return;
            op.Failed(e.Message);
            Fail(e.Message, logged: true);
        }
    }

    /// <summary>Whether a prefab comes from a pack or pak of <paramref name="view"/>.</summary>
    private bool PrefabIn(ContentView view, PrefabEntry p)
    {
        if (!view.Stock) return true;
        if (p.Source == PrefabSource.Pak) return view.HasPath(p.Pack);
        return _ws.Catalog.Packs.FirstOrDefault(e => e.Label == p.Pack) is not { } pack || view.HasPath(pack.Path);
    }

    // ---- animation -------------------------------------------------------------------------------------------

    private IClipPose? _clip;

    /// <summary>A clip is playing (not paused).</summary>
    public bool IsPlaying => _playing;
    public SeqEntry? PlayingSequence => _seq;
    public string? ShownModel => (_shown as ModelEntry)?.Basename;
    private SeqEntry? _seq;
    private double _frame, _from, _to;
    private bool _playing, _scrubbing;
    private TimeSpan _lastTick;

    /// <summary>
    /// Play a SeqTrack on the shown model or mesh: its clip decoded, tracks bound to the skeleton by name hash, looping
    /// over the track's frame range at its fps. The model follows the clip's rig (<see cref="AnimRigs"/>): the rig
    /// binding the most tracks (an FPP clip loads the FPP player, a biter clip a biter); a shown mesh whose skeleton binds
    /// as well is kept. An object's clip (a gate, a chest, a cable) plays on the mesh whose entities it names.
    /// </summary>
    /// <param name="again">Play it again as the base clip (Mods or Follow flipped), even with Layer on.</param>
    private async Task PlaySequence(Selected.Sequence q, bool again = false)
    {
        var entry = q.Entry;
        ClearError();
        if (entry.Record.IsPlaceholder) { Fail($"{entry.Record.Name}: placeholder (no clip)"); return; }
        if (LayerToggle.IsChecked == true && _clip is not null && !again)
        {
            await AddLayer(q.Animations, entry);
            return;
        }
        if (ClipOf(q.Animations, entry.Record.Anm2Name) is not { } where)
        {
            Fail($"{entry.Record.Name}: clip {entry.Record.Anm2Name} is not shipped in any loaded pack");
            return;
        }
        _seqSel = q;
        try
        {
            var sw = Stopwatch.StartNew();
            var clip = await Task.Run(() => Anm2Resource.Decode(where.Pack, where.Index));
            // lip-sync and facial clips weight head poses (CSolidHeadController): a face channel on a shown head
            if (clip.IsPoseWeights)
            {
                await PlayFace(entry, clip);
                return;
            }
            var (why, pick, fit) = await FollowRig(entry, clip);
            if (why is not null) { Fail($"{entry.Record.Name}: {why}"); return; }
            if (_bones is not { } bones || !View.CanPose) { Fail($"{entry.Record.Name}: {pick.Describe()}; nothing posable is shown"); return; }
            var pose = await Task.Run(() => new AnimPose(clip, bones));
            Start(pose, entry);
            ClipNote(fit);
            Log.Info("viewport", $"{entry.Bank}@{entry.Record.Name}: {entry.Record.Anm2Name} ({where.Label}), {pose.Clip.FrameBound} frames at {entry.Record.Fps:0.##} fps, " +
                                 $"{pose.BoundBones}/{bones.Names.Count} bones bound ({pose.Binding.Misses.Length} tracks unbound), {sw.ElapsedMilliseconds} ms");
            if (pose.BoundBones * 3 < bones.Names.Count(n => !n.Contains("camera", StringComparison.OrdinalIgnoreCase)) / 2)
                Log.Info("viewport", $"{entry.Record.Name}: drives {pose.BoundBones} of {bones.Names.Count} bones — a layer or another rig's clip; the rest stays in bind pose");
        }
        catch (Exception e) when (e is AnimBankFormatException or Anm2FormatException or Anm2UnsupportedException or RpackFormatException or ObjectDisposedException)
        {
            Fail(e.Message);
        }
    }

    /// <summary>Make <paramref name="pose"/> the running clip over its SeqTrack's range and start it.</summary>
    private void Start(IClipPose pose, SeqEntry entry)
    {
        StopClip();
        _clip = pose;
        _seq = entry;
        (_from, _to) = Range(entry, pose.Clip);
        _frame = entry.Record.Reverse ? _to : _from;
        Timeline.Minimum = _from;
        Timeline.Maximum = _to;
        AnimBar.Visibility = Visibility.Visible;
        SetPlaying(true);
        ApplyFrame();
    }

    private static (double From, double To) Range(SeqEntry entry, Anm2Clip clip)
    {
        float start = entry.Record.StartFrame, end = entry.Record.EndFrame;
        double from = Math.Clamp(Math.Min(start, end), 0, clip.FrameBound), to = Math.Clamp(Math.Max(start, end), 0, clip.FrameBound);
        return to <= from ? (0, clip.FrameBound) : (from, to);
    }

    private (ModelCatalog? Models, CorrectiveShape[]? Shapes)? _correctives;

    /// <summary>
    /// A facial / lip-sync clip: over the running body clip as a layer (the face mix replaces the face bones), else on
    /// its own over the rest pose. It needs a shown head whose face rig matches the clip's poses; with nothing that
    /// fits on screen and no body clip running, the engine's own template head (<c>solid_head_template_v1/v2</c>) is
    /// loaded.
    /// </summary>
    private async Task PlayFace(SeqEntry entry, Anm2Clip clip)
    {
        var rig = await Task.Run(ShownFaceRigs(clip));
        if (rig is null && _clip is null)
        {
            string template = clip.PoseHashes.Length > FacialPose.V1Poses + FacialPose.WrinkleCount ? "solid_head_template_v2" : "solid_head_template_v1";
            if (_ws.Content(_mods).Catalog.Lookup(template, 0x10) is { Length: > 0 } hits)
            {
                await LoadMesh(_ws.Catalog, hits[0]);
                rig = await Task.Run(ShownFaceRigs(clip));
            }
        }
        if (rig is null || _bones is not { } bones || !View.CanPose)
        {
            Fail($"{entry.Record.Name}: no shown head has the face rig its {clip.PoseHashes.Length} pose weights need");
            return;
        }
        var models = (_view ?? _ws.Content(_mods)).Models;
        if (rig.Poses.Length == FacialPose.V2Poses && _correctives?.Models != models)
            _correctives = (models, models is null ? null : await Task.Run(() => CorrectiveShape.Load(models)));
        var face = new FacialPose(clip, rig, bones, rig.Poses.Length == FacialPose.V2Poses ? CorrectiveShape.ForRig(rig, _correctives?.Shapes) : null);
        if (entry.Bank.Contains("lipsync", StringComparison.OrdinalIgnoreCase))
        {
            // lip-sync slots never drive the eyes: blink and look-at come from elsewhere in the game
            face.GroupWeights[FacialPose.GroupEyelids] = 0;
            face.GroupWeights[FacialPose.GroupEyeballs] = 0;
        }
        Log.Info("viewport", $"{entry.Bank}@{entry.Record.Name}: face {face.BoundBones} bones, {rig.Poses.Length} poses, {clip.FrameBound} frames");
        if (_clip is AnimPose)
        {
            var (from, to) = Range(entry, clip);
            _layers.RemoveAll(l => l.Pose is FacialPose);
            _layers.Add(new Layer(face, entry, from, to));
            ApplyFrame();
        }
        else Start(face, entry);
    }

    /// <summary>The first shown mesh (model slots, or the mesh) carrying a face rig this clip fits.</summary>
    private Func<FaceRig?> ShownFaceRigs(Anm2Clip clip)
    {
        var meshes = _doc is not null ? _slotMeshes.Values.Select(v => v.Mesh).ToList() : _model is { } m ? [m] : [];
        return () =>
        {
            foreach (var mesh in meshes)
            {
                FaceRig? rig;
                try { rig = FaceRig.FromMesh(mesh); }
                catch (MeshFormatException) { continue; }
                if (rig is not null && FacialPose.Mismatch(clip, rig) is null) return rig;
            }
            return null;
        };
    }

    /// <summary>A clip played over the base: its own frame, range and fps (it loops on its own clock).</summary>
    private sealed class Layer(IClipPose pose, SeqEntry seq, double from, double to)
    {
        public IClipPose Pose { get; } = pose;
        public SeqEntry Seq { get; } = seq;
        public double From { get; } = from;
        public double To { get; } = to;
        public double Frame { get; set; } = seq.Record.Reverse ? to : from;
    }

    private readonly List<Layer> _layers = [];
    public int LayerCount => _layers.Count;

    /// <summary>
    /// Play <paramref name="entry"/> over the running clip on the same skeleton (<see cref="AnimPose.Over"/>): the bones
    /// it drives take its pose, or, for an additive clip, its delta after the base. No model switch.
    /// </summary>
    private async Task AddLayer(AnimCatalog animations, SeqEntry entry)
    {
        if (_bones is not { } bones) return;
        ClearError();
        if (ClipOf(animations, entry.Record.Anm2Name) is not { } where)
        {
            Fail($"{entry.Record.Name}: clip {entry.Record.Anm2Name} is not shipped in any loaded pack");
            return;
        }
        try
        {
            var clip = await Task.Run(() => Anm2Resource.Decode(where.Pack, where.Index));
            if (clip.IsPoseWeights)
            {
                await PlayFace(entry, clip);
                return;
            }
            var pose = await Task.Run(() => new AnimPose(clip, bones));
            if (pose.BoundBones == 0) { Fail($"{entry.Record.Name}: drives no bone of the shown skeleton"); return; }
            if (!ReferenceEquals(bones, _bones) || _clip is null) return;
            var (from, to) = Range(entry, pose.Clip);
            _layers.Add(new Layer(pose, entry, from, to));
            Log.Info("viewport", $"layer {_layers.Count}: {entry.Bank}@{entry.Record.Name}, {pose.BoundBones} bones{(pose.IsAdditive ? ", additive" : "")}");
            ApplyFrame();
        }
        catch (Exception e) when (e is AnimBankFormatException or Anm2FormatException or Anm2UnsupportedException or RpackFormatException or ObjectDisposedException)
        {
            Fail(e.Message);
        }
    }

    private void Layer_Click(object sender, RoutedEventArgs e)
    {
        if (LayerToggle.IsChecked == true || _layers.Count == 0) return;
        _layers.Clear();
        ApplyFrame();
    }

    /// <summary>Pause the running clip at <paramref name="frame"/> (dock check).</summary>
    public void PauseAt(double frame)
    {
        if (_clip is null) return;
        SetPlaying(false);
        _frame = Math.Clamp(frame, _from, _to);
        ApplyFrame();
    }

    public void SetLayering(bool on)
    {
        LayerToggle.IsChecked = on;
        Layer_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Show what <paramref name="clip"/> plays on, unless the shown model or mesh already binds as much: the model of the
    /// rig <see cref="AnimRigs.Pick"/> finds, or for an object's clip the mesh whose entities bind at least half its
    /// tracks (<see cref="AnimObjects"/>). Returns why nothing can play it, naming the best rig and its bound count.
    /// With Follow off a posable shown model or mesh stays: the clip plays on it, binding what binds, and
    /// <c>Fit</c> is "n/m tracks bound" when that is a poor fit (<see cref="AnimRigs.PoorFit"/>); the pick is still logged.
    /// Names resolve against the Mods view (<see cref="Workspace.Content"/>).
    /// </summary>
    private async Task<(string? Why, RigPick Pick, string? Fit)> FollowRig(SeqEntry entry, Anm2Clip clip)
    {
        var view = _ws.Content(_mods);
        var models = view.Models;
        var rigs = await Task.Run(() => view.Rigs);
        var shownModel = _shown as ModelEntry;
        string? current = shownModel is not null ? _doc?.Skeleton : null;
        var tracks = clip.TrackHashes;
        var pick = rigs?.Pick(tracks, entry.Record.Name, entry.Bank, current) ?? new RigPick(null, 0, tracks.Length, tracks.Length);
        var hashes = tracks.ToHashSet();
        int shownBound = _bones?.Names.Count(n => hashes.Contains(Anm2Hash.H41(n))) ?? 0;
        bool posable = _bones is not null && View.CanPose;
        int need = Math.Max(1, (pick.Named + 1) / 2);
        Func<int, bool>? inView = view.Stock ? view.Has : null;
        if (!_follow && posable)
        {
            // Follow off: keep what is shown; say what following would have loaded
            string would = pick.Describe();
            if (pick.Rig is null || pick.Object)
            {
                var objects = await Task.Run(() => _ws.AnimObjects);
                if (objects?.Best(tracks, entry.Record.Anm2Name, view.Catalog, inView) is { } hit && hit.Bound >= need)
                    would += $"; object {view.Catalog.Name(hit.Gid)} binds {hit.Bound}/{tracks.Length}";
            }
            else if (models?.Find(AnimRigs.Model(pick.Rig, shownModel?.Basename, entry.Record.Name)) is { } target)
                would += $" ({target.Basename})";
            string fit = $"{shownBound}/{tracks.Length} tracks bound";
            Log.Info("viewport", $"{entry.Record.Name}: kept {ShownModel ?? _catalog?.Name(_gid) ?? "the shown mesh"}, {fit}; {would}");
            return (null, pick, AnimRigs.PoorFit(shownBound, pick) ? fit : null);
        }
        if (pick.Rig is null || pick.Object)
        {
            if (posable && shownModel is null && shownBound >= need && shownBound > pick.Bound) return (null, pick, null);
            var objects = await Task.Run(() => _ws.AnimObjects);
            if (objects?.Best(tracks, entry.Record.Anm2Name, view.Catalog, inView) is { } hit && hit.Bound >= need)
            {
                Log.Info("viewport", $"{entry.Record.Name}: object clip ({pick.Describe()}) -> {view.Catalog.Name(hit.Gid)} ({hit.Bound}/{tracks.Length} tracks)");
                await LoadMesh(_ws.Catalog, hit.Gid, rigid: true);
                return (null, pick, null);
            }
            if (pick.Rig is null)
                return (pick.Named == 0 ? $"its {tracks.Length} tracks are root, attach or camera tracks only" : pick.Describe() + ", nor any mesh", pick, null);
            return ($"object clip: {pick.Describe()}, no mesh binds half of them", pick, null);
        }
        if (models is null) return (null, pick, null);
        if (string.Equals(pick.Rig.Skeleton, current, StringComparison.OrdinalIgnoreCase)) return (null, pick, null);
        if (posable && shownModel is null && AnimRigs.Ties(shownBound, pick.Bound)) return (null, pick, null);
        if (models.Find(AnimRigs.Model(pick.Rig, shownModel?.Basename, entry.Record.Name)) is not { } target2)
            return ($"{pick.Describe()}, but no loaded pak has its models", pick, null);
        Log.Info("viewport", $"{entry.Record.Name}: rig {pick.Describe()} -> {target2.Basename}");
        await LoadModel(models, target2);
        return (null, pick, null);
    }

    /// <summary>The clip a SeqTrack plays under the Mods view; with Mods off, one only a mod ships plays as the game loads it.</summary>
    private ClipRef? ClipOf(AnimCatalog animations, string anm2Name)
    {
        var view = _ws.Content(_mods);
        if (view.Clip(animations, anm2Name) is { } hit) return hit;
        if (!view.Stock || animations.ClipOf(anm2Name) is not { } mod) return null;
        Log.Info("viewport", $"{anm2Name}: only in {mod.Label}, played from it");
        return mod;
    }

    private void StopClip()
    {
        SetPlaying(false);
        if (_clip is not null)
        {
            View.Pose(null);
            if (_bones is { } b) View.ShowSkeleton(MeshScenes.Skeleton(b));
        }
        _clip = null;
        _seq = null;
        _layers.Clear();
        AnimBar.Visibility = Visibility.Collapsed;
    }

    private void SetPlaying(bool on)
    {
        PlayToggle.IsChecked = on;
        if (on == _playing) return;
        _playing = on;
        if (on)
        {
            _lastTick = TimeSpan.Zero;
            CompositionTarget.Rendering += OnTick;
        }
        else CompositionTarget.Rendering -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_clip is null || _seq is null || e is not RenderingEventArgs r) return;
        if (_lastTick == TimeSpan.Zero || r.RenderingTime == _lastTick) { _lastTick = r.RenderingTime; return; }
        double dt = (r.RenderingTime - _lastTick).TotalSeconds;
        _lastTick = r.RenderingTime;
        double fps = _seq.Record.Fps > 0 ? _seq.Record.Fps : 30;
        _frame += dt * fps * (_seq.Record.Reverse ? -1 : 1);
        double span = _to - _from;
        if (span > 0) _frame = _from + ((_frame - _from) % span + span) % span;
        foreach (var l in _layers)
        {
            double lf = l.Frame + dt * (l.Seq.Record.Fps > 0 ? l.Seq.Record.Fps : 30) * (l.Seq.Record.Reverse ? -1 : 1);
            double ls = l.To - l.From;
            l.Frame = ls > 0 ? l.From + ((lf - l.From) % ls + ls) % ls : l.From;
        }
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        if (_clip is null) return;
        var locals = _clip.Locals(_frame);
        foreach (var l in _layers) l.Pose.Over(locals, l.Frame);
        var globals = AnimPose.Globals(_clip.Skeleton.Parents, locals);
        View.Pose(globals);
        if (_bones is { } b) View.ShowSkeleton(MeshScenes.Skeleton(b, globals));
        if (EyeToggle.IsChecked == true && EyeBone() is int eye) View.Eye(globals[eye]);
        _scrubbing = true;
        Timeline.Value = _frame;
        _scrubbing = false;
        FrameText.Text = $"{_frame:0}/{_to:0}";
    }

    private void Play_Click(object sender, RoutedEventArgs e) => SetPlaying(PlayToggle.IsChecked == true);

    private void Timeline_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_scrubbing || _clip is null) return;
        SetPlaying(false);
        _frame = e.NewValue;
        ApplyFrame();
    }

    private void Rest_Click(object sender, RoutedEventArgs e)
    {
        StopClip();
        if (!_failed) ClipNote(null);
    }

    // ---- shared ----------------------------------------------------------------------------------------------

    private void SetMode(bool model)
    {
        SkinLabel.Visibility = SkinPicker.Visibility = model ? Visibility.Collapsed : Visibility.Visible;
        SlotsToggle.Visibility = model ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What was last shown successfully: an error clears back to it.</summary>
    private string _shownText = "";
    /// <summary>The last load's status, before any clip note.</summary>
    private string _loadedText = "";
    private bool _failed;

    private void Busy(string name) => SetStatus($"{name}...", "FgDim");

    private void Shown(string text)
    {
        _loadedText = text;
        _shownText = text;
        SetStatus(text, "FgDim");
    }

    /// <summary>The last load's status after a note about the playing clip (Follow off: how poorly it binds), or without one.</summary>
    private void ClipNote(string? note)
    {
        _shownText = note is null ? _loadedText : $"{note}  ·  {_loadedText}";
        SetStatus(_shownText, "FgDim");
    }

    /// <summary>A new clip or load clears the last error (loads through <see cref="Busy"/>).</summary>
    private void ClearError()
    {
        if (_failed) SetStatus(_shownText, "FgDim");
    }

    /// <summary>
    /// Show an error, trimmed to the bar with the full text as its tooltip, and write the full text to the Log
    /// unless the failed operation already did.
    /// </summary>
    private void Fail(string message, bool logged = false)
    {
        if (!logged) Log.Error("viewport", message);
        SetStatus(message, "Error");
        _failed = true;
    }

    private void SetStatus(string text, string brush)
    {
        _failed = false;
        Status.Text = text;
        Status.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        Status.Foreground = Skin.Brush(brush);
    }

    /// <summary>Completes after the next frame WPF composes (Helix renders on CompositionTarget.Rendering).</summary>
    private static Task FirstFrame()
    {
        var done = new TaskCompletionSource();
        EventHandler? h = null;
        int frames = 0;
        h = (_, _) =>
        {
            if (++frames < 2) return;
            CompositionTarget.Rendering -= h;
            done.TrySetResult();
        };
        CompositionTarget.Rendering += h;
        return done.Task;
    }

    private void Frame_Click(object sender, RoutedEventArgs e)
    {
        EyeToggle.IsChecked = false;
        View.Frame();
    }

    /// <summary>The FPP camera bone the clips drive (<c>eyecamera</c>), when the shown skeleton has one.</summary>
    private int? EyeBone() => _bones?.IndexOf("eyecamera") is >= 0 and var i ? i : null;

    private void SyncEye()
    {
        bool has = EyeBone() is not null;
        EyeToggle.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has && EyeToggle.IsChecked == true)
        {
            EyeToggle.IsChecked = false;
            View.Frame();
        }
    }

    public void SetEye(bool on)
    {
        EyeToggle.IsChecked = on;
        Eye_Click(this, new RoutedEventArgs());
    }

    private void Eye_Click(object sender, RoutedEventArgs e)
    {
        if (EyeToggle.IsChecked != true || EyeBone() is not int eye || _bones is not { } bones) { View.Frame(); return; }
        if (_clip is not null) ApplyFrame();
        else View.Eye(bones.Rest[eye]);
    }

    private void Wire_Click(object sender, RoutedEventArgs e) => View.Wireframe = Wire.IsChecked == true;

    private void Mods_Click(object sender, RoutedEventArgs e) => _ = SetMods(ModsToggle.IsChecked == true);

    private void Follow_Click(object sender, RoutedEventArgs e) => _ = SetFollow(FollowToggle.IsChecked == true);

    public bool Mods => _mods;
    public bool Follow => _follow;

    /// <summary>
    /// Mods on or off (remembered): what is shown is drawn again under the other view, and the playing clip, if any,
    /// plays again on it.
    /// </summary>
    public async Task SetMods(bool on)
    {
        ModsToggle.IsChecked = on;
        if (on == _mods) return;
        _mods = on;
        _ws.Settings.ViewportMods = on;
        _ws.Settings.Save();
        var seq = _clip is not null ? _seqSel : null;
        var shown = _shown;
        _shown = null;
        switch (shown)
        {
            case ModelEntry m when _shownModels is { } models: await LoadModel(models, m); break;
            case ValueTuple<RpackCatalog, int> s: await LoadMesh(s.Item1, s.Item2); break;
            case ValueTuple<PrefabEntry, string?> p when _shownPrefabs is { } prefabs: await LoadPrefab(prefabs, p.Item1, p.Item2); break;
        }
        if (seq is not null) await PlaySequence(seq, again: true);
    }

    /// <summary>Follow on or off (remembered). Turned on while a clip plays, the clip's rig is picked again.</summary>
    public async Task SetFollow(bool on)
    {
        FollowToggle.IsChecked = on;
        if (on == _follow) return;
        _follow = on;
        _ws.Settings.FollowRig = on;
        _ws.Settings.Save();
        if (on && _clip is not null && _seqSel is { } seq) await PlaySequence(seq, again: true);
    }

    private void Bones_Click(object sender, RoutedEventArgs e) => SetBones(BonesToggle.IsChecked == true);

    public void SetBones(bool on)
    {
        _bonesOn = on;
        BonesToggle.IsChecked = on;
        View.Bones = on;
    }

    private void Textured_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is not null) _ = ReapplyModelMaterials();
        else _ = ApplySkin(CurrentSkin);
    }
}
