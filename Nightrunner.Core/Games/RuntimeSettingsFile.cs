using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightrunner.Core.Games;

/// <summary>One <c>mods</c> entry of <c>nightrunner.json</c> as NightrunnerProxy keeps it (id trimmed, first entry per id).</summary>
public sealed record RuntimeSettingsEntry(string Id, bool Enabled, int Order);

/// <summary>
/// NightrunnerProxy's <c>nightrunner.json</c>, read by the runtime's own rules (<c>Core/Content/RuntimeSettings.cs</c> and
/// <c>ModJson.cs</c>, NightrunnerProxy rc1). Reads only.
/// </summary>
/// <remarks>
/// <para>Missing: the defaults — every mod on disk enabled, console off, logLevel info.</para>
/// <para>There but untrusted: the runtime fails closed and loads NO mods; <see cref="Problem"/> says why. Untrusted is:
/// unreadable, empty (whitespace only), not JSON, JSON <c>null</c> or not an object, a value of the wrong type, a
/// <c>schema</c> other than <see cref="SchemaId"/> (absent is fine; compared trimmed, ignoring case), a <c>logLevel</c> not
/// debug/info/warn/error/fatal, a null <c>mods</c> entry, or an entry with no id. Unknown keys are ignored; comments and
/// trailing commas are accepted; keys match ignoring case.</para>
/// <para>A mod listed twice keeps its first entry (ids trimmed, compared ignoring case), with a warning.</para>
/// </remarks>
public sealed class RuntimeSettingsFile
{
    public const string FileName = "nightrunner.json";

    /// <summary><c>RuntimeSettings.SchemaId</c>.</summary>
    public const string SchemaId = "nightrunner/settings@1";

    public static RuntimeSettingsFile Defaults { get; } = new();

    /// <summary>The file was there (broken or not).</summary>
    public bool Exists { get; private init; }

    /// <summary>The debug console: off unless the file says true.</summary>
    public bool Console { get; private init; }

    /// <summary>debug, info, warn, error or fatal (lower case).</summary>
    public string LogLevel { get; private init; } = "info";

    public IReadOnlyList<RuntimeSettingsEntry> Mods { get; private init; } = [];

    /// <summary>Why the runtime cannot use the file (<c>nightrunner.json is empty</c>, ...), or null. Non-null: no mods load.</summary>
    public string? Problem { get; private init; }

    /// <summary>What the runtime warns about but reads anyway: an id listed again.</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    public bool IsBroken => Problem is not null;

    /// <summary>The first entry for <paramref name="modId"/> (ignoring case), as <c>RuntimeSettings.TryGet</c>.</summary>
    public RuntimeSettingsEntry? Find(string modId) =>
        Mods.FirstOrDefault(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The order a mod not listed yet gets when it is added: one past the highest order in the file (0 for none), held at
    /// <see cref="int.MaxValue"/>. Unlisted mods sort after every listed one whatever their number (<see cref="Plan"/>).
    /// </summary>
    public int NextOrder
    {
        get
        {
            if (Mods.Count == 0) return 0;
            int max = Mods.Max(m => m.Order);
            return max == int.MaxValue ? max : max + 1;
        }
    }

    /// <summary>
    /// <c>LoadPlanBuilder.Build</c>'s mod order: enabled listed mods by (order, id), then the unlisted ones (enabled by
    /// default) by id. A broken file gives nothing.
    /// </summary>
    public IReadOnlyList<string> Plan(IEnumerable<string> modIds)
    {
        if (IsBroken) return [];
        return modIds.Select(id => (Id: id, S: Find(id)))
                     .Where(x => x.S is null || x.S.Enabled)
                     .OrderBy(x => x.S is null ? 1 : 0)
                     .ThenBy(x => x.S?.Order ?? 0)
                     .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                     .Select(x => x.Id).ToList();
    }

    /// <summary><c>RuntimeSettings.Load</c>. Never throws.</summary>
    public static RuntimeSettingsFile Load(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return Defaults;
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            return Broken($"could not be read ({e.GetType().Name}: {e.Message})");
        }
        return Parse(text);
    }

    /// <summary>The file's text as the runtime reads it (the file exists). Never throws.</summary>
    public static RuntimeSettingsFile Parse(string text)
    {
        // empty is not missing: whatever emptied it, the user's "enabled": false entries are gone
        if (text.Trim().Length == 0) return Broken("is empty");

        SettingsWire? json;
        try
        {
            json = JsonSerializer.Deserialize<SettingsWire>(text, Options);
        }
        catch (JsonException e)
        {
            var where = e.Path is { Length: > 0 } p ? $" at {p}" : "";
            var line = e.LineNumber is { } n ? $", line {n + 1}" : "";
            return Broken($"is not valid{where}{line}");
        }
        catch (Exception e)
        {
            return Broken($"could not be parsed ({e.GetType().Name}: {e.Message})");
        }

        if (json is null) return Broken("is null, not an object");
        if (json.Schema is { } schema && !schema.Trim().Equals(SchemaId, StringComparison.OrdinalIgnoreCase))
            return Broken($"has schema '{schema}', not '{SchemaId}'");
        if (ParseLevel(json.LogLevel) is not { } level)
            return Broken($"has logLevel '{json.LogLevel}', not debug/info/warn/error/fatal");

        var mods = new List<RuntimeSettingsEntry>();
        var warnings = new List<string>();
        int index = -1;
        foreach (var entry in json.Mods ?? [])
        {
            index++;
            if (entry is null) return Broken($"has a null entry at mods[{index}]");
            if (string.IsNullOrWhiteSpace(entry.Id)) return Broken($"has an entry with no id at mods[{index}]");
            string id = entry.Id.Trim();
            if (mods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"{FileName}: mods[{index}] lists '{id}' again; the first entry is used");
                continue;
            }
            mods.Add(new RuntimeSettingsEntry(id, entry.Enabled ?? true, entry.Order ?? 0));
        }
        return new RuntimeSettingsFile { Exists = true, Console = json.Console ?? false, LogLevel = level, Mods = mods, Warnings = warnings };
    }

    private static RuntimeSettingsFile Broken(string why) => new() { Exists = true, Problem = $"{FileName} {why}" };

    // spelled out, as the runtime does: Enum.TryParse would also take "3" and "debug, info"
    private static string? ParseLevel(string? text)
    {
        if (text is null) return "info";
        string level = text.Trim().ToLowerInvariant();
        return level is "debug" or "info" or "warn" or "error" or "fatal" ? level : null;
    }

    // ---- the runtime's wire shape (ModJson.cs: SettingsJson, ModEntryJson, ContentJson.Options) ---------------------

    private sealed class SettingsWire
    {
        public string? Schema { get; set; }
        public bool? Console { get; set; }
        public string? LogLevel { get; set; }
        public List<EntryWire?>? Mods { get; set; }
    }

    private sealed class EntryWire
    {
        public string? Id { get; set; }
        public bool? Enabled { get; set; }
        public int? Order { get; set; }
    }

    /// <summary>ContentJson.Options, shared with mod.json (<see cref="RuntimeContent"/>).</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
