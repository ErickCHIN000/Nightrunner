using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Prefab;

/// <summary>What <see cref="PrefabPlacement"/> reads outside the prefab to place rig-driven parts.</summary>
public interface IPrefabRigSource
{
    /// <summary>
    /// Rest global (4×4 row-major, column vectors) of the element named <paramref name="element"/> in mesh
    /// <paramref name="mesh"/> (no <c>.msh</c>), or null with the reason.
    /// </summary>
    double[]? Element(string mesh, string element, out string? why);

    /// <summary>Text of a script by file name (a pak member by basename, the last pak providing it wins), or null.</summary>
    string? Script(string name);
}

/// <summary>
/// The rig source of an install: meshes from the loaded packs (first provider), scripts from the data paks. Read-only;
/// decoded meshes and script texts are cached.
/// </summary>
public sealed class PrefabRigSource : IPrefabRigSource, IDisposable
{
    private readonly RpackCatalog _catalog;
    private readonly IReadOnlyList<string> _paks;
    private readonly ConcurrentDictionary<string, (string[] Names, double[][] Globals)?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private Dictionary<string, (PakIndex Pak, PakMember Member)>? _members;
    private readonly List<PakIndex> _open = [];

    public PrefabRigSource(RpackCatalog catalog, IReadOnlyList<string> paks)
    {
        _catalog = catalog;
        _paks = paks;
    }

    public double[]? Element(string mesh, string element, out string? why)
    {
        var m = _meshes.GetOrAdd(mesh, Load);
        if (m is not { } found)
        {
            why = $"mesh {mesh} not provided by any loaded pack";
            return null;
        }
        int i = Array.FindIndex(found.Names, n => n.Equals(element, StringComparison.OrdinalIgnoreCase));
        why = i < 0 ? $"mesh {mesh} has no element {element}" : null;
        return i < 0 ? null : found.Globals[i];
    }

    private (string[], double[][])? Load(string mesh)
    {
        foreach (int gid in _catalog.Lookup(mesh, 0x10))
        {
            try
            {
                var (e, i) = _catalog.Split(gid);
                var model = MeshDecoder.Decode(e.Pack!, i);
                return (model.Entities.Select(x => x.NameStr).ToArray(), model.EntityGlobals());
            }
            catch (Exception ex) when (ex is MeshFormatException or MeshUnsupportedException or RpackFormatException) { }
        }
        return null;
    }

    public string? Script(string name) => _scripts.GetOrAdd(name, n =>
    {
        lock (_lock)
        {
            _members ??= Index();
            string key = n.Replace('\\', '/');
            key = key[(key.LastIndexOf('/') + 1)..].ToLowerInvariant();
            if (!_members.TryGetValue(key, out var hit)) return null;
            try { return Encoding.UTF8.GetString(hit.Pak.Read(hit.Member)); }
            catch (ModelFormatException) { return null; }
        }
    });

    private Dictionary<string, (PakIndex, PakMember)> Index()
    {
        var map = new Dictionary<string, (PakIndex, PakMember)>(StringComparer.Ordinal);
        foreach (var path in _paks)
        {
            PakIndex ix;
            try { ix = PakIndex.Open(path); }
            catch (Exception e) when (e is ModelFormatException or IOException or UnauthorizedAccessException) { continue; }
            _open.Add(ix);
            foreach (var m in ix.Members)
                if (m.Name.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)) map[m.Basename] = (ix, m);   // later pak wins
        }
        return map;
    }

    public void Dispose()
    {
        lock (_lock) foreach (var p in _open) p.Dispose();
    }
}

/// <summary>
/// The vehicle rig: a vehicle proxy component (<c>CoModelObjectProxy</c> vehicle classes) takes hierarchy components
/// through property bindings (<c>H.this → P.m_WheelFLHierarchy</c> …) and, at runtime, sets each one to an element of
/// its model mesh (<c>CModelObject::MeshName</c>). Which element is a string parameter of the vehicle's script
/// (<c>m_ScriptName</c> → <c>LoadParams(…)</c> → <c>ParamString("mesh_wheel_fl_helper", "Bone_wheel_fl")</c>), with
/// the engine default when the scripts do not set it.
/// </summary>
/// <remarks>
/// gamedll (DLTB build, analysis only): <c>SuspensionParams</c>' constructor (sub_1812e0470) sets the defaults
/// axis_fr/fl/rr/rl, Bone_door_fr/fl/rr/rl, … Bone_root; the params reader (sub_181363b00) maps the names
/// <c>mesh_wheel_*_helper</c>, <c>mesh_door_*_helper</c>, <c>mesh_chassis_helper</c> onto them; sub_181353ce0 resolves
/// each helper to an element index of the model for the hierarchy registered as m_VisHierarchy, m_WheelFL/FR/RL/RR
/// Hierarchy (field order of the truck proxy's RegisterSTTI, sub_1813d2370); sub_181406700 resets the model to its
/// reference frame and hands each hierarchy the element's matrix (the chassis one times the body roll/pitch, identity
/// at rest). At rest the element's reference-frame global is used. Doors hang on their hinge element
/// (<c>Bone_door_*</c>, no rotation); the closed pose is in the door mesh itself — its geometry sits under a child
/// entity that turns the door's +X back along the body (the viewer applies owner-entity transforms), so no door logic
/// is needed at rest. The skin preset's extra prefabs (<see cref="SkinPrefabs"/>) complete the vehicle.
/// </remarks>
public static partial class PrefabVehicleRig
{
    /// <summary>Proxy field → (script parameter, engine default element).</summary>
    public static readonly IReadOnlyDictionary<string, (string Param, string Default)> Slots = new Dictionary<string, (string, string)>
    {
        ["m_VisHierarchy"] = ("mesh_chassis_helper", "Bone_root"),
        ["m_WheelFLHierarchy"] = ("mesh_wheel_fl_helper", "axis_fl"),
        ["m_WheelFRHierarchy"] = ("mesh_wheel_fr_helper", "axis_fr"),
        ["m_WheelRLHierarchy"] = ("mesh_wheel_rl_helper", "axis_rl"),
        ["m_WheelRRHierarchy"] = ("mesh_wheel_rr_helper", "axis_rr"),
        ["m_DoorFLHierarchy"] = ("mesh_door_fl_helper", "Bone_door_fl"),
        ["m_DoorFRHierarchy"] = ("mesh_door_fr_helper", "Bone_door_fr"),
        ["m_DoorRLHierarchy"] = ("mesh_door_rl_helper", "Bone_door_rl"),
        ["m_DoorRRHierarchy"] = ("mesh_door_rr_helper", "Bone_door_rr"),
    };

    /// <summary>The script holding the vehicle skin presets (<c>VehicleSkinPreset("…") { … }</c>).</summary>
    public const string SkinScript = "vehicle_skin_presets.scr";

    /// <summary>The skin every vehicle has; the game picks another from the player's inventory at runtime.</summary>
    public const string DefaultSkin = "Default";

    /// <summary>
    /// A skin preset's <c>PrefabsToLoadStrings</c>: <c>prefab</c>, <c>prefab;group;preset</c> or
    /// <c>prefab;group;preset;hierarchy</c> (the hierarchy is a proxy field without its <c>m_</c>, e.g.
    /// <c>DoorFLHierarchy</c>). Null when the script or the preset is missing.
    /// </summary>
    public static List<(string Prefab, string? Presets, string? Hierarchy)>? SkinPrefabs(IPrefabRigSource source, string skin)
    {
        if (source.Script(SkinScript) is not { } text) return null;
        text = Comments().Replace(text, "");
        foreach (Match m in SkinBlock().Matches(text))
        {
            if (!m.Groups["name"].Value.Equals(skin, StringComparison.OrdinalIgnoreCase)) continue;
            var list = new List<(string, string?, string?)>();
            foreach (Match l in LoadString().Matches(m.Groups["body"].Value))
            {
                var parts = l.Groups["v"].Value.Split(';');
                string prefab = parts[0].Trim();
                if (prefab.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) prefab = prefab[..^7];
                string? presets = parts.Length >= 3 ? $"{parts[1]};{parts[2]}" : null;
                list.Add((prefab, presets, parts.Length >= 4 && parts[3].Length > 0 ? parts[3] : null));
            }
            return list;
        }
        return null;
    }

    [GeneratedRegex(@"VehicleSkinPreset\s*\(\s*""(?<name>[^""]*)""\s*\)\s*\{(?<body>[^{}]*)\}")]
    private static partial Regex SkinBlock();

    [GeneratedRegex(@"PrefabsToLoadStrings\s*\(\s*""(?<v>[^""]*)""\s*\)")]
    private static partial Regex LoadString();

    public const int MaxIncludeDepth = 8;

    /// <summary>
    /// The <c>ParamString</c> values a vehicle script sets, following <c>LoadParams("…")</c> in order (a later
    /// assignment wins); null when the script is not found. Comments are skipped.
    /// </summary>
    public static Dictionary<string, string>? Params(IPrefabRigSource source, string script, List<string>? notes = null)
    {
        if (source.Script(script) is null) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Read(source, script, result, notes, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private static void Read(IPrefabRigSource source, string script, Dictionary<string, string> into, List<string>? notes, int depth,
                             HashSet<string> seen)
    {
        if (depth > MaxIncludeDepth || !seen.Add(script)) return;
        if (source.Script(script) is not { } text)
        {
            notes?.Add($"script {script} not found");
            return;
        }
        text = Comments().Replace(text, "");
        foreach (Match m in Statement().Matches(text))
        {
            if (m.Groups["load"].Success) Read(source, m.Groups["load"].Value, into, notes, depth + 1, seen);
            else into[m.Groups["key"].Value] = m.Groups["value"].Value;
        }
    }

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"LoadParams\s*\(\s*""(?<load>[^""]*)""\s*\)|ParamString\s*\(\s*""(?<key>[^""]*)""\s*,\s*""(?<value>[^""]*)""\s*\)")]
    private static partial Regex Statement();
}
