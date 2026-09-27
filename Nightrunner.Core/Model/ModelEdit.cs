using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nightrunner.Core.Model;

/// <summary>
/// Edits on a <c>.model</c> document (port of the slot/material helpers of <c>project.py</c>). They change the JSON
/// in place; unknown fields are never touched. <paramref name="stash"/> arguments hold the entries of a slot that was
/// turned off (<c>{slot name: [entries]}</c>, kept in the project, never written to the PAK).
/// </summary>
/// <remarks>
/// The game draws a slot's first entry and ignores <c>selected</c> (in-game test 2026-09-16). The prototype edits by
/// flipping <c>selected</c> and relies on <see cref="GameDoc"/> at build time; C# additionally moves the chosen entry
/// to the front, so the document reads the same in every viewer (the Viewport draws the first) and after
/// <see cref="GameDoc"/>.
/// </remarks>
public static partial class ModelEdit
{
    public const string OutfitScript = "scripts/player/player_outfit_slots.scr";
    public const string OutfitScriptLegacy = "scripts/player_outfit_slots.scr";
    public static readonly string[] PlayerModels = ["player_tpp_skeleton.model", "player_fpp_skeleton.model"];

    public const string OutfitTemplate = """

        // mapping of outfit part slots to model slots. Done per game.
        // Emptied by nightrunner: equipped gear no longer replaces model slots (the look comes from the .model only).


        sub main()
        {

        }

        """;

    private static string Msh(string name) => name.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) ? name : name + ".msh";

    public static JsonArray Slots(JsonObject doc) => doc["slots"] as JsonArray ?? (JsonArray)(doc["slots"] = new JsonArray());

    public static JsonObject Slot(JsonObject doc, string name) =>
        Slots(doc).OfType<JsonObject>().FirstOrDefault(s => ModelDocument.Str(s["name"]) == name)
        ?? throw new ModelFormatException($"no slot '{name}'");

    public static JsonArray Resources(JsonObject slot)
    {
        if (slot["meshResources"] is not JsonObject mr) slot["meshResources"] = mr = new JsonObject();
        if (mr["resources"] is not JsonArray res) mr["resources"] = res = new JsonArray();
        return res;
    }

    private static void Off(JsonObject s, JsonObject? stash)
    {
        var res = Resources(s);
        if (res.Count > 0 && stash is not null) stash[ModelDocument.Str(s["name"]) ?? ""] = res.DeepClone();
        res.Clear();
    }

    private static JsonArray Restore(JsonObject s, JsonObject? stash)
    {
        var res = Resources(s);
        string name = ModelDocument.Str(s["name"]) ?? "";
        if (res.Count == 0 && stash?[name] is JsonArray saved)
        {
            foreach (var e in saved) res.Add(e?.DeepClone());
            stash.Remove(name);
        }
        return res;
    }

    /// <summary>Turn a slot off (empty list — the only "off" stock data proves) or back on.</summary>
    public static void SetSlotEnabled(JsonObject doc, string slotName, bool on, JsonObject? stash = null)
    {
        var s = Slot(doc, slotName);
        if (!on) { Off(s, stash); return; }
        var res = Restore(s, stash);
        if (res.Count > 0 && !res.OfType<JsonObject>().Any(r => ModelDocument.Flag(r["selected"]))) res[0]!["selected"] = true;
    }

    /// <summary>
    /// Make <paramref name="mesh"/> the slot's drawn entry (null = off). A mesh not listed yet is added as a copy of the
    /// current chosen entry — materials included — with only the name changed. The entry is selected and moved first.
    /// </summary>
    public static JsonObject? SetSlotMesh(JsonObject doc, string slotName, string? mesh, JsonObject? stash = null)
    {
        var s = Slot(doc, slotName);
        if (mesh is null) { Off(s, stash); return null; }
        var res = Restore(s, stash);
        string want = Msh(mesh);
        var hit = res.OfType<JsonObject>().FirstOrDefault(r => Msh(ModelDocument.Str(r["name"]) ?? "").Equals(want, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            var template = res.OfType<JsonObject>().FirstOrDefault(r => ModelDocument.Flag(r["selected"])) ?? res.OfType<JsonObject>().FirstOrDefault();
            hit = template?.DeepClone().AsObject() ?? new JsonObject
            {
                ["layoutId"] = 4, ["userData"] = new JsonArray(0, 0, 0, 0),
                ["materialsData"] = new JsonArray(), ["materialsResources"] = new JsonArray(),
            };
            hit["name"] = want;
            res.Add(hit);
        }
        foreach (var r in res.OfType<JsonObject>()) r["selected"] = ReferenceEquals(r, hit);
        res.Remove(hit);
        res.Insert(0, hit);
        return hit;
    }

    /// <summary>A new empty slot with the stock fields (lowest free uid from 100). An existing name returns that slot.</summary>
    public static JsonObject AddSlot(JsonObject doc, string name, string filterText = "torso")
    {
        var existing = Slots(doc).OfType<JsonObject>().FirstOrDefault(s => ModelDocument.Str(s["name"]) == name);
        if (existing is not null) return existing;
        var used = Slots(doc).OfType<JsonObject>().Select(s => ModelDocument.Int(s["slotUid"])).ToHashSet();
        int uid = 100;
        while (used.Contains(uid)) uid++;
        var slot = new JsonObject
        {
            ["slotUid"] = uid, ["name"] = name, ["filterText"] = filterText, ["tagsBits"] = 0, ["shadowMaps"] = 15,
            ["meshResources"] = new JsonObject { ["resources"] = new JsonArray() },
        };
        Slots(doc).Add(slot);
        return slot;
    }

    public static JsonObject MeshEntry(JsonObject doc, string slotName, string mesh) =>
        Resources(Slot(doc, slotName)).OfType<JsonObject>()
            .FirstOrDefault(r => Msh(ModelDocument.Str(r["name"]) ?? "").Equals(Msh(mesh), StringComparison.OrdinalIgnoreCase))
        ?? throw new ModelFormatException($"no mesh '{mesh}' in slot '{slotName}'");

    /// <summary>
    /// The chosen materialsResources entry for a submesh material of a mesh entry, created on demand with base
    /// <paramref name="baseMaterial"/> (else the material itself); an existing entry is re-based when one is given.
    /// </summary>
    public static JsonObject? MaterialEntry(JsonObject meshEntry, string material, string? baseMaterial = null, bool create = true)
    {
        if (meshEntry["materialsData"] is not JsonArray data) { if (!create) return null; meshEntry["materialsData"] = data = new JsonArray(); }
        var row = data.OfType<JsonObject>().FirstOrDefault(m => string.Equals(ModelDocument.Str(m["name"]), material, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            if (!create) return null;
            int num = data.OfType<JsonObject>().Select(m => ModelDocument.Int(m["number"]) ?? 0).DefaultIfEmpty(-1).Max() + 1;
            row = new JsonObject { ["number"] = num, ["name"] = material, ["layoutId"] = 4, ["loadFlags"] = "S" };
            data.Add(row);
        }
        int? number = ModelDocument.Int(row["number"]);
        if (meshEntry["materialsResources"] is not JsonArray groups) { if (!create) return null; meshEntry["materialsResources"] = groups = new JsonArray(); }
        var group = groups.OfType<JsonObject>().FirstOrDefault(g => ModelDocument.Int(g["number"]) == number);
        if (group is null)
        {
            if (!create) return null;
            group = new JsonObject { ["number"] = number, ["resources"] = new JsonArray() };
            groups.Add(group);
        }
        if (group["resources"] is not JsonArray entries) group["resources"] = entries = new JsonArray();
        var ent = entries.OfType<JsonObject>().FirstOrDefault(e => ModelDocument.Flag(e["selected"])) ?? entries.OfType<JsonObject>().FirstOrDefault();
        if (ent is null)
        {
            if (!create) return null;
            ent = new JsonObject { ["name"] = baseMaterial ?? material, ["selected"] = true, ["layoutId"] = 4, ["loadFlags"] = "S", ["rttiValues"] = new JsonArray() };
            entries.Add(ent);
        }
        else if (baseMaterial is not null) ent["name"] = baseMaterial;
        return ent;
    }

    /// <summary>Set or remove (null) one rttiValues override: string → type 7, number → 2, three numbers → 4.</summary>
    public static void SetRtti(JsonObject materialEntry, string param, object? value)
    {
        if (materialEntry["rttiValues"] is not JsonArray vals) materialEntry["rttiValues"] = vals = new JsonArray();
        foreach (var v in vals.OfType<JsonObject>().Where(v => ModelDocument.Str(v["name"]) == param).ToList()) vals.Remove(v);
        switch (value)
        {
            case null: return;
            case string s: vals.Add(new JsonObject { ["name"] = param, ["type"] = 7, ["val_str"] = s }); break;
            case double or float or int or long:
                vals.Add(new JsonObject { ["name"] = param, ["type"] = 2, ["val_float"] = Convert.ToDouble(value, CultureInfo.InvariantCulture) });
                break;
            case IEnumerable<double> seq:
                var v3 = seq.ToArray();
                if (v3.Length != 3) throw new ModelFormatException($"{param}: vec3 expected");
                vals.Add(new JsonObject { ["name"] = param, ["type"] = 4, ["val_vec3"] = new JsonArray(v3[0], v3[1], v3[2]) });
                break;
            default: throw new ModelFormatException($"{param}: unsupported value {value.GetType().Name}");
        }
    }

    /// <summary>
    /// The document as the game must see it: every slot holds at most one entry — the selected one, else the first —
    /// marked selected. (With several, the game draws the stock first entry and ignores ours.)
    /// </summary>
    public static JsonObject GameDoc(JsonObject doc)
    {
        var copy = doc.DeepClone().AsObject();
        foreach (var s in Slots(copy).OfType<JsonObject>())
        {
            if (s["meshResources"] is not JsonObject mr || mr["resources"] is not JsonArray res || res.Count <= 1) continue;
            var chosen = res.OfType<JsonObject>().FirstOrDefault(r => ModelDocument.Flag(r["selected"])) ?? (JsonObject)res[0]!;
            res.Remove(chosen);
            chosen["selected"] = true;
            mr["resources"] = new JsonArray(chosen);
        }
        return copy;
    }

    /// <summary>Member paths a document is written to: "root" (bare name), "original", or "both".</summary>
    public static IReadOnlyList<string> MemberPaths(string member, string mode)
    {
        string m = member.Replace('\\', '/');
        string bare = m[(m.LastIndexOf('/') + 1)..];
        return mode switch
        {
            "root" => [bare],
            "original" => [m],
            "both" => bare == m ? [bare] : [bare, m],
            _ => throw new ModelFormatException($"path mode '{mode}' (root, original or both)"),
        };
    }

    public static bool IsPlayer(string member) =>
        PlayerModels.Contains(member.Replace('\\', '/')[(member.Replace('\\', '/').LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"sub\s+main\s*\(\s*\)\s*\{")]
    private static partial Regex MainSub();

    /// <summary><c>player_outfit_slots.scr</c> with an empty <c>main()</c>, comments kept — what gear-proof player mods ship.</summary>
    public static string EmptyOutfitScript(string? text)
    {
        if (string.IsNullOrEmpty(text)) return OutfitTemplate;
        var m = MainSub().Match(text);
        if (!m.Success) return OutfitTemplate;
        int depth = 1, i = m.Index + m.Length;
        while (i < text.Length && depth > 0)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            i++;
        }
        return text[..(m.Index + m.Length)] + "\n\n" + text[(i - 1)..];
    }
}
