using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nightrunner.Core.Games;

/// <summary>What a mod switch did.</summary>
public enum RuntimeSwitchState { Written, Unchanged, Refused }

/// <summary>The outcome of switching one mod on or off in <c>nightrunner.json</c>; <paramref name="Message"/> is UI text.</summary>
public sealed record RuntimeSwitch(string ModId, bool Enabled, RuntimeSwitchState State, string Message)
{
    public bool Written => State == RuntimeSwitchState.Written;

    public static RuntimeSwitch Refuse(string modId, bool enabled, string why) => new(modId, enabled, RuntimeSwitchState.Refused, why);
}

/// <summary>
/// The one write into a game folder this tool makes (CLAUDE.md, Rules): NightrunnerProxy's settings file
/// <c>bin\x64\Nightrunner\nightrunner.json</c>, to switch a mod on or off. Nothing else is written there — no mod files,
/// no copies, no deletes (the <c>.tmp</c> and <c>.bak</c> beside it are part of writing that one file).
/// </summary>
/// <remarks>
/// <para>The format is the runtime's own (NightrunnerProxy rc1 <c>Core/Content/RuntimeSettings.cs</c>, <c>ModJson.cs</c>;
/// read here by <see cref="RuntimeSettingsFile"/>):
/// <c>{ "schema": "nightrunner/settings@1", "console": false, "logLevel": "info", "mods": [ { "id", "enabled", "order" } ] }</c>,
/// camelCase, read case-insensitively with comments and trailing commas allowed. The first entry whose trimmed id
/// matches (ignoring case) is the mod's; <c>enabled</c> absent is on, <c>order</c> absent is 0; a mod not listed is on
/// and sorts after every listed one (<c>LoadPlanBuilder</c>).</para>
/// <para>Read-modify-write on a <see cref="JsonNode"/> tree: every key not touched (console, logLevel, unknown keys,
/// other mods' entries, their order and key spelling) stays; only the mod's <c>enabled</c> value changes, or, for a
/// mod not listed yet, an entry at <see cref="RuntimeSettingsFile.NextOrder"/> is appended. A new file gets the runtime's
/// defaults (console off) and schema. The file keeps its line endings, indent, one-line entries, BOM and final newline,
/// so switching a mod and back gives the same bytes. Refused, with the reason: runtime modding off, no runtime module for
/// the game, NightrunnerProxy not installed, the game running from the install's bin folder, a file the runtime treats as
/// broken (it loads no mods: empty, not valid, wrong schema or logLevel, a null or id-less entry — the switch would not
/// say what the user meant), comments (they would be lost), no write access. Nothing is written that the runtime would
/// read as broken.</para>
/// </remarks>
public static partial class RuntimeSettingsWriter
{
    /// <summary><c>RuntimeSettings.SchemaId</c>.</summary>
    public const string SchemaId = RuntimeSettingsFile.SchemaId;

    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Switch <paramref name="modId"/> on or off for the next game start. <paramref name="running"/> finds processes whose
    /// executable is under a folder (default <see cref="ProcessesUnder"/>; deploy.ps1's check). Never throws.
    /// </summary>
    public static RuntimeSwitch SetEnabled(GameInstall install, string modId, bool enabled, bool runtimeModding,
                                           Func<string, IReadOnlyList<int>>? running = null)
    {
        RuntimeSwitch Refuse(string why) => RuntimeSwitch.Refuse(modId, enabled, why);
        if (!RuntimeModding.HasModule(install.Profile)) return Refuse(RuntimeContent.NoModule(install.Id));
        if (!runtimeModding) return Refuse("runtime modding off");
        var check = RuntimeModding.Validate(install);
        if (!check.Found) return Refuse(check.Flavor == RuntimeFlavor.Proxy ? "runtime incomplete" : "runtime not installed");
        IReadOnlyList<int> pids;
        try { pids = (running ?? ProcessesUnder)(install.Paths.Bin); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Refuse($"cannot check for the game ({e.Message})");
        }
        if (pids.Count > 0) return Refuse($"game running (pid {string.Join(", ", pids)})");

        var content = RuntimeContent.Read(install);
        if (content.Settings.IsBroken) return Refuse(content.Settings.Problem!);
        var mod = content.Mods.FirstOrDefault(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (mod is null) return Refuse($"no mod '{modId}'");
        if (mod.Enabled == enabled) return new RuntimeSwitch(mod.Id, enabled, RuntimeSwitchState.Unchanged, enabled ? "already on" : "already off");

        string path = Path.Combine(install.Paths.RuntimeFolder, RuntimeContent.SettingsName);
        byte[]? old;
        try { old = File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refuse($"{RuntimeContent.SettingsName} unreadable ({e.Message})"); }

        byte[] next;
        try { next = Switch(old, content, mod.Id, enabled); }
        catch (FormatException e) { return Refuse(e.Message); }

        try { WriteAtomic(path, next); }
        catch (UnauthorizedAccessException) { return Refuse("no write access"); }
        catch (IOException e) { return Refuse($"not written ({e.Message})"); }
        return new RuntimeSwitch(mod.Id, enabled, RuntimeSwitchState.Written, "applies at next game start");
    }

    // ---- the edit ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="old"/> (the file's bytes, null when there is none) with <paramref name="modId"/> switched. Pure: no
    /// disk access. Throws <see cref="FormatException"/> with UI text when the file cannot be edited faithfully.
    /// </summary>
    public static byte[] Switch(byte[]? old, RuntimeContent content, string modId, bool enabled)
    {
        var mod = content.Mods.FirstOrDefault(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))
                  ?? throw new FormatException($"no mod '{modId}'");
        bool bom = old is { Length: >= 3 } && old[0] == 0xEF && old[1] == 0xBB && old[2] == 0xBF;
        string? text = old is null ? null : new UTF8Encoding(false).GetString(old, bom ? 3 : 0, old.Length - (bom ? 3 : 0));

        JsonObject root;
        var style = Style.New;
        if (text is null)
        {
            // RuntimeSettings' defaults (console off, logLevel info), in the order its Save writes them
            root = new JsonObject { ["schema"] = SchemaId, ["console"] = false, ["logLevel"] = "info", ["mods"] = new JsonArray() };
        }
        else
        {
            // a file the runtime refuses (an empty one included) loads no mods; switching one mod would not say what the
            // user meant for the others, so it is left for the user to fix
            if (RuntimeSettingsFile.Parse(text).Problem is { } problem) throw new FormatException(problem);
            if (HasComments(text)) throw new FormatException($"{RuntimeContent.SettingsName} has comments (would be lost)");
            try
            {
                root = JsonNode.Parse(text, null, new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                       ?? throw new FormatException($"{RuntimeContent.SettingsName} is not an object");
                Touch(root);   // materialise every object: a duplicate key throws here, not halfway through the edit
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
            {
                throw new FormatException($"{RuntimeContent.SettingsName} not editable ({e.Message})");
            }
            style = Style.Of(text);
        }

        string modsKey = Key(root, "mods") ?? "mods";
        if (root[modsKey] is not JsonArray mods)
        {
            if (root[modsKey] is not null) throw new FormatException($"{RuntimeContent.SettingsName}: mods is not a list");
            root[modsKey] = mods = new JsonArray();
        }
        var entry = mods.OfType<JsonObject>().FirstOrDefault(o =>
            Key(o, "id") is { } k && o[k] is JsonValue v && v.TryGetValue<string>(out var id) &&
            id.Trim().Equals(mod.Id, StringComparison.OrdinalIgnoreCase));
        var was = text is null ? RuntimeSettingsFile.Defaults : RuntimeSettingsFile.Parse(text);
        if (entry is not null)
            entry[Key(entry, "enabled") ?? "enabled"] = enabled;
        else
            mods.Add(new JsonObject { ["id"] = mod.Id, ["enabled"] = enabled, ["order"] = was.NextOrder });   // after every listed mod

        string written = style.Write(root);
        Verify(was, written, content, mod, enabled);
        var bytes = Encoding.UTF8.GetBytes(written);
        return bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes;
    }

    /// <summary>
    /// What the runtime would make of the new file: not broken, the mod has the asked-for switch and the other mods' plan
    /// (<see cref="RuntimeSettingsFile.Plan"/>) is unchanged. A mismatch is a bug here, so nothing is written.
    /// </summary>
    private static void Verify(RuntimeSettingsFile was, string after, RuntimeContent content, RuntimeMod mod, bool enabled)
    {
        var now = RuntimeSettingsFile.Parse(after);
        if (now.Problem is { } problem) throw new FormatException($"switch would leave {problem} (not written)");
        var others = content.Mods.Where(m => m.Valid && !m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)).Select(m => m.Id).ToList();
        if (!was.Plan(others).SequenceEqual(now.Plan(others), StringComparer.OrdinalIgnoreCase) ||
            (now.Find(mod.Id)?.Enabled ?? true) != enabled)
            throw new FormatException("switch would change other mods (not written)");
    }

    private static void Touch(JsonNode? node)
    {
        if (node is JsonObject o) foreach (var p in o) Touch(p.Value);
        else if (node is JsonArray a) foreach (var n in a) Touch(n);
    }

    private static string? Key(JsonObject o, string name) =>
        o.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool HasComments(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text),
                                        new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.Comment) return true;
        return false;
    }

    // ---- the file's own style ---------------------------------------------------------------------------------------

    private sealed partial class Style
    {
        public static Style New => new() { NewLine = Environment.NewLine, Indent = "  ", FinalNewLine = true, InlineEntries = true };

        public required string NewLine { get; init; }
        public required string Indent { get; init; }
        public required bool FinalNewLine { get; init; }

        /// <summary>An object of plain values inside a list is written on one line (<c>{ "id": "x", "enabled": true }</c>).</summary>
        public required bool InlineEntries { get; init; }

        private static readonly JsonSerializerOptions Out = new() { Encoder = JavaScriptEncoderRelaxed };
        private static JavaScriptEncoder JavaScriptEncoderRelaxed => JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        public static Style Of(string text)
        {
            var indent = IndentRx().Match(text);
            bool anyEntry = EntryStartRx().IsMatch(text);
            return new Style
            {
                NewLine = text.Contains("\r\n") ? "\r\n" : "\n",
                Indent = indent.Success ? indent.Groups[1].Value : "  ",
                FinalNewLine = text.EndsWith('\n'),
                InlineEntries = !anyEntry || InlineRx().IsMatch(text),
            };
        }

        public string Write(JsonObject root)
        {
            var sb = new StringBuilder();
            Node(sb, root, 0, inArray: false);
            if (FinalNewLine) sb.Append(NewLine);
            return sb.ToString();
        }

        private static bool Plain(JsonNode? n) => n is null or JsonValue;

        private void Node(StringBuilder sb, JsonNode? node, int depth, bool inArray)
        {
            switch (node)
            {
                case null:
                    sb.Append("null");
                    break;
                case JsonValue v:
                    sb.Append(v.ToJsonString(Out));
                    break;
                case JsonObject o when o.Count == 0:
                    sb.Append("{}");
                    break;
                case JsonObject o when inArray && InlineEntries && o.All(p => Plain(p.Value)):
                    sb.Append("{ ");
                    sb.AppendJoin(", ", o.Select(p => $"{JsonSerializer.Serialize(p.Key, Out)}: {Inline(p.Value)}"));
                    sb.Append(" }");
                    break;
                case JsonObject o:
                    sb.Append('{');
                    int i = 0;
                    foreach (var p in o)
                    {
                        sb.Append(i++ == 0 ? "" : ",").Append(NewLine).Append(Pad(depth + 1));
                        sb.Append(JsonSerializer.Serialize(p.Key, Out)).Append(": ");
                        Node(sb, p.Value, depth + 1, inArray: false);
                    }
                    sb.Append(NewLine).Append(Pad(depth)).Append('}');
                    break;
                case JsonArray a when a.Count == 0:
                    sb.Append("[]");
                    break;
                case JsonArray a when a.All(Plain):
                    sb.Append('[').AppendJoin(", ", a.Select(Inline)).Append(']');
                    break;
                case JsonArray a:
                    sb.Append('[');
                    for (int k = 0; k < a.Count; k++)
                    {
                        sb.Append(k == 0 ? "" : ",").Append(NewLine).Append(Pad(depth + 1));
                        Node(sb, a[k], depth + 1, inArray: true);
                    }
                    sb.Append(NewLine).Append(Pad(depth)).Append(']');
                    break;
            }
        }

        private static string Inline(JsonNode? n) => n is null ? "null" : n.ToJsonString(Out);

        private string Pad(int depth) => string.Concat(Enumerable.Repeat(Indent, depth));

        [GeneratedRegex(@"\n([ \t]+)\S")]
        private static partial Regex IndentRx();

        [GeneratedRegex(@"\[\s*\{")]
        private static partial Regex EntryStartRx();

        [GeneratedRegex(@"(?m)^[ \t]*\{[^\r\n{}]*\}[ \t]*,?[ \t]*\r?$")]
        private static partial Regex InlineRx();
    }

    // ---- disk -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Write <paramref name="bytes"/> to <c>path.tmp</c> in the same folder, then swap it in with
    /// <see cref="File.Replace(string, string, string?, bool)"/> (the old file becomes <c>path.bak</c>), or move it in when
    /// there was no file. The temp file is removed if anything fails.
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + TempSuffix;
        if (File.Exists(path))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) throw new UnauthorizedAccessException("read-only");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }   // write access, before anything changes
        }
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + BackupSuffix, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // ---- is the game running --------------------------------------------------------------------------------------

    /// <summary>
    /// Processes whose executable is under <paramref name="folder"/> (deploy.ps1: <c>$_.Path.StartsWith($GameBin)</c>).
    /// The image path comes from <c>QueryFullProcessImageName</c> with limited query rights, so elevated processes are seen too.
    /// </summary>
    public static IReadOnlyList<int> ProcessesUnder(string folder)
    {
        string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pids = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (ImagePath(p.Id) is { } image && image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) pids.Add(p.Id);
            }
        }
        pids.Sort();
        return pids;
    }

    private static string? ImagePath(int pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        nint h = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (h == 0) return null;
        try
        {
            var buf = new char[1024];
            uint size = (uint)buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
