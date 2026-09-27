using System.Text.Json.Nodes;

namespace Nightrunner.Core.Model;

/// <summary>
/// A <c>.model</c> document (JSON, version 6): typed views over the parsed JSON, which is kept (<see cref="Json"/>) so
/// unknown fields survive and the document can be edited and written back. Schema as observed on every stock member
/// (<c>pak/model_json.py</c>, pak-and-model.md):
/// <code>
/// version 6 | preset {skeletonName} | data {meshAttribute, properties[{name,value}]} | slots[] | poseItems
/// slot: slotUid, name, filterText, tagsBits, shadowMaps, meshResources{resources[]}, clothResources
/// mesh: name (.msh), selected, layoutId, userData, materialsData[{number,name,layoutId,loadFlags}],
///       materialsResources[{number, resources[{name (.mat), selected, layoutId, loadFlags, rttiValues[]}]}]
/// rttiValues: {name, type, val_str (7) | val_float (2) | val_vec3 (4)}
/// </code>
/// </summary>
public sealed class ModelDocument
{
    public JsonObject Json { get; }
    public string Name { get; }
    public int Version { get; }
    public string? Skeleton { get; }
    public IReadOnlyList<ModelSlot> Slots { get; }
    public IReadOnlyList<(string Name, string Value)> Properties { get; }
    public bool HasPoseItems { get; }

    private ModelDocument(JsonObject json, string name)
    {
        Json = json;
        Name = name;
        Version = json["version"] is JsonValue v && v.TryGetValue(out int ver) ? ver : -1;
        Skeleton = Str(json["preset"]?["skeletonName"]);
        Properties = (json["data"]?["properties"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(p => (Str(p["name"]) ?? "", p["value"]?.ToJsonString() ?? "")).ToList();
        if (json["slots"] is JsonNode sn && sn is not JsonArray) throw new ModelFormatException($"{name}: 'slots' is not a list");
        Slots = (json["slots"] as JsonArray ?? []).Select((s, i) => s is JsonObject o
            ? new ModelSlot(o, i)
            : throw new ModelFormatException($"{name}: slot {i} is not an object")).ToList();
        HasPoseItems = json["poseItems"] is JsonArray { Count: > 0 };
    }

    public static ModelDocument From(JsonObject json, string name)
    {
        var doc = new ModelDocument(json, name);
        if (doc.Version != 6) throw new ModelFormatException($"{name}: version {json["version"]?.ToJsonString() ?? "none"} (only 6 is known)");
        return doc;
    }

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    public static bool Flag(JsonNode? n) => n is JsonValue v && (v.TryGetValue(out bool b) ? b : v.TryGetValue(out int i) && i != 0);
    public static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : null;
}

public sealed class ModelSlot
{
    public JsonObject Json { get; }
    public int Index { get; }
    public string Name { get; }
    public string? SlotUid { get; }
    public string? FilterText { get; }
    public IReadOnlyList<ModelMesh> Meshes { get; }
    public bool HasCloth { get; }

    internal ModelSlot(JsonObject o, int index)
    {
        Json = o;
        Index = index;
        Name = ModelDocument.Str(o["name"]) ?? $"slot {index}";
        SlotUid = o["slotUid"]?.ToJsonString();
        FilterText = ModelDocument.Str(o["filterText"]);
        Meshes = (o["meshResources"]?["resources"] as JsonArray ?? [])
            .OfType<JsonObject>().Select((m, i) => new ModelMesh(m, i)).ToList();
        HasCloth = o["clothResources"] is JsonNode c && !(c is JsonArray { Count: 0 }) && !(c is JsonObject { Count: 0 });
    }

    /// <summary>
    /// The entry the game draws: the FIRST one. In-game testing (2026-09-16) showed the engine ignores
    /// <c>selected</c>; no stock model has more than one entry per slot.
    /// </summary>
    public ModelMesh? Drawn => Meshes.Count > 0 ? Meshes[0] : null;

    /// <summary>The prototype's pick (selected, else first) — what its viewer showed.</summary>
    public ModelMesh? Chosen => Meshes.FirstOrDefault(m => m.Selected) ?? Drawn;
}

public sealed class ModelMesh
{
    public JsonObject Json { get; }
    public int Index { get; }
    public string Name { get; }
    public bool Selected { get; }
    public string? LayoutId { get; }
    public IReadOnlyList<ModelMaterialData> MaterialsData { get; }
    public IReadOnlyList<ModelMaterialGroup> MaterialsResources { get; }

    public ModelMesh(JsonObject o, int index)
    {
        Json = o;
        Index = index;
        Name = ModelDocument.Str(o["name"]) ?? "";
        Selected = ModelDocument.Flag(o["selected"]);
        LayoutId = o["layoutId"]?.ToJsonString();
        MaterialsData = (o["materialsData"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(m => new ModelMaterialData(ModelDocument.Int(m["number"]), ModelDocument.Str(m["name"]) ?? "",
                                               ModelDocument.Str(m["loadFlags"]))).ToList();
        MaterialsResources = (o["materialsResources"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(g => new ModelMaterialGroup(ModelDocument.Int(g["number"]),
                (g["resources"] as JsonArray ?? []).OfType<JsonObject>().Select(r => new ModelMaterialResource(
                    ModelDocument.Str(r["name"]) ?? "", ModelDocument.Flag(r["selected"]), ModelDocument.Str(r["loadFlags"]),
                    (r["rttiValues"] as JsonArray ?? []).OfType<JsonObject>().Select(RttiValue.From).ToList())).ToList()))
            .ToList();
    }

    /// <summary>The <c>.msh</c> key with the suffix the catalog lookup wants.</summary>
    public string MeshName => Name.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) ? Name[..^4] : Name;
}

public sealed record ModelMaterialData(int? Number, string Name, string? LoadFlags);

public sealed record ModelMaterialGroup(int? Number, IReadOnlyList<ModelMaterialResource> Resources)
{
    /// <summary>Selected, else the first (the prototype's join rule, B).</summary>
    public ModelMaterialResource? Chosen => Resources.FirstOrDefault(r => r.Selected) ?? Resources.FirstOrDefault();
}

public sealed record ModelMaterialResource(string Name, bool Selected, string? LoadFlags, IReadOnlyList<RttiValue> RttiValues);

/// <summary>A material override: type 7 texture (val_str), 2 float (val_float), 4 vec3 (val_vec3).</summary>
public sealed record RttiValue(string Name, int? Type, string Value)
{
    public string Kind => Type switch { 7 => "texture", 2 => "float", 4 => "vec3", _ => $"type {Type}" };

    public static RttiValue From(JsonObject v)
    {
        int? t = ModelDocument.Int(v["type"]);
        var val = t switch
        {
            7 => v["val_str"],
            2 => v["val_float"],
            4 => v["val_vec3"],
            _ => v.FirstOrDefault(kv => kv.Key.StartsWith("val_")).Value,
        };
        string text = val is JsonValue jv && jv.TryGetValue(out string? s) ? s : val?.ToJsonString() ?? "";
        return new RttiValue(ModelDocument.Str(v["name"]) ?? "", t, text);
    }
}
