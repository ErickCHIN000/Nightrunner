using System.Runtime.CompilerServices;

namespace Nightrunner.Core.Prefab;

/// <summary>A mesh a prefab draws, where (4×4 row-major, column vectors) and with which skin; <see cref="Path"/> is the entity chain.</summary>
public sealed record PrefabMesh(string Mesh, string? Skin, double[] Transform, string Path)
{
    /// <summary>
    /// How the part is placed in its prefab: <c>rig</c> (under a hierarchy a vehicle rig element places),
    /// <c>attached</c> (by its <c>m_XformComponent</c>), <c>xform</c> (its own transform chain) or <c>origin</c> (none:
    /// the prefab origin).
    /// </summary>
    public string Placed { get; init; } = "xform";

    /// <summary>The part's transform within its own prefab (before the instancing chain).</summary>
    public double[]? Local { get; init; }

    /// <summary>A distance stand-in (<c>LodVisibility</c> set); drawn only when a prefab has nothing else.</summary>
    public bool Lod { get; init; }
}

/// <summary>
/// The meshes a prefab shows, with its child entities expanded recursively (another prefab instanced by name, placed
/// by the entity's transform, parented by pcid). Mesh components: <c>*::m_MeshName</c> / <c>MeshName</c> values of
/// mesh-render and mesh-logic components (editor helpers excluded), skin from <c>*::m_SkinName</c>. Left out: components
/// and entities whose <c>m_SelfActive</c> is 0 (they start inactive — damage variants), meshes with a
/// <c>LodVisibility</c> value (distance stand-ins; kept when they are all a prefab has), and a mesh a second component
/// draws at the same place.
/// </summary>
/// <remarks>
/// <para>The local transform is the engine's <c>PrefabXformComponent::CreateXform</c>: <c>mtx34::rotation_xyz(rx, ry,
/// rz)</c> (degrees; <c>R = Rx · Ry · Rz</c>, column vectors, read in the engine_core decompile), its columns scaled by
/// the scale, then the translation added, chained through the entity parent pcid and the hierarchy component's parent
/// (+0x68).</para>
/// <para>A component with <c>PrefabComponentWithXformPtr::m_XformComponent</c> (+0x40, <see
/// cref="PrefabComponent.XformComponent"/>) is placed by that xform/hierarchy component. A hierarchy bound to a
/// vehicle proxy's <c>m_Wheel*Hierarchy</c> / <c>m_Door*Hierarchy</c> / <c>m_VisHierarchy</c> field is placed at the
/// rest global of the proxy mesh element its vehicle script names (<see cref="PrefabVehicleRig"/>) when a rig source is
/// given and everything resolves; otherwise it keeps its own transform and a note says why.</para>
/// <para>Values reach a child prefab through its virtual fields: the instancing entity's values, the presets it names
/// (<c>group;preset</c> pairs, the child class's preset sets) and the parent's own overrides aimed at the entity are
/// forwarded to each virtual field's destinations (field on a pcid; an entity destination forwards again). Entity
/// values override preset values; an outer override wins over an inner one.</para>
/// </remarks>
public static class PrefabPlacement
{
    public const int MaxDepth = 12;

    /// <summary>A value arriving at a component field through virtual fields (text, or the raw 8-byte payload).</summary>
    public readonly record struct Override(string? Text, ulong Raw)
    {
        /// <summary>As a flag: text (text prefabs, presets) is on unless "0"; a binary payload by its low byte.</summary>
        public bool On => Text is { } t ? t.Trim() != "0" : (Raw & 0xFF) != 0;
    }

    public static List<PrefabMesh> Meshes(PrefabCatalog catalog, PrefabEntry entry, List<string>? notes = null,
                                          IPrefabRigSource? rig = null, string? presets = null)
    {
        var result = new List<PrefabMesh>();
        var top = presets is { Length: > 0 } ? Incoming(catalog, entry, null, presets, null, 0, notes) : [];
        Walk(catalog, entry, Identity(), entry.Name, 0, result, notes, [], rig, Forward(entry, top));
        // distance stand-ins are left out, unless they are all there is (a *_lod1 prefab viewed on its own)
        var drawn = result.Where(m => !m.Lod).ToList();
        if (drawn.Count == 0) drawn = result;
        // one draw per mesh and place: a mesh-render + mesh-logic pair keeps the one with a skin (the render one)
        return drawn.GroupBy(m => (m.Mesh.ToLowerInvariant(), string.Join(',', m.Transform.Select(v => Math.Round(v, 5)))))
                    .Select(g => g.FirstOrDefault(m => m.Skin is { Length: > 0 }) ?? g.First()).ToList();
    }

    private static void Walk(PrefabCatalog catalog, PrefabEntry entry, double[] world, string path, int depth,
                             List<PrefabMesh> result, List<string>? notes, HashSet<string> stack, IPrefabRigSource? rig,
                             Dictionary<(uint, string), Override> overrides)
    {
        if (depth > MaxDepth || !stack.Add(entry.Name))
        {
            notes?.Add($"{path}: recursion stopped");
            return;
        }
        var root = entry.Root;
        var byPcid = root.Components.GroupBy(c => c.Pcid).ToDictionary(g => g.Key, g => g.First());
        bool isVehicle = false;
        var rigged = rig is null ? [] : Rig(root, byPcid, overrides, rig, path, notes, out isVehicle);
        var memo = new Dictionary<uint, double[]>();
        double[] Local(PrefabComponent c, int guard)
        {
            if (memo.TryGetValue(c.Pcid, out var m)) return m;
            if (guard >= 64) return Identity();
            var own = rigged.TryGetValue(c.Pcid, out var r) ? r : c.Xform is { } x ? Xform(x) : Identity();
            // parent: the entity's parent pcid, the hierarchy's parent, else the component's m_XformComponent
            uint pp = c.Entity?.ParentPcid ?? c.HierarchyParent ?? c.XformComponent ?? 0;
            if (pp != c.Pcid && pp != 0 && byPcid.TryGetValue(pp, out var parent))
                own = Mul(Local(parent, guard + 1), own);
            return memo[c.Pcid] = own;
        }
        string How(PrefabComponent c)
        {
            var at = c;
            for (int guard = 0; guard < 64; guard++)
            {
                if (rigged.ContainsKey(at.Pcid)) return "rig";
                uint pp = at.Entity?.ParentPcid ?? at.HierarchyParent ?? at.XformComponent ?? 0;
                if (pp == 0 || pp == at.Pcid || !byPcid.TryGetValue(pp, out var parent)) break;
                at = parent;
            }
            return c.XformComponent is { } x && byPcid.ContainsKey(x) ? "attached" : c.Xform is not null ? "xform" : "origin";
        }
        foreach (var c in root.Components)
        {
            if (Get(c, overrides, "m_SelfActive") is { } active && !active.On) continue;
            if (c.Entity is { } e)
            {
                string child = e.PrefabName ?? e.EntityPrefabClass ?? "";
                if (child.Length == 0 || catalog.Find(child) is not { } childEntry)
                {
                    if (child.Length > 0) notes?.Add($"{path}/{e.Name}: prefab {child} not registered");
                    continue;
                }
                var incoming = Incoming(catalog, childEntry, c, e.PresetNames, overrides, c.Pcid, notes);
                Walk(catalog, childEntry, Mul(world, Local(c, 0)), $"{path}/{e.Name ?? child}", depth + 1, result, notes, stack, rig,
                     Forward(childEntry, incoming));
                continue;
            }
            if (c.ComponentClass is { } cls && cls.Contains("EditorHelper", StringComparison.Ordinal)) continue;
            string? name = MeshOf(c, overrides);
            if (name is null) continue;
            bool isLod = Get(c, overrides, "LodVisibility") is { } lod && lod.On;
            string? skin = Get(c, overrides, "m_SkinName")?.Text ?? Get(c, overrides, "SkinName")?.Text;
            var local = Local(c, 0);
            result.Add(new PrefabMesh(name[..^4], skin, Mul(world, local), path) { Placed = How(c), Local = local, Lod = isLod });
        }
        // a vehicle's skin preset loads more prefabs (body panels, lights, mirrors on the doors): the Default skin here
        if (isVehicle && PrefabVehicleRig.SkinPrefabs(rig!, PrefabVehicleRig.DefaultSkin) is { } extras)
            foreach (var (prefab, presets, hierarchy) in extras)
            {
                if (catalog.Find(prefab) is not { } extra)
                {
                    notes?.Add($"{path}: skin prefab {prefab} not registered");
                    continue;
                }
                var at = Identity();
                if (hierarchy is not null)
                {
                    var bind = root.PropertyBindings.FirstOrDefault(b => b.Source == "this" && b.Target == "m_" + hierarchy);
                    if (bind is not null && byPcid.TryGetValue(bind.SourcePcid, out var h)) at = Local(h, 0);
                    else notes?.Add($"{path}: skin prefab {prefab}: hierarchy {hierarchy} not bound");
                }
                var incoming = Incoming(catalog, extra, null, presets, null, 0, notes);
                Walk(catalog, extra, Mul(world, at), $"{path}/{prefab}", depth + 1, result, notes, stack, rig, Forward(extra, incoming));
            }
        stack.Remove(entry.Name);
    }

    /// <summary>
    /// The <c>.msh</c> a component draws: a <c>m_MeshName</c> / <c>MeshName</c> arriving through a virtual field (on
    /// any component — a model proxy's <c>MeshName</c> too, as the Dying Light 2 containers use it), else its own
    /// <c>*m_MeshName</c> / <c>MeshName</c> value.
    /// </summary>
    private static string? MeshOf(PrefabComponent c, Dictionary<(uint, string), Override> overrides)
    {
        foreach (var f in new[] { "m_MeshName", "MeshName" })
            if (overrides.TryGetValue((c.Pcid, f), out var o) && o.Text is { } ot && ot.EndsWith(".msh", StringComparison.OrdinalIgnoreCase))
                return ot;
        return (c.Values?.Entries ?? []).FirstOrDefault(v => v.Text is { } t && t.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) &&
                                                           v.Key is { } k && (k.EndsWith("m_MeshName", StringComparison.Ordinal) || k == "MeshName"))?.Text;
    }

    /// <summary>A component field: an override arriving through virtual fields, else the component's own value.</summary>
    private static Override? Get(PrefabComponent c, Dictionary<(uint, string), Override> overrides, string field) =>
        overrides.TryGetValue((c.Pcid, field), out var o) ? o : Own(c, field) is { } v ? new Override(v.Text, v.Payload) : null;

    /// <summary>The component's own value of a field (key <c>field</c> or <c>class::field</c>).</summary>
    private static PrefabValue? Own(PrefabComponent c, string field) =>
        (c.Values?.Entries ?? []).FirstOrDefault(v => v.Key is { } k && (k == field || k.EndsWith("::" + field, StringComparison.Ordinal)));

    /// <summary>
    /// Values for a child prefab's virtual fields, by field name: its presets (lowest), the entity's own values, then
    /// the parent's overrides aimed at the entity (highest).
    /// </summary>
    private static Dictionary<string, Override> Incoming(PrefabCatalog catalog, PrefabEntry child, PrefabComponent? entity, string? presets,
                                                         Dictionary<(uint, string), Override>? parent, uint pcid, List<string>? notes)
    {
        var into = new Dictionary<string, Override>(StringComparer.Ordinal);
        if (presets is { Length: > 0 })
        {
            var parts = presets.Split(';');
            for (int i = 0; i + 1 < parts.Length; i += 2)
                if (Preset(catalog, child.Name, parts[i], parts[i + 1]) is { } p)
                {
                    foreach (var v in p.Values)
                        if (v.Field is { } f) into[Strip(f, child.Name)] = new Override(v.Text, BitConverter.ToUInt64(v.Union, 0));
                }
                else notes?.Add($"{child.Name}: preset {parts[i]};{parts[i + 1]} not found");
        }
        foreach (var v in entity?.Values?.Entries ?? [])
            if (v.Key is { } k) into[Strip(k, child.Name)] = new Override(v.Text, v.Payload);
        if (parent is not null)
            foreach (var ((p, f), o) in parent)
                if (p == pcid) into[Strip(f, child.Name)] = o;
        return into;
    }

    private static string Strip(string key, string prefab) =>
        key.Length > prefab.Length + 2 && key[prefab.Length] == ':' && key[prefab.Length + 1] == ':' &&
        key.AsSpan(0, prefab.Length).Equals(prefab, StringComparison.OrdinalIgnoreCase) ? key[(prefab.Length + 2)..] : key;

    private static readonly ConditionalWeakTable<PrefabRoot, Dictionary<string, List<int>>> VirtualIndex = new();

    /// <summary>
    /// Virtual field values → (destination pcid, destination field), applied in the prefab's virtual-field order (a
    /// later virtual field aimed at the same destination wins).
    /// </summary>
    private static Dictionary<(uint, string), Override> Forward(PrefabEntry child, Dictionary<string, Override> incoming)
    {
        var map = new Dictionary<(uint, string), Override>();
        if (incoming.Count == 0) return map;
        var root = child.Root;
        var index = VirtualIndex.GetValue(root, r =>
        {
            var m = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < r.VirtualFields.Count; i++)
                if (r.VirtualFields[i].Name is { } n)
                {
                    if (!m.TryGetValue(n, out var l)) m[n] = l = [];
                    l.Add(i);
                }
            return m;
        });
        var hits = new List<int>();
        var valueOf = new Dictionary<int, Override>();
        foreach (var (key, o) in incoming)
        {
            // a qualified key (class::field) that names no virtual field falls back to its field name
            int q = key.LastIndexOf("::", StringComparison.Ordinal);
            if (!index.TryGetValue(key, out var l) && !(q >= 0 && index.TryGetValue(key[(q + 2)..], out l))) continue;
            foreach (int i in l)
                if (valueOf.TryAdd(i, o)) hits.Add(i);
        }
        hits.Sort();
        foreach (int i in hits)
        {
            var vf = root.VirtualFields[i];
            var o = valueOf[i];
            foreach (var d in vf.Destinations)
                if (d.Field is { } f) map[((uint)d.Pcid, f)] = o;
        }
        return map;
    }

    private static readonly ConditionalWeakTable<PrefabCatalog, Dictionary<(string, string, string), Preset>> PresetIndex = new();

    /// <summary>
    /// Preset <paramref name="preset"/> of group <paramref name="group"/> of class <paramref name="className"/>: the
    /// sources of <see cref="PrefabCatalog.Preset"/> (every binary resource's class presets in catalog order, group by key
    /// or name, a preset key match before a name match), indexed once per catalog — a linear scan per entity was 30 µs on classes
    /// with large preset groups.
    /// </summary>
    public static Preset? Preset(PrefabCatalog catalog, string className, string group, string preset)
    {
        var index = PresetIndex.GetValue(catalog, cat =>
        {
            var byKey = new Dictionary<(string, string, string), Preset>();
            var byName = new Dictionary<(string, string, string), Preset>();
            foreach (var doc in cat.Prefabs.Take(cat.BinaryCount).Select(p => p.Document).Distinct())
                foreach (var s in doc.PresetSets)
                    if (s.ClassName is { } cls)
                        foreach (var g in s.Groups)
                            foreach (var gn in new[] { g.Key, g.Name }.OfType<string>().Distinct())
                                foreach (var p in g.Presets)
                                {
                                    if (p.Key is { } k) byKey.TryAdd((cls, gn, k), p);
                                    if (p.Name is { } n) byName.TryAdd((cls, gn, n), p);
                                }
            foreach (var (k, p) in byName) byKey.TryAdd(k, p);
            return byKey;
        });
        return index.GetValueOrDefault((className, group, preset));
    }

    /// <summary>
    /// Hierarchies placed by a vehicle rig: pcid → local transform (the element's rest global in the proxy's frame).
    /// Only when the proxy's mesh, script and element all resolve; each miss is a note, never a guess.
    /// </summary>
    private static Dictionary<uint, double[]> Rig(PrefabRoot root, Dictionary<uint, PrefabComponent> byPcid,
                                                  Dictionary<(uint, string), Override> overrides, IPrefabRigSource rig,
                                                  string path, List<string>? notes, out bool vehicle)
    {
        var placed = new Dictionary<uint, double[]>();
        vehicle = false;
        var bound = root.PropertyBindings.Where(b => b.Source == "this" && b.Target is { } t && PrefabVehicleRig.Slots.ContainsKey(t)).ToList();
        foreach (var proxy in bound.GroupBy(b => b.TargetPcid))
        {
            if (!byPcid.TryGetValue(proxy.Key, out var p)) continue;
            var meshName = Get(p, overrides, "MeshName")?.Text;
            var script = Get(p, overrides, "m_ScriptName")?.Text;
            if (meshName is null || !meshName.EndsWith(".msh", StringComparison.OrdinalIgnoreCase))
            {
                notes?.Add($"{path}: rig of pcid {p.Pcid}: no model mesh");
                continue;
            }
            if (script is not { Length: > 0 })
            {
                notes?.Add($"{path}: rig of pcid {p.Pcid}: no vehicle script (m_ScriptName)");
                continue;
            }
            var pars = PrefabVehicleRig.Params(rig, script, notes);
            if (pars is null)
            {
                notes?.Add($"{path}: rig of pcid {p.Pcid}: script {script} not found");
                continue;
            }
            vehicle = true;
            foreach (var b in proxy)
            {
                var (param, fallback) = PrefabVehicleRig.Slots[b.Target!];
                string element = pars.GetValueOrDefault(param, fallback);
                if (rig.Element(meshName[..^4], element, out var why) is { } g) placed[b.SourcePcid] = g;
                else notes?.Add($"{path}: {b.Target} (pcid {b.SourcePcid}): {why}");
            }
        }
        return placed;
    }

    /// <summary><c>T · Rx · Ry · Rz · S</c> from a prefab transform (rotate in degrees), as <c>CreateXform</c>.</summary>
    public static double[] Xform(PrefabXform x)
    {
        double rx = x.Rotate.X * Math.PI / 180, ry = x.Rotate.Y * Math.PI / 180, rz = x.Rotate.Z * Math.PI / 180;
        double[] Rx = [1, 0, 0, 0, 0, Math.Cos(rx), -Math.Sin(rx), 0, 0, Math.Sin(rx), Math.Cos(rx), 0, 0, 0, 0, 1];
        double[] Ry = [Math.Cos(ry), 0, Math.Sin(ry), 0, 0, 1, 0, 0, -Math.Sin(ry), 0, Math.Cos(ry), 0, 0, 0, 0, 1];
        double[] Rz = [Math.Cos(rz), -Math.Sin(rz), 0, 0, Math.Sin(rz), Math.Cos(rz), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        double sx = x.Scale.X == 0 ? 1 : x.Scale.X, sy = x.Scale.Y == 0 ? 1 : x.Scale.Y, sz = x.Scale.Z == 0 ? 1 : x.Scale.Z;
        double[] S = [sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, 0, 0, 0, 1];
        var m = Mul(Mul(Mul(Rx, Ry), Rz), S);
        m[3] = x.Translate.X;
        m[7] = x.Translate.Y;
        m[11] = x.Translate.Z;
        return m;
    }

    private static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private static double[] Mul(double[] a, double[] b) => Cast.ModelCast.Mul(a, b);
}
