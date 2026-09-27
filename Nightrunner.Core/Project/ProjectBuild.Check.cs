using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using File = System.IO.File;

namespace Nightrunner.Core.Project;

/// <summary>
/// What a Check found: the report <see cref="ProjectBuild.Build"/> would write (plus <c>check</c>, and <c>refused</c> when
/// the build refuses), its warnings and verdict. Nothing of it was kept on disk.
/// </summary>
public sealed record CheckResult(JsonObject Report, IReadOnlyList<string> Warnings, string Verdict, bool Ok, string? Refused);

public static partial class ProjectBuild
{
    /// <summary>
    /// Dry run of <see cref="Build"/>: the same scan, name and template refusals, texture encode, scene split, mesh and
    /// clip encode, prefab edit validation, write and verify — the very same code, run on a copy of the project whose
    /// build folder is a new temp folder (the split and the verify read files back, so they cannot run without
    /// writing). The temp folder is deleted afterwards; the project, its manifest and its build folder are not
    /// touched. The report's <c>diff</c> compares the would-be outputs with the last build's (<see cref="LastReport"/>).
    /// </summary>
    public static CheckResult Check(ModProject project, BuildEnv env, Action<string>? say = null, CancellationToken ct = default)
    {
        var previous = LastReport(project);
        string temp = Path.Combine(Path.GetTempPath(), $"nightrunner-check-{Guid.NewGuid():N}");
        // an own copy of the manifest, pointed at the temp folder and never saved
        var shadow = ModProject.Open(project.Folder);
        shadow.Manifest.BuildFolder = temp;
        JsonObject report;
        IReadOnlyList<string> warnings;
        string verdict;
        bool ok;
        string? refused = null;
        try
        {
            var r = Build(shadow, env, say, ct);
            (report, warnings, verdict, ok) = (r.Report, r.Warnings, r.Verdict, r.Verified);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // a refusal, or a format problem the build would stop at: that is the check's answer, by name
            refused = e.Message;
            (warnings, verdict, ok) = ([], e.Message, false);
            report = new JsonObject
            {
                ["schema"] = Schema, ["project"] = project.Name, ["game"] = project.Manifest.Game ?? env.Install?.Id,
                ["refused"] = e.Message, ["warnings"] = new JsonArray(), ["verified"] = false, ["verdict"] = e.Message,
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logging.Log.Warn("check", $"{temp}: {e.Message}");
            }
        }
        report.Remove("install");
        report["check"] = true;
        report["diff"] = Diff(previous, report["hashes"] as JsonObject);
        return new CheckResult(report, warnings, verdict, ok, refused);
    }

    /// <summary>The report the last build of <paramref name="project"/> wrote (<c>&lt;name&gt;.build.json</c>), or null.</summary>
    public static JsonObject? LastReport(ModProject project) => LastReport(project.BuildFolder, project.Name);

    public static JsonObject? LastReport(string buildFolder, string projectName)
    {
        string path = Path.Combine(buildFolder, $"{projectName}.build.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Record <c>hashes</c> (per output, per resource or member) and <c>diff</c> (against the report already in
    /// <paramref name="outDir"/>) in a build's report. Called by <see cref="Build"/> before it writes the report.
    /// </summary>
    private static void AddHashes(JsonObject report, string outDir, string projectName, string? rpack, string? anims, string? pak)
    {
        var hashes = OutputHashes(rpack, anims, pak);
        report["hashes"] = hashes;
        report["diff"] = Diff(LastReport(outDir, projectName), hashes);
    }

    /// <summary>Hashes of every resource of the written packs and every member of the PAK: <c>{rpack|anims|pak: {name: hash}}</c>.</summary>
    public static JsonObject OutputHashes(string? rpack, string? anims, string? pak)
    {
        var all = new JsonObject();
        if (rpack is not null && File.Exists(rpack)) all["rpack"] = PackHashes(rpack);
        if (anims is not null && File.Exists(anims)) all["anims"] = PackHashes(anims);
        if (pak is not null && File.Exists(pak)) all["pak"] = PakHashes(pak);
        return all;
    }

    /// <summary>
    /// One hash per resource: SHA-256 (first 8 bytes, hex) of its type and flags and of each part's type, length and
    /// bytes. Keyed by name; a second resource of the same name gets <c>name#TT</c> (its type).
    /// </summary>
    public static JsonObject PackHashes(string path)
    {
        using var pack = RpackFile.Open(path);
        var map = new JsonObject();
        Span<byte> head = stackalloc byte[5];
        for (int i = 0; i < pack.Count; i++)
        {
            var lg = pack.Logicals[i];
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            h.AppendData([lg.Type, lg.Flags]);
            for (int k = 0; k < lg.PartCount; k++)
            {
                int pi = (int)lg.FirstPart + k;
                var data = pack.ReadPart(pi);
                head[0] = pack.PartType(pi);
                BinaryPrimitives.WriteInt32LittleEndian(head[1..], data.Length);
                h.AppendData(head);
                h.AppendData(data);
            }
            string name = pack.Name(i);
            map[map.ContainsKey(name) ? $"{name}#{lg.Type:X2}" : name] = Convert.ToHexStringLower(h.GetHashAndReset().AsSpan(0, 8));
        }
        return map;
    }

    /// <summary>One hash per PAK member (first 8 bytes of SHA-256 of its bytes), keyed by <see cref="MemberKey"/>.</summary>
    public static JsonObject PakHashes(string path)
    {
        using var pak = PakIndex.Open(path);
        var map = new JsonObject();
        foreach (var m in pak.Members)
            map[MemberKey(m.Name)] = Convert.ToHexStringLower(SHA256.HashData(pak.Read(m, int.MaxValue)).AsSpan(0, 8));
        return map;
    }

    /// <summary>A PAK member path as hashes and item outputs key it: forward slashes, lower case.</summary>
    public static string MemberKey(string member) => member.Replace('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Per output resource (<c>&lt;rpack|anims|pak&gt;/&lt;name&gt;</c>): <c>added</c>, <c>changed</c> or <c>unchanged</c> against
    /// <paramref name="previous"/>'s hashes, <c>removed</c> for what only the previous build had, and <c>unknown</c> for
    /// everything when the previous report predates hashes. No previous report: everything is added.
    /// </summary>
    public static JsonObject Diff(JsonObject? previous, JsonObject? hashes)
    {
        var diff = new JsonObject();
        var before = previous?["hashes"] as JsonObject;
        foreach (var (pack, node) in hashes ?? [])
            foreach (var (name, hash) in node as JsonObject ?? [])
                diff[$"{pack}/{name}"] = previous is null ? "added"
                    : before is null ? "unknown"
                    : (string?)before[pack]?[name] is not { } old ? "added"
                    : old == (string?)hash ? "unchanged" : "changed";
        foreach (var (pack, node) in before ?? [])
            foreach (var (name, _) in node as JsonObject ?? [])
                if (hashes?[pack]?[name] is null) diff[$"{pack}/{name}"] = "removed";
        return diff;
    }

    /// <summary>Why a template cannot be used — not in the open game, not indexed, or not that type — or null.</summary>
    public static string? TemplateRefusal(RpackCatalog catalog, string label, int index, string from, byte type = 0x10)
    {
        try
        {
            Template(catalog, label, index, from, type);
            return null;
        }
        catch (ProjectException e)
        {
            return e.Message;
        }
    }

    /// <summary>The template resource an item is built from (as <see cref="Build"/> resolves it); throws by name.</summary>
    public static (RpackFile Pack, int Index) TemplateOf(RpackCatalog catalog, string label, int index, string from, byte type = 0x10) =>
        Template(catalog, label, index, from, type);
}
