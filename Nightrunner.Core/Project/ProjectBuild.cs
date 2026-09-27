using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using File = System.IO.File;

namespace Nightrunner.Core.Project;

/// <summary>
/// What a full build needs from the open game. <see cref="LoadPng"/> decodes a PNG to BGRA32. <see cref="PlainClips"/>
/// writes every clip in the plain form (one 0x40 part) even when its template is a stream pair (0x44 + 0x45).
/// <see cref="RuntimeModding"/> on lays the outputs out as a NightrunnerProxy mod folder (<see cref="ModId"/>, by default
/// <see cref="ProjectBuild.ModId(string)"/> of the project name); off, they go to the stock folders.
/// </summary>
public sealed record BuildEnv(GameInstall? Install, RpackCatalog Catalog, ModelCatalog? Models,
                              Func<string, (byte[] Bgra, int Width, int Height)> LoadPng,
                              string? RpackName = null, string? PakName = null, bool RuntimeModding = false,
                              bool PlainClips = false)
{
    /// <summary>
    /// Lay the verified outputs out as a NightrunnerProxy mod folder, <c>&lt;build&gt;\&lt;ModId&gt;\</c> with its
    /// <c>mod.json</c> (<see cref="ProjectBuild.WriteModFolder"/>); null: the project name's id with
    /// <see cref="RuntimeModding"/> on, else not written. Never written into the game folder.
    /// </summary>
    public string? ModId { get; init; }
}

public sealed record FullBuildResult(string? RpackPath, string? PakPath, JsonObject Report, IReadOnlyList<string> Warnings,
                                     string Verdict, bool Verified)
{
    /// <summary>The clip pack (<see cref="ProjectBuild.AnimsPackName"/>), or null when the project has no anim items.</summary>
    public string? AnimsRpackPath { get; init; }

    /// <summary>The NightrunnerProxy mod folder written (<see cref="BuildEnv.ModId"/>), or null.</summary>
    public string? ModFolder { get; init; }
}

/// <summary>
/// One file a build writes: <paramref name="Kind"/> rpack | anims | pak and where it goes (relative to the game root).
/// For a NightrunnerProxy mod folder, <see cref="Destination"/> is the file's folder inside <c>mods\&lt;id&gt;</c>,
/// <paramref name="Order"/> its item order in mod.json and <see cref="At"/> its load point; for a stock folder both are null.
/// </summary>
public sealed record BuildOutput(string Kind, string File, string Destination, int? Order)
{
    /// <summary>mod.json <c>at</c> (<c>after-builtins</c> / <c>before:&lt;pack&gt;</c>); null for the stock layout.</summary>
    public string? At { get; init; }
}

/// <summary>
/// The whole project build (port of the model-building parts of <c>project.py::build_project</c>): textures, meshes
/// and model scenes into one new rpack, clips into their own <c>&lt;name&gt;_anims_pc.rpack</c> (loaded before the stock
/// anim packs, <see cref="AnimsAt"/>: clip names register first-wins), <c>.model</c> overrides into one new
/// <c>dataN.pak</c>, all in
/// <see cref="ModProject.BuildFolder"/>, plus <c>&lt;project&gt;.build.json</c>. Output names default to the prototype's:
/// the first free <c>assets_N_pc.rpack</c> (N from 2) in the game's assets folder and the first free <c>dataN.pak</c>
/// (0..8, never a stock archive) in its source folder. Nothing is written into the game folder. Meshes, scenes and
/// models are DLTB only.
/// </summary>
public static partial class ProjectBuild
{
    public const string Schema = "nightrunner.project_build/1";

    /// <summary>The clip pack beside an rpack name: <c>assets_2_pc.rpack</c> → <c>assets_2_anims_pc.rpack</c>.</summary>
    public static string AnimsPackName(string rpackName)
    {
        string stem = ProjectBuilder.PackName(rpackName)[..^".rpack".Length];
        if (stem.EndsWith("_pc", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
        return stem + "_anims_pc.rpack";
    }

    /// <summary>
    /// What a build of <paramref name="contents"/> writes and where each file goes: the stock folders, or with
    /// <paramref name="modId"/> (runtime modding on) the NightrunnerProxy mod folder layout — each file's folder under
    /// <c>bin\x64\Nightrunner\mods\&lt;id&gt;</c>, its item order in mod.json, and its load point (<see cref="BuildOutput.At"/>).
    /// </summary>
    public static List<BuildOutput> Outputs(ProjectAssets.Contents contents, bool textures, string rpackName, string? pakName,
                                            GameInstall? install, string? modId)
    {
        var (rpackTo, pakTo) = Destinations(install);
        var list = new List<BuildOutput>();
        rpackName = ProjectBuilder.PackName(rpackName);
        bool main = textures || contents.Meshes.Count + contents.Scenes.Count + contents.Prefabs.Count > 0;
        bool anims = contents.Anims.Count > 0, pak = contents.Models.Count > 0 && pakName is { Length: > 0 };
        if (modId is not null)
        {
            string root = $"{ModsDestination(install)}/{modId}";
            foreach (var item in ModItems(main ? rpackName : null, anims ? AnimsPackName(rpackName) : null, pak ? PakName(pakName!) : null))
                list.Add(new(item.Output, Path.GetFileName(item.File), $"{root}/{Path.GetDirectoryName(item.File)!.Replace('\\', '/')}", item.Order)
                         { At = item.At });
            return list;
        }
        if (main)
            list.Add(new("rpack", rpackName, rpackTo, null));
        if (anims)
            list.Add(new("anims", AnimsPackName(rpackName), rpackTo, null));
        if (pak)
            list.Add(new("pak", PakName(pakName!), pakTo, null));
        return list;
    }

    // ---- NightrunnerProxy mod folder ------------------------------------------------------------------------------

    /// <summary>mod.json beside the mod's files (NightrunnerProxy <c>Core/Content/ModCatalog.ManifestName</c>).</summary>
    public const string ModManifest = "mod.json";

    /// <summary>
    /// Where the clip pack loads: immediately before <c>common_anims_pc</c>. Clip names register first-wins, so an
    /// override must be registered before the stock clip packs. Measured on DLTB (NightrunnerProxy LoadPack log,
    /// 2026-09-26): <c>common_anims_pc</c> is the first anim pack the engine loads (#21, then player_anims_static_pc,
    /// player_anims_pc), and the proxy loads a <c>before:</c> item inside that LoadPack call, ahead of the original
    /// (<c>DltbContentHost.OnPackLoading</c>). The stock packs between LoadResources and it ship no clip; the boot packs
    /// before LoadResources do (engine_pc 2, lang_speech_en_pc 11,084), and those names cannot be overridden from any
    /// mod load point that is proven — the build names such clips. A clip overriding through this load point has not been
    /// verified in game by this tool; the build report says so.
    /// </summary>
    public const string AnimsAt = "before:common_anims_pc";

    /// <summary>The main pack and the pak load at the first LoadResources (paks mount first, then rpacks).</summary>
    public const string ModAt = "after-builtins";

    /// <summary>The build report's note on <see cref="AnimsAt"/>.</summary>
    public const string AnimsAtBasis =
        "common_anims_pc is the first anim pack in the measured DLTB load sequence (NightrunnerProxy LoadPack log, 2026-09-26); " +
        "the proxy loads before: items inside that LoadPack call, ahead of it; the stock packs between LoadResources and it ship no clip, " +
        "the boot packs (engine_pc, lang_speech_*_pc) register theirs earlier. Clip override from this load point: unverified in game.";

    /// <summary>
    /// The mod id for a project name: lower case, anything but <c>a-z 0-9 . _ -</c> becomes <c>-</c>. The runtime takes
    /// any id; this keeps the folder name and the id the same and safe.
    /// </summary>
    public static string ModId(string projectName)
    {
        var sb = new StringBuilder();
        foreach (char c in projectName.Trim().ToLowerInvariant())
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '-');
        string id = sb.ToString().Trim('-', '.');
        return id.Length > 0 ? id : "mod";
    }

    /// <summary>Why a mod id cannot be used, or null: it must be a bare folder name.</summary>
    public static string? ModIdRefusal(string id) =>
        id.Trim().Length == 0 || id != id.Trim() || !IsFileName(id) || id is "." or ".." ? $"'{id}' is not a mod id (a folder name)" : null;

    /// <summary>NightrunnerProxy's mods folder relative to the game root: <c>{data}/work/bin/x64/Nightrunner/mods</c>.</summary>
    public static string ModsDestination(GameInstall? install)
    {
        string data = install?.Paths.DataName ?? "ph_ft";
        var profile = install?.Profile ?? GameProfile.Dltb;
        string exe = (profile.ExeTemplates.Length > 0 ? profile.ExeTemplates[0] : "{data}/work/bin/x64/x.exe").Replace("{data}", data).Replace('\\', '/');
        string bin = exe.Contains('/') ? exe[..exe.LastIndexOf('/')] : "";
        return $"{bin}/{RuntimeContent.RuntimeFolderName}/{RuntimeContent.ModsFolderName}".TrimStart('/');
    }

    /// <summary>
    /// The items of the mod folder, in the sample modpack's layout: rpacks under <c>packs/</c>, the pak under
    /// <c>paks/</c>; the clip pack first (<see cref="AnimsAt"/>), then the main pack and the pak (<see cref="ModAt"/>).
    /// </summary>
    public static List<(string Output, string Kind, string File, string At, int Order)> ModItems(string? rpack, string? anims, string? pak)
    {
        var items = new List<(string, string, string, string, int)>();
        if (anims is not null) items.Add(("anims", "rpack", $"packs/{anims}", AnimsAt, items.Count));
        if (rpack is not null) items.Add(("rpack", "rpack", $"packs/{rpack}", ModAt, items.Count));
        if (pak is not null) items.Add(("pak", "pak", $"paks/{pak}", ModAt, items.Count));
        return items;
    }

    /// <summary>
    /// Lay verified outputs out as a NightrunnerProxy mod folder under <paramref name="buildFolder"/>: the files copied
    /// to <c>&lt;id&gt;\packs</c> / <c>&lt;id&gt;\paks</c>, and <c>mod.json</c> in the schema <c>Core/Content/ModJson.cs</c>
    /// reads (id, name, description, items: kind, file, at, order). A stale folder of the same id is replaced whole.
    /// Returns the folder and the manifest written.
    /// </summary>
    public static (string Folder, JsonObject Manifest) WriteModFolder(string buildFolder, string modId, string projectName,
                                                                     string? rpackPath, string? animsPath, string? pakPath)
    {
        if (ModIdRefusal(modId) is { } refusal) throw new ProjectException(refusal);
        string folder = Path.Combine(buildFolder, modId);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var items = new JsonArray();
        var sources = new Dictionary<string, string?> { ["rpack"] = rpackPath, ["anims"] = animsPath, ["pak"] = pakPath };
        foreach (var (output, kind, file, at, order) in ModItems(rpackPath is null ? null : Path.GetFileName(rpackPath),
                                                                animsPath is null ? null : Path.GetFileName(animsPath),
                                                                pakPath is null ? null : Path.GetFileName(pakPath)))
        {
            string to = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(sources[output]!, to);
            items.Add(new JsonObject { ["kind"] = kind, ["file"] = file, ["at"] = at, ["order"] = order });
        }
        if (items.Count == 0) throw new ProjectException("nothing to put in a mod folder");
        var manifest = new JsonObject
        {
            ["id"] = modId,
            ["name"] = projectName,
            ["description"] = $"Nightrunner build of {projectName}",
            ["items"] = items,
        };
        File.WriteAllText(Path.Combine(folder, ModManifest), manifest.ToJsonString(ProjectAssets.Indented));
        return (folder, manifest);
    }

    private static readonly string[] StockPaks = ["data0.pak", "data1.pak"];

    public static string NextFreeRpack(string? assets)
    {
        var names = assets is not null && Directory.Exists(assets)
            ? Directory.GetFiles(assets).Select(f => Path.GetFileName(f).ToLowerInvariant()).ToHashSet() : [];
        int n = 2;
        while (names.Contains($"assets_{n}_pc.rpack")) n++;
        return $"assets_{n}_pc.rpack";
    }

    public static string NextFreePak(string? source)
    {
        var names = new HashSet<string>(StockPaks, StringComparer.OrdinalIgnoreCase);
        if (source is not null && Directory.Exists(source))
            foreach (var f in Directory.GetFiles(source)) names.Add(Path.GetFileName(f));
        for (int n = 0; n <= 8; n++)
            if (!names.Contains($"data{n}.pak")) return $"data{n}.pak";
        throw new ProjectException("no free dataN.pak name (N 0..8)");
    }

    /// <summary>
    /// Why an .rpack name cannot be used, or null: not a bare file name, or a pack the game ships (a file in its
    /// assets folder that is not an <c>assets_N_pc.rpack</c> mod slot).
    /// </summary>
    public static string? RpackRefusal(string name, GameInstall? install)
    {
        if (!IsFileName(name)) return $"'{name}' is not a file name";
        if (!ModSlot.IsMatch(name) && install?.Assets is { } assets && File.Exists(Path.Combine(assets, name)))
            return $"{name} is a stock pack";
        return null;
    }

    /// <summary>Why a .pak name cannot be used, or null: not a bare file name, or a stock archive (data0, data1).</summary>
    public static string? PakRefusal(string name)
    {
        if (!IsFileName(name)) return $"'{name}' is not a file name";
        return StockPaks.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"{name} is a stock archive" : null;
    }

    public static string PakName(string name) =>
        name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ? name : name + ".pak";

    private static bool IsFileName(string name) =>
        name.Length > 0 && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static readonly System.Text.RegularExpressions.Regex ModSlot =
        new(@"^assets_\d+_pc\.rpack$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The stock folder each output belongs in without runtime modding, relative to the game root (<c>ph_ft/work/…</c>):
    /// the assets and source folders. With runtime modding the outputs go to the mod folder (<see cref="ModsDestination"/>).
    /// </summary>
    public static (string Rpack, string Pak) Destinations(GameInstall? install)
    {
        string data = install?.Paths.DataName ?? "ph_ft";
        var folders = install?.Profile.Folders ?? GameProfile.ChromeFolders;
        string Where(GameFolder stock) =>
            (folders.TryGetValue(stock, out var s) ? s : GameProfile.ChromeFolders[stock]).Replace("{data}", data);
        return (Where(GameFolder.Assets), Where(GameFolder.Data));
    }

    public static FullBuildResult Build(ModProject project, BuildEnv env, Action<string>? say = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var contents = ProjectAssets.Scan(project);
        warnings.AddRange(contents.Problems);
        string game = project.Manifest.Game ?? env.Install?.Id ?? "dltb";
        bool geometry = contents.Meshes.Count + contents.Scenes.Count + contents.Models.Count > 0;
        if (geometry && game != "dltb")
            throw new ProjectException($"{game.ToUpperInvariant()} mesh and model building is not supported (DLTB only)");
        if (contents.Anims.Count > 0 && game != "dltb")
            throw new ProjectException($"{game.ToUpperInvariant()} animation building is not supported (DLTB only)");

        string outDir = project.BuildFolder;
        Directory.CreateDirectory(outDir);
        string rpackName = ProjectBuilder.PackName(env.RpackName is { Length: > 0 } r ? r : NextFreeRpack(env.Install?.Assets));
        string pakName = env.PakName is { Length: > 0 } p ? PakName(p) : NextFreePak(env.Install?.Paths.Folder(GameFolder.Data));
        string animsName = AnimsPackName(rpackName);
        if ((RpackRefusal(rpackName, env.Install) ?? PakRefusal(pakName) ?? (contents.Anims.Count > 0 ? RpackRefusal(animsName, env.Install) : null))
            is { } refusal) throw new ProjectException(refusal);
        if (env.ModId is { } requestedId && ModIdRefusal(requestedId) is { } badId) throw new ProjectException(badId);
        string? modId = env.ModId ?? (env.RuntimeModding ? ModId(project.Name) : null);
        var report = new JsonObject { ["schema"] = Schema, ["project"] = project.Name, ["game"] = game };
        var items = new JsonArray();
        var specs = new List<ResourceSpec>();
        var animSpecs = new List<ResourceSpec>();
        var animField08s = new List<uint>();
        var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- textures --------------------------------------------------------------------------------------------
        var textureSet = ProjectBuilder.TextureSpecs(project, env.LoadPng, ct);
        warnings.AddRange(textureSet.Skipped);
        warnings.AddRange(textureSet.Warnings);
        var mainField08s = textureSet.SourcePacks.Select(s => ProjectBuilder.SourceField08(env.Catalog, s)).ToList();
        foreach (var (spec, line) in textureSet.Specs.Zip(textureSet.Built))
        {
            specs.Add(spec);
            built.Add(Encoding.UTF8.GetString(spec.Name));
            items.Add(new JsonObject { ["kind"] = "texture", ["name"] = Encoding.UTF8.GetString(spec.Name), ["result"] = line });
        }

        // ---- model scenes: split back per mesh, then build the meshes that changed -----------------------------
        string work = Path.Combine(outDir, $".{RawExporter_Safe(project.Name)}.work");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        var meshJobs = new List<(string Output, string SourcePack, int SourceIndex, string Scene, JsonObject? Sidecar, string From)>();
        try
        {
            foreach (var scene in contents.Scenes)
            {
                ct.ThrowIfCancellationRequested();
                say?.Invoke($"split {Path.GetFileName(scene.ScenePath)}");
                items.Add(new JsonObject
                {
                    ["kind"] = "scene", ["name"] = Path.GetFileName(scene.Folder),
                    ["from"] = $"scene/{Path.GetFileName(scene.Folder)}/{Path.GetFileName(scene.ScenePath)}",
                });
                foreach (var m in SplitScene(scene, env, Path.Combine(work, "split", Path.GetFileName(scene.Folder)), warnings))
                    meshJobs.Add(m);
            }
            foreach (var m in contents.Meshes)
                meshJobs.Add((m.OutputName, m.SourcePack, m.SourceIndex, m.ScenePath, m.Sidecar, $"mesh/{Path.GetFileName(m.Folder)}"));

            // ---- meshes ------------------------------------------------------------------------------------------
            foreach (var job in meshJobs)
            {
                ct.ThrowIfCancellationRequested();
                say?.Invoke($"mesh {job.Output}");
                if (!built.Add(job.Output)) throw new ProjectException($"{job.Output}: built twice ({job.From})");
                var (pack, index) = Template(env.Catalog, job.SourcePack, job.SourceIndex, job.From);
                var original = MeshDecoder.ReadParts(pack, index);
                var result = MeshBuild.Build(original.ByType, job.Output, job.Scene, job.Sidecar, new MeshBuildOptions { IgnoreBoneChanges = true });
                warnings.AddRange(result.Warnings.Select(w => $"{job.Output}: {w}"));
                specs.Add(FromTemplate(pack, index, job.Output, result.Parts));
                mainField08s.Add(pack.Header.Field08);
                items.Add(new JsonObject
                {
                    ["kind"] = "mesh", ["name"] = job.Output, ["from"] = job.From,
                    ["target"] = $"{job.SourcePack}:{index}", ["report"] = result.Report.DeepClone(),
                });
            }
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }

        // ---- clips: the edited scene resampled and encoded, in the template's form (stream pair or plain) --------
        foreach (var a in contents.Anims)
        {
            ct.ThrowIfCancellationRequested();
            say?.Invoke($"clip {a.OutputName}");
            string from = $"anim/{Path.GetFileName(a.Folder)}";
            if (!built.Add(a.OutputName)) throw new ProjectException($"{a.OutputName}: built twice ({from})");
            var (pack, index) = Template(env.Catalog, a.SourcePack, a.SourceIndex, from, Anim.Anm2Resource.TypeAnimation);
            var template = Anim.Anm2Resource.Decode(pack, index);
            // Add to project refuses these too; a hand-made or edited anim.json must not write bone keys over a face clip
            if (template.IsPoseWeights)
                throw new ProjectException($"{from}: {a.SourcePack}:{index} is a facial pose-weight clip, not bone animation");
            var imported = Anim.AnimImport.Import(a.ScenePath, a.Fps, template);
            var encoded = Anim.Anm2Encoder.Encode(imported.Clip, Anim.Anm2EncodeOptions.Recipe);
            var check = Anim.Anm2Encoder.SelfCheck(imported.Clip, encoded);
            if (!check.HeaderOk) throw new ProjectException($"{a.OutputName}: encoded clip fails its self-check ({check.Problem})");
            Anim.Anm2Resource.Read(pack, index, out bool streamPair);
            var parts = new Dictionary<byte, byte[]>();
            bool forced = false;
            if (streamPair && env.PlainClips)
            {
                // The two forms are the same bytes (plain = 0x44 ‖ 0x45, 40,083/40,083 DLTB pairs); the plain form's words
                // come from the clip's own plain copy, which every stream clip ships (census: DLTB 40,083/40,083,
                // DL2 28,560/28,560; all plain copies lf 0x01 [0x40 align 8, flags 0x40])
                (pack, index) = PlainCopy(env.Catalog, pack.Name(index))
                                ?? throw new ProjectException($"{from}: no plain (0x40) copy of {pack.Name(index)} to take the plain form's storage words from");
                streamPair = false;
                forced = true;
            }
            if (streamPair)
            {
                var (hd, pd) = Anim.Anm2Resource.SplitStreamPair(encoded);
                parts[Anim.Anm2Resource.PartHeader] = hd;
                parts[Anim.Anm2Resource.PartPayload] = pd;
            }
            else parts[Anim.Anm2Resource.TypeAnimation] = encoded;
            warnings.AddRange(imported.Notes.Select(n => $"{a.OutputName}: {n}"));
            if (modId is not null)
            {
                // a boot pack (engine, lang_speech_*) registers its clips before any mod load point, before:common_anims_pc included
                var boot = env.Catalog.Lookup(a.OutputName, Anim.Anm2Resource.TypeAnimation).Select(g => env.Catalog.Split(g).Entry)
                    .Where(e => RuntimeContent.IsBootPack(game, Path.GetFileName(e.Path))).Select(e => Path.GetFileName(e.Path)).Distinct().ToList();
                if (boot.Count > 0)
                    warnings.Add($"{a.OutputName}: also in {string.Join(", ", boot)}, which loads before {AnimsAt[7..]}: the mod's copy cannot win there");
            }
            animSpecs.Add(FromTemplate(pack, index, a.OutputName, parts));
            animField08s.Add(pack.Header.Field08);
            items.Add(new JsonObject
            {
                ["kind"] = "anim", ["name"] = a.OutputName, ["from"] = $"{from}/{Path.GetFileName(a.ScenePath)}",
                ["target"] = $"{a.SourcePack}:{a.SourceIndex}", ["form"] = streamPair ? "stream (0x44+0x45)" : forced ? "plain (0x40, forced)" : "plain (0x40)",
                ["tracks"] = imported.Clip.TrackCount, ["keys"] = imported.Keys, ["fps"] = a.Fps, ["bytes"] = encoded.Length,
                ["maxRotationDegrees"] = check.MaxRotationDegrees, ["maxTranslation"] = check.MaxTranslation,
            });
        }

        // ---- prefabs: the template Prefabs resource with the recorded edits, validated, re-parsed and checked ------
        if (contents.Prefabs.Count > 1)
            throw new ProjectException($"edits of {contents.Prefabs.Count} packs' Prefabs resources: one per build (they share the name 'Prefabs')");
        foreach (var pf in contents.Prefabs)
        {
            ct.ThrowIfCancellationRequested();
            string from = $"prefab/{Path.GetFileName(pf.Folder)}";
            if (game != "dltb") throw new ProjectException($"{from}: {game.ToUpperInvariant()} prefabs are not edited (DLTB only)");
            say?.Invoke($"prefabs {pf.SourcePack}");
            if (!built.Add(pf.SourceName)) throw new ProjectException($"{pf.SourceName}: built twice ({from})");
            var (pack, index) = Template(env.Catalog, pf.SourcePack, pf.SourceIndex, from, Prefab.PrefabContainer.ResourceType);
            var c = Prefab.PrefabContainer.Read(pack, index);
            int before = c.Primary.Length;
            try
            {
                Prefab.PrefabEdits.Apply(c, pf.Edits);
                var meta = c.EncodeMetadata();
                var back = Prefab.PrefabContainer.Parse(c.EncodePrimary(), meta);
                back.Validate();
                if (!back.EncodeMetadata().AsSpan().SequenceEqual(meta)) throw new ProjectException($"{from}: the edited resource does not re-encode byte-exactly");
                var doc = Prefab.PrefabDecoder.Decode(back);
                foreach (var op in pf.Edits)
                    if (Prefab.PrefabEdits.Check(doc, op) is { } bad) throw new ProjectException($"{from}: {op}: {bad}");
                mainField08s.Add(pack.Header.Field08);
                specs.Add(FromTemplate(pack, index, pf.SourceName, new Dictionary<byte, byte[]>
                {
                    [Prefab.PrefabContainer.PartImage] = c.EncodePrimary(), [Prefab.PrefabContainer.PartMeta] = meta,
                }));
            }
            catch (Prefab.PrefabFormatException e) { throw new ProjectException($"{from}: {e.Message}"); }
            items.Add(new JsonObject
            {
                ["kind"] = "prefab", ["name"] = pf.SourceName, ["from"] = from, ["target"] = $"{pf.SourcePack}:{index}",
                ["edits"] = pf.Edits.Count, ["primaryBytes"] = c.Primary.Length, ["primaryGrowth"] = c.Primary.Length - before,
                ["loaded"] = "never loaded in game",
            });
            warnings.Add($"{from}: an edited Prefabs resource has never been loaded by the game (see docs/formats.md, Prefab loading options)");
        }

        // Every output is written as <name>.unverified and replaces <name> only once Verify passes; a failed verify
        // keeps it as <name>.invalid and leaves an earlier <name> untouched (the prototype's build.py rule).
        var staged = new List<(string Staged, string Final, JsonObject Entry)>();
        var outputs = new JsonObject();
        string Stage(string name, string key, JsonObject entry)
        {
            string final = Path.Combine(outDir, name);
            outputs[key] = entry;
            staged.Add((final + Unverified, final, entry));
            return final + Unverified;
        }
        string? rpackStaged = null, animsStaged = null, pakStaged = null;
        var docs = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        string verdict;
        bool ok;
        try
        {
            if (specs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                say?.Invoke($"write {rpackName}");
                // the prototype's rule: 0x1000 with a mesh, else the source packs' value when they agree, else 0x1000
                uint field08 = ProjectBuilder.PackField08(meshJobs.Count > 0, mainField08s);
                var entry = new JsonObject { ["path"] = rpackName };
                rpackStaged = Stage(rpackName, "rpack", entry);
                var written = RpackWriter.Write(rpackStaged, specs, field08);
                entry["size"] = written.Size;
                entry["resources"] = written.Resources;
                entry["field08"] = $"0x{field08:X}";
            }
            if (animSpecs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                say?.Invoke($"write {animsName}");
                // clips only; the prototype's field08 rule (no mesh: the source packs' value when they agree — 0 for
                // every stock anim pack — else 0x1000), so a stream pair (0x44 stream-less + 0x45 stream) is laid out grouped
                uint field08 = RpackWriter.ProjectField08(null, false, animField08s);
                var entry = new JsonObject { ["path"] = animsName };
                animsStaged = Stage(animsName, "anims", entry);
                var written = RpackWriter.Write(animsStaged, animSpecs, field08);
                entry["size"] = written.Size;
                entry["resources"] = written.Resources;
                entry["field08"] = $"0x{field08:X}";
                if (modId is null)
                    warnings.Add($"{animsName}: clips override only from a pack loaded before the stock ones (runtime modding: {AnimsAt})");
            }

            // ---- PAK -----------------------------------------------------------------------------------------------
            foreach (var m in contents.Models)
            {
                var doc = ModelEdit.GameDoc(m.Load());
                foreach (var path in ModelEdit.MemberPaths(m.Member, m.PathMode)) docs[path] = doc;
                if (m.NoGear)
                    foreach (var path in ModelEdit.MemberPaths(ModelEdit.OutfitScript, m.PathMode))
                        texts[path] = ModelEdit.EmptyOutfitScript(OutfitSource(env));
                items.Add(new JsonObject { ["kind"] = "model", ["member"] = m.Member, ["paths"] = m.PathMode, ["noGear"] = m.NoGear });
            }
            if (docs.Count > 0 || texts.Count > 0)
            {
                say?.Invoke($"write {pakName}");
                var entry = new JsonObject { ["path"] = pakName };
                pakStaged = Stage(pakName, "pak", entry);
                var pak = PakWriter.Write(pakStaged, docs, texts, overwrite: true);
                entry["members"] = new JsonArray(pak.Members.Select(n => (JsonNode)n!).ToArray());
                entry["size"] = pak.Bytes;
            }
            if (staged.Count == 0) throw new ProjectException("nothing to build");
            var rpacks = new List<(string, int)>();
            if (rpackStaged is not null) rpacks.Add((rpackStaged, specs.Count));
            if (animsStaged is not null) rpacks.Add((animsStaged, animSpecs.Count));
            (verdict, ok) = Verify(rpacks, pakStaged, docs.Keys.ToList());
        }
        catch
        {
            foreach (var s in staged)
                try { File.Delete(s.Staged); } catch (IOException) { }
            throw;
        }

        // ---- keep what verified, set aside what did not ---------------------------------------------------------------
        var kept = new List<string>();
        foreach (var (from, final, entry) in staged)
        {
            string to = ok ? final : final + Invalid;
            File.Move(from, to, overwrite: true);
            if (ok && File.Exists(final + Invalid)) File.Delete(final + Invalid);   // a stale failure of the same output
            entry["path"] = Path.GetFileName(to);
            kept.Add(Path.GetFileName(to));
        }
        if (!ok) verdict += $"; kept as {string.Join(", ", kept)}";
        string? Final(string? stagedPath) => stagedPath is null ? null : stagedPath[..^Unverified.Length] + (ok ? "" : Invalid);
        string? rpackPath = Final(rpackStaged), animsPath = Final(animsStaged), pakPath = Final(pakStaged);

        // where each verified output goes: the stock folders, or with runtime modding the mod folder's packs/ and paks/
        var (rpackTo, pakTo) = Destinations(env.Install);
        string? modRoot = modId is null ? null : $"{ModsDestination(env.Install)}/{modId}";
        string? To(string? path, string name, string stock, string inMod) =>
            path is null || !ok ? null : modRoot is null ? $"{stock}/{name}" : $"{modRoot}/{inMod}/{name}";
        report["items"] = items;
        report["outputs"] = outputs;
        report["install"] = new JsonObject
        {
            ["rpack"] = To(rpackPath, rpackName, rpackTo, "packs"),
            ["anims"] = To(animsPath, animsName, rpackTo, "packs"),
            ["pak"] = To(pakPath, pakName, pakTo, "paks"),
        };
        string? modFolder = null;
        if (modId is not null)
        {
            if (!ok) warnings.Add($"{modId}: no mod folder written (verify failed)");
            else
            {
                if (game != GameProfile.Dltb.Id)
                    warnings.Add($"{modId}: NightrunnerProxy loads content on DLTB only (its source has no {game.ToUpperInvariant()} module)");
                var (folder, manifest) = WriteModFolder(outDir, modId, project.Name, rpackPath, animsPath, pakPath);
                modFolder = folder;
                var mod = new JsonObject
                {
                    ["id"] = modId,
                    ["folder"] = modId,
                    ["install"] = $"{ModsDestination(env.Install)}/{modId}",
                    ["items"] = manifest["items"]!.DeepClone(),
                };
                if (animsPath is not null)
                {
                    mod["animsAt"] = new JsonObject { ["at"] = AnimsAt, ["basis"] = AnimsAtBasis, ["verified"] = false };
                    warnings.Add($"{modId}: clip pack at {AnimsAt}: the load point is from the proxy source and the measured DLTB load order; a clip override through it is unverified in game");
                }
                report["mod"] = mod;
            }
        }
        report["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w!).ToArray());
        report["verified"] = ok;
        report["verdict"] = verdict;
        AddHashes(report, outDir, project.Name, rpackPath, animsPath, pakPath);
        File.WriteAllText(Path.Combine(outDir, $"{project.Name}.build.json"), report.ToJsonString(ProjectAssets.Indented));
        foreach (var w in warnings) Log.Warn("build", w);
        return new FullBuildResult(rpackPath, pakPath, report, warnings, verdict, ok) { AnimsRpackPath = animsPath, ModFolder = modFolder };
    }

    /// <summary>Suffix of an output written but not verified yet, and of one whose verify failed.</summary>
    public const string Unverified = ".unverified", Invalid = ".invalid";

    private static string RawExporter_Safe(string name) => Export.RawExporter.SafeName(name);

    /// <summary>A plain-form (single 0x40 part) copy of a clip in the open game, or null.</summary>
    private static (RpackFile Pack, int Index)? PlainCopy(RpackCatalog catalog, string clip)
    {
        foreach (int gid in catalog.Lookup(clip, Anim.Anm2Resource.TypeAnimation))
        {
            var (entry, i) = catalog.Split(gid);
            var lg = entry.Pack!.Logicals[i];
            if (lg.PartCount == 1 && entry.Pack.PartType((int)lg.FirstPart) == Anim.Anm2Resource.TypeAnimation) return (entry.Pack, i);
        }
        return null;
    }

    /// <summary>The template resource a mesh is built from: its pack by label, the logical index, type 0x10.</summary>
    private static (RpackFile Pack, int Index) Template(RpackCatalog catalog, string label, int index, string from, byte type = 0x10)
    {
        var entry = catalog.Packs.FirstOrDefault(p => p.Label.Replace('\\', '/').Equals(label.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                                                      || Path.GetFileName(p.Path).Equals(Path.GetFileName(label), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ProjectException($"{from}: pack {label} is not in the open game");
        var pack = entry.Pack ?? throw new ProjectException($"{from}: pack {label} is not indexed ({entry.Error})");
        if ((uint)index >= (uint)pack.Count || pack.Logicals[index].Type != type)
            throw new ProjectException($"{from}: {label}:{index} is not a {(type == 0x10 ? "mesh" : $"type 0x{type:X2} resource")}");
        return (pack, index);
    }

    /// <summary>A resource spec with the template's parts, storage words, flag bits and logical flags; rebuilt parts replace theirs.</summary>
    private static ResourceSpec FromTemplate(RpackFile pack, int index, string name, IReadOnlyDictionary<byte, byte[]> rebuilt)
    {
        var lg = pack.Logicals[index];
        var parts = new List<PartSpec>();
        for (int k = 0; k < lg.PartCount; k++)
        {
            int pi = (int)lg.FirstPart + k;
            var storage = pack.PartStorage(pi);
            byte[] data = rebuilt.TryGetValue(storage.Type, out var b) ? b : pack.ReadPart(pi);
            parts.Add(new PartSpec(storage.Type, data, storage.AlignRaw, storage.Flags, storage.Metadata,
                                   pack.Physicals[pi].FlagBits, pack.Physicals[pi].Fc));
        }
        return new ResourceSpec(Encoding.UTF8.GetBytes(name), lg.Type, lg.Flags, parts);
    }

    private static string? OutfitSource(BuildEnv env)
    {
        if (env.Models is not { } models) return null;
        foreach (var member in new[] { ModelEdit.OutfitScript, ModelEdit.OutfitScriptLegacy })
            foreach (var pak in models.PakPaths.Where(p => Path.GetFileName(p).Equals("data0.pak", StringComparison.OrdinalIgnoreCase)))
            {
                try { return Encoding.UTF8.GetString(models.Pak(pak).Read(models.Pak(pak).Find(member))); }
                catch (Exception e) when (e is ModelFormatException or IOException or KeyNotFoundException) { }
            }
        return null;
    }

    /// <summary>
    /// Split an edited model scene per mesh (<see cref="ModelSplit"/>, with the mesh encoder as its check); yields a
    /// mesh job for every mesh with an applied, not-unchanged submesh, or a rename — as the prototype's build does.
    /// Any problem refuses the build.
    /// </summary>
    private static List<(string, string, int, string, JsonObject?, string)> SplitScene(SceneItem scene, BuildEnv env, string outDir, List<string> warnings)
    {
        string from = $"scene/{Path.GetFileName(scene.Folder)}";
        var report = JsonNode.Parse(File.ReadAllText(scene.ReportPath))!.AsObject();
        var options = new ModelSplitOptions
        {
            Tol = (double?)scene.Options["tol"] ?? 1e-4,
            Assign = (scene.Options["assign"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value ?? ""),
            Hide = (scene.Options["hide"] as JsonArray)?.Select(x => (string)x!).ToList(),
            Lods = (bool?)scene.Options["lods"] ?? true,
            DoubleSided = (scene.Options["double_sided"] as JsonArray)?.Select(x => (string)x!).ToList(),
            Check = (parts, name, cast, sidecar) =>
            {
                var r = MeshBuild.Build(parts, name, cast, sidecar, new MeshBuildOptions { IgnoreBoneChanges = true });
                return new ModelSplitCheckResult(r.Parts, r.Warnings, []);
            },
            SkeletonByName = (name, pack) => env.Catalog.Lookup(name.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name, 0x10) is { Length: > 0 } g
                ? MeshDecoder.Decode(env.Catalog.Split(g[0]).Entry.Pack!, env.Catalog.Split(g[0]).Index) : null,
        };
        var result = ModelSplit.Split(scene.ScenePath, report, outDir, (label, index) =>
        {
            var (pack, i) = Template(env.Catalog, label, index, from);
            var parts = MeshDecoder.ReadParts(pack, i);
            return (parts.ByType, MeshDecoder.Decode(parts), pack.Name(i).Trim());
        }, options);
        var problems = result.Problems.Concat(result.Meshes.SelectMany(m => m.Problems)).ToList();
        if (problems.Count > 0) throw new ProjectException($"{Path.GetFileName(scene.ScenePath)}: {string.Join("; ", problems.Take(5))}");
        var parts = report["parts"] as JsonArray ?? [];
        var renames = scene.Options["renames"] as JsonObject;
        var jobs = new List<(string, string, int, string, JsonObject?, string)>();
        for (int k = 0; k < result.Meshes.Count && k < parts.Count; k++)
        {
            var m = result.Meshes[k];
            var part = parts[k]!.AsObject();
            string? rename = (string?)renames?[m.LogicalName];
            bool changed = m.Submeshes.Any(s => s.Status == "applied") && m.Check is not { Unchanged: true };
            if ((!changed && rename is null) || m.Dir is null) continue;
            if (m.Check is { Ok: false } bad) throw new ProjectException($"{m.LogicalName}: {bad.Error}");
            string sidecarPath = Path.Combine(m.Dir, "mesh.json");
            var sidecar = File.Exists(sidecarPath) ? JsonNode.Parse(File.ReadAllText(sidecarPath))!.AsObject() : null;
            jobs.Add((rename ?? m.LogicalName, (string?)part["pack"] ?? "", (int?)part["index"] ?? -1,
                      Path.Combine(m.Dir, "model.cast"), sidecar, from));
        }
        if (jobs.Count == 0) warnings.Add($"{from}: no mesh changed");
        return jobs;
    }

    /// <summary>Reopen what was written: every rpack part readable and every mesh decodes; every PAK model parses with one entry per slot.</summary>
    public static (string Verdict, bool Ok) Verify(string? rpackPath, string? pakPath, int expectedResources, IReadOnlyList<string> members) =>
        Verify(rpackPath is null ? [] : [(rpackPath, expectedResources)], pakPath, members);

    /// <summary><see cref="Verify(string?, string?, int, IReadOnlyList{string})"/> over several packs, each with its resource count.</summary>
    public static (string Verdict, bool Ok) Verify(IReadOnlyList<(string Path, int Expected)> rpacks, string? pakPath, IReadOnlyList<string> members)
    {
        var problems = new List<string>();
        int meshes = 0, textures = 0, models = 0, clips = 0, prefabs = 0;
        foreach (var (rpackPath, expectedResources) in rpacks)
        {
            using var pack = RpackFile.Open(rpackPath);
            if (pack.Count != expectedResources) problems.Add($"{Path.GetFileName(rpackPath)}: {pack.Count} resources, expected {expectedResources}");
            for (int i = 0; i < pack.Count; i++)
            {
                var lg = pack.Logicals[i];
                for (int k = 0; k < lg.PartCount; k++)
                    if (pack.PartUnreadableReason((int)lg.FirstPart + k) is { } why) problems.Add($"{pack.Name(i)}: {why}");
                try
                {
                    if (lg.Type == 0x10) { MeshDecoder.Decode(pack, i); meshes++; }
                    else if (lg.Type == 0x20) { Texture.TextureResource.Open(pack, i); textures++; }
                    else if (lg.Type == Anim.Anm2Resource.TypeAnimation) { Anim.Anm2Resource.Decode(pack, i); clips++; }
                    else if (lg.Type == Prefab.PrefabContainer.ResourceType)
                    {
                        var c = Prefab.PrefabContainer.Read(pack, i);
                        c.Validate();
                        Prefab.PrefabDecoder.Decode(c);
                        prefabs++;
                    }
                }
                catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or Texture.ImgcException or RpackFormatException
                                              or Anim.Anm2FormatException or Anim.Anm2UnsupportedException or Prefab.PrefabFormatException)
                {
                    problems.Add($"{pack.Name(i)}: {e.Message}");
                }
            }
        }
        if (pakPath is not null)
        {
            using var catalog = new ModelCatalog([pakPath]);
            problems.AddRange(catalog.Errors);
            foreach (var e in catalog.Models)
            {
                try
                {
                    var doc = catalog.Load(e);
                    if (doc.Slots.Any(s => s.Meshes.Count > 1)) problems.Add($"{e.Name}: a slot has more than one entry");
                    models++;
                }
                catch (ModelFormatException ex) { problems.Add($"{e.Name}: {ex.Message}"); }
            }
            if (models != members.Count(m => m.EndsWith(".model", StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{models} models in the PAK, expected {members.Count}");
        }
        bool ok = problems.Count == 0;
        string verdict = ok
            ? $"verified: {meshes} mesh(es) decode, {textures} texture(s) open, " + (clips > 0 ? $"{clips} clip(s) decode, " : "") +
              (prefabs > 0 ? $"{prefabs} Prefabs resource(s) validate and decode, " : "") + $"{models} model(s) parse"
            : $"verify found {problems.Count} problem(s): {problems[0]}";
        Log.Write(ok ? LogLevel.Info : LogLevel.Error, "build", verdict, null);
        return (verdict, ok);
    }
}
