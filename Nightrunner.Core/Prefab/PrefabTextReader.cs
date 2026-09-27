using System.Globalization;

namespace Nightrunner.Core.Prefab;

public enum PrefabTextFormat { Json, MessagePack, Yaml }

/// <summary>Where a text prefab came from: pak file name, member path, DOM format, and the document's <c>Version</c>.</summary>
public sealed record PrefabTextSource(string Pak, string Member, PrefabTextFormat Format, int? Version);

/// <summary>
/// Reads a text <c>.prefab</c> (a pak member) into the same <see cref="PrefabDocument"/> model as the binary decoder.
/// The format is picked as <c>dom::CreateReaderInSitu</c> does (prefab notes §10): first byte <c>{</c> → JSON, a
/// <c>MsgP</c> magic → MessagePack, anything else → YAML. Grammar (measured over every shipped file):
/// <code>
/// { "__filename__": { "Version": 1..4 (absent on the oldest files),
///                     "RuntimeData": { "Components": [ { "Class": "CEntity" | "CoMeshRender" | …,
///                                                       "PrefabFieldsNative": { name: [ERTTIType, value] },  (v3+)
///                                                       "PrefabFields": { name: "text" },                  (older)
///                                                       "Fields": { name: "text" },
///                                                       "Presets" | "EmbeddedObject" | … (kept raw) } ],
///                                      "Interface": { "VirtualFields": [ {Name, Init, DestinationFields: [{Uuid, Name}]} ], … },
///                                      "Bindings": { "Properties": [ {src_uuid, src, dest_uuid, dest} ], "Pipes": [ … ] },
///                                      "PrefabInterfaces": [ "IName" ], "Extents", "SpawnType", … (kept raw) },
///                     "PrefabEditorData": { … } (kept raw) },
///   "&lt;sub prefab name&gt;": { same shape } }
/// </code>
/// The oldest YAML files hold the <c>RuntimeData</c> members directly in their first document (then the editor data,
/// then editor nodes); they are read as if wrapped. Component member names that repeat (<c>EmbeddedObject</c>,
/// <c>Bundle</c>, <c>Mesh</c>) are kept in order. What the model has no member for is kept as its DOM text in
/// <see cref="PrefabRoot.TextOther"/> / <see cref="PrefabComponent.TextOther"/>, never dropped.
/// </summary>
public static class PrefabTextReader
{
    public const string FileKey = "__filename__";

    public static PrefabTextFormat Detect(ReadOnlySpan<byte> bytes)
    {
        var b = bytes.StartsWith("﻿"u8) ? bytes[3..] : bytes;
        if (b.Length > 0 && b[0] == (byte)'{') return PrefabTextFormat.Json;
        if (bytes.Length > 4 && bytes.StartsWith("MsgP"u8)) return PrefabTextFormat.MessagePack;
        return PrefabTextFormat.Yaml;
    }

    /// <summary>The file as one JSON-shaped object (<c>__filename__</c> plus sub-prefab keys), whatever its format.</summary>
    public static PrefabTextNode Parse(ReadOnlySpan<byte> bytes, out PrefabTextFormat format)
    {
        format = Detect(bytes);
        PrefabTextNode root;
        switch (format)
        {
            case PrefabTextFormat.Json: root = PrefabTextNode.ParseJson(bytes); break;
            case PrefabTextFormat.MessagePack: root = PrefabTextNode.ParseMessagePack(bytes[4..]); break;
            default:
            {
                var docs = PrefabYaml.ParseDocuments(bytes);
                if (docs.Count == 0) throw new PrefabFormatException("YAML: no document");
                if (docs[0][FileKey] is not null) { root = docs[0]; break; }
                // the oldest form: document 1 = RuntimeData members, 2 = editor data, 3 = editor nodes
                var wrapped = new List<KeyValuePair<string, PrefabTextNode>> { new("RuntimeData", docs[0]) };
                if (docs.Count > 1) wrapped.Add(new("PrefabEditorData", docs[1]));
                for (int k = 2; k < docs.Count; k++) wrapped.Add(new($"YamlDocument{k + 1}", docs[k]));
                root = PrefabTextNode.Object([new(FileKey, PrefabTextNode.Object(wrapped))]);
                break;
            }
        }
        if (root.Kind != PrefabTextKind.Object) throw new PrefabFormatException($"{format}: the root is a {root.Kind}, not an object");
        return root;
    }

    /// <summary>
    /// Reads a text prefab: prefab 0 is the file's own (<c>__filename__</c>, named <paramref name="name"/>), the
    /// others its sub-prefabs by key. Malformed or unsupported input throws <see cref="PrefabFormatException"/>.
    /// </summary>
    public static PrefabDocument Read(ReadOnlySpan<byte> bytes, string name, string pak = "", string member = "")
    {
        var root = Parse(bytes, out var format);
        var ctx = new Context();
        var prefabs = new List<PrefabRoot>();
        int? version = null;
        foreach (var (key, value) in root.Members!)
        {
            if (value.Kind != PrefabTextKind.Object)
            {
                ctx.Warnings.Add($"top-level '{key}' is a {value.Kind}, not a prefab object");
                continue;
            }
            var p = Prefab(prefabs.Count, key == FileKey ? name : key, value, ctx);
            if (key == FileKey) version = p.TextVersion;
            prefabs.Add(p);
        }
        if (prefabs.Count == 0) throw new PrefabFormatException($"{format}: no prefab object in the file");
        var cov = new PrefabCoverage(ctx.Total, ctx.Typed, ctx.Total - ctx.Typed, new SortedDictionary<uint, int>(), []);
        return new PrefabDocument(prefabs, [], [], cov, ctx.Warnings) { TextSource = new PrefabTextSource(pak, member, format, version) };
    }

    private sealed class Context
    {
        public int Total, Typed;
        public readonly List<string> Warnings = [];
        public void Count(bool typed) { Total++; if (typed) Typed++; }
    }

    private static PrefabRoot Prefab(int index, string name, PrefabTextNode p, Context ctx)
    {
        int? version = p["Version"] is { } v && int.TryParse(v.Scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vi) ? vi : null;
        if (version > 4) ctx.Warnings.Add($"{name}: Version {version} (the engine reads up to 4)");
        var other = new List<KeyValuePair<string, string>>();
        var comps = new List<PrefabComponent>();
        var interfaces = new List<PrefabInterface>();
        var virtuals = new List<PrefabVirtualField>();
        var propBindings = new List<PrefabBinding>();
        var pipeBindings = new List<PrefabBinding>();
        int propCount = 0;
        foreach (var (key, value) in p.Members!)
        {
            if (key == "Version") continue;
            if (key != "RuntimeData")
            {
                other.Add(new(key, value.ToString()));
                ctx.Count(false);
                continue;
            }
            foreach (var (rk, rv) in value.Members ?? [])
            {
                switch (rk)
                {
                    case "Components":
                        foreach (var c in rv.Items ?? [])
                            if (c.Kind == PrefabTextKind.Object) comps.Add(Component(c, index, comps.Count, ctx));
                            else ctx.Warnings.Add($"{name}: component of kind {c.Kind}");
                        ctx.Count(true);
                        break;
                    case "PrefabInterfaces":
                        foreach (var it in rv.Items ?? []) interfaces.Add(new PrefabInterface(null, it.Scalar ?? it.ToString(), 0));
                        ctx.Count(true);
                        break;
                    case "Interface":
                        foreach (var (ik, iv) in iv_Members(rv))
                        {
                            if (ik == "VirtualFields")
                            {
                                foreach (var f in iv.Items ?? [])
                                    virtuals.Add(new PrefabVirtualField(f["Name"]?.Scalar, f["Init"]?.Scalar,
                                        (f["DestinationFields"]?.Items ?? []).Select(d => new PrefabVirtualDestination(d["Name"]?.Scalar, ULong(d["Uuid"]))).ToList()));
                                ctx.Count(true);
                            }
                            else
                            {
                                if (ik == "Properties") propCount = iv.Items?.Count ?? 0;
                                other.Add(new("Interface." + ik, iv.ToString()));
                                ctx.Count(false);
                            }
                        }
                        break;
                    case "Bindings":
                        foreach (var (bk, bv) in iv_Members(rv))
                        {
                            var list = bk == "Properties" ? propBindings : bk == "Pipes" ? pipeBindings : null;
                            if (list is null) { other.Add(new("Bindings." + bk, bv.ToString())); ctx.Count(false); continue; }
                            foreach (var b in bv.Items ?? [])
                                list.Add(new PrefabBinding((uint)ULong(b["src_uuid"]), (uint)ULong(b["dest_uuid"]), b["src"]?.Scalar, b["dest"]?.Scalar));
                            ctx.Count(true);
                        }
                        break;
                    default:
                        other.Add(new("RuntimeData." + rk, rv.ToString()));
                        ctx.Count(false);
                        break;
                }
            }
        }
        return new PrefabRoot(index, -1, name, null, 0, 0, [], comps, interfaces, propCount, 0, [], [], propBindings, pipeBindings, virtuals)
        {
            TextVersion = version,
            TextOther = other,
        };
    }

    private static IEnumerable<KeyValuePair<string, PrefabTextNode>> iv_Members(PrefabTextNode n) => n.Members ?? [];

    // ---- components -----------------------------------------------------------------------------------------

    private static PrefabComponent Component(PrefabTextNode c, int owner, int ordinal, Context ctx)
    {
        string? cls = null;
        var native = new List<PrefabValue>();
        var values = new List<PrefabValue>();
        var other = new List<KeyValuePair<string, string>>();
        var byName = new Dictionary<string, PrefabTextNode>(StringComparer.Ordinal);
        foreach (var (key, value) in c.Members!)
        {
            switch (key)
            {
                case "Class":
                    cls = value.Scalar;
                    break;
                case "PrefabFieldsNative":
                case "PrefabFields":
                    foreach (var (fk, fv) in value.Members ?? [])
                    {
                        // native: [ERTTIType, value…]; the older PrefabFields: "text"
                        ushort? type = null;
                        PrefabTextNode val = fv;
                        if (key == "PrefabFieldsNative" && fv.Items is { Count: >= 2 } items && ushort.TryParse(items[0].Scalar, out ushort t))
                        {
                            type = t;
                            val = items.Count == 2 ? items[1] : PrefabTextNode.Array(items.Skip(1).ToList());
                        }
                        byName.TryAdd(fk, val);
                        native.Add(TextValue(fk, type, val));
                        ctx.Count(true);
                    }
                    break;
                case "Fields":
                    foreach (var (fk, fv) in value.Members ?? [])
                    {
                        values.Add(TextValue(fk, null, fv));
                        ctx.Count(true);
                    }
                    break;
                default:
                    other.Add(new(key, value.ToString()));
                    ctx.Count(false);
                    break;
            }
        }
        uint pcid = (uint)(Num(byName, "m_PcId") ?? Num(byName, "m_Uuid") ?? 0);
        bool entity = cls == "CEntity";
        var t3 = Floats(byName, "m_Translate", 3);
        var r3 = Floats(byName, "m_Rotate", 3);
        var s3 = Floats(byName, "m_Scale", 3);
        PrefabXform? xf = entity || t3 is not null || r3 is not null || s3 is not null
            ? new PrefabXform(V(t3, 0), V(r3, 0), V(s3, 1))
            : null;
        uint? parent = Num(byName, "m_ParentComponent") is { } pp ? (uint)pp : null;
        PrefabEntity? ent = null;
        if (entity)
            ent = new PrefabEntity(Str(byName, "m_Name"), Str(byName, "m_PrefabName"), Str(byName, "m_PresetNames"),
                                   Str(byName, "m_Configurations"), -1, null, parent ?? 0,
                                   Floats(byName, "m_EntityComponentsExtents", 6) ?? [], [], 0);
        return new PrefabComponent(-1 - ordinal, entity ? PrefabClasses.EntityComponent : 0, pcid, 0, owner, cls, false,
                                   new PrefabValues(-1, 0, 0, values), null, xf, ent)
        {
            HierarchyParent = entity ? null : parent,
            IsText = true,
            XformComponent = Num(byName, "m_XformComponent") is { } x ? (uint)x : null,
            NativeFields = native,
            TextOther = other,
        };
    }

    private static PrefabValue TextValue(string key, ushort? type, PrefabTextNode v)
    {
        if (v.Kind == PrefabTextKind.Array && v.Items!.All(i => i.Kind == PrefabTextKind.Number))
            return new PrefabValue(key, false, type, 0, PrefabValueForm.Text, string.Join(" ", v.Items!.Select(i => i.Text)), null,
                                   v.Items!.Select(i => (float)double.Parse(i.Text!, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray());
        if (v.Kind is PrefabTextKind.Number or PrefabTextKind.Bool)
            return new PrefabValue(key, false, type, 0, PrefabValueForm.Scalar, v.Text,
                                   v.Kind == PrefabTextKind.Bool ? (v.Text == "true" ? 1 : 0) : double.Parse(v.Text!, NumberStyles.Float, CultureInfo.InvariantCulture), null);
        return new PrefabValue(key, false, type, 0, PrefabValueForm.Text, v.Kind == PrefabTextKind.String ? v.Text : v.ToString(), null, null);
    }

    private static Vec3 V(float[]? f, float fallback) => f is null ? new Vec3(fallback, fallback, fallback) : new Vec3(f[0], f[1], f[2]);

    private static string? Str(Dictionary<string, PrefabTextNode> d, string key) => d.GetValueOrDefault(key)?.Scalar;

    private static double? Num(Dictionary<string, PrefabTextNode> d, string key) =>
        d.GetValueOrDefault(key) is { } n && n.TryDouble(out double v) ? v : null;

    /// <summary>A vector as a number array (native) or space-separated text (older files); null when absent or not n numbers.</summary>
    private static float[]? Floats(Dictionary<string, PrefabTextNode> d, string key, int n)
    {
        if (d.GetValueOrDefault(key) is not { } node) return null;
        IEnumerable<string?> parts = node.Kind == PrefabTextKind.Array
            ? node.Items!.Select(i => i.Scalar)
            : (node.Scalar ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var f = new List<float>();
        foreach (var s in parts)
        {
            if (s is null || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return null;
            f.Add((float)x);
        }
        return f.Count == n ? f.ToArray() : null;
    }

    private static ulong ULong(PrefabTextNode? n) =>
        n?.Scalar is { } s && ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v) ? v : 0;
}
