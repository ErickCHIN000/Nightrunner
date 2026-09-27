using System.IO.Compression;
using System.Text;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.Tests;

/// <summary>
/// What Nightrunner Runtime Modding loads and in which order: NightrunnerProxy's mods folder read the way the
/// runtime reads it. Synthetic installs in a temp folder; one install-backed check resolves a model through a temp
/// runtime layout over the real stock packs.
/// </summary>
public class RuntimeContentTests
{
    // ---- leftovers of the removed first runtime ------------------------------------------------------------------

    /// <summary>
    /// The first runtime (content.ini, winmm.dll) is gone: a content.ini a user still has beside the executable is
    /// simply not read — no items, no problems — and only the mods folder counts.
    /// </summary>
    [Fact]
    public void ALeftoverContentIniIsIgnored()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        FakeInstall.Text(Path.Combine(g.Bin, "content.ini"), "[rpacks]\nold = -1\n[paks]\nold = 1\n");
        g.Mod("m", """{ "id": "m", "items": [ { "kind": "rpack", "file": "m_pc.rpack" } ] }""", "m_pc.rpack");

        var rc = RuntimeContent.Read(g.Install);
        Assert.Empty(rc.Problems);
        Assert.Equal(["m"], rc.Items.Select(i => i.Source).Distinct());
        string S(string f) => $"ph_ft/work/data_platform/pc/assets/{f}";
        Assert.Equal([S("engine_pc.rpack"), S("lang_speech_en_pc.rpack"), "ph_ft/work/bin/x64/Nightrunner/mods/m/m_pc.rpack",
                      S("common_anims_pc.rpack"), S("common_meshes_pc.rpack"), S("dlc/level_pc.rpack")], g.Install.Rpacks(rc).Select(g.Rel));
        Assert.Equal(["ph_ft/source/data0.pak", "ph_ft/source/data1.pak"], g.Install.Paks(rc).Select(g.Rel));
        Assert.Equal(1, g.Install.CustomRpackCount());

        // off: stock only, the mods folder stays out
        Assert.Equal([S("common_anims_pc.rpack"), S("common_meshes_pc.rpack"), S("dlc/level_pc.rpack"),
                      S("engine_pc.rpack"), S("lang_speech_en_pc.rpack")], g.Install.Rpacks().Select(g.Rel));
        Assert.Equal(2, g.Install.Paks().Length);
    }

    // ---- mods folder (NightrunnerProxy) ----------------------------------------------------------------------------

    [Fact]
    public void ModsPlanFollowsTheRuntime()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        FakeInstall.Text(Path.Combine(g.Bin, "Nightrunner", RuntimeContent.SettingsName), """
            { "schema": "nightrunner/settings@1",
              "mods": [ { "id": "late", "order": 5 }, { "id": "off", "enabled": false, "order": 1 }, { "id": "first", "order": 1 } ] }
            """);
        g.Mod("first", """
            { "id": "first", "items": [
              { "kind": "pak", "file": "paks/first.pak", "order": 2 },
              { "kind": "rpack", "file": "packs/anim_pc.rpack", "at": "before:common_anims_pc", "order": 0 },
              { "kind": "rpack", "file": "packs/first_pc.rpack", "at": "after-builtins", "order": 1 },
            ] }  // trailing commas and comments are accepted
            """, "paks/first.pak", "packs/anim_pc.rpack", "packs/first_pc.rpack");
        g.Mod("late", """{ "Id": "late", "Items": [ { "Kind": "RPACK", "File": "late_pc.rpack" } ] }""", "late_pc.rpack");
        g.Mod("auto", """{ "id": "auto", "items": [ { "kind": "rpack", "file": "auto_pc.rpack" } ] }""", "auto_pc.rpack");
        g.Mod("off", """{ "id": "off", "items": [ { "kind": "rpack", "file": "off_pc.rpack" } ] }""", "off_pc.rpack");
        g.Mod("broken", """{ "id": "broken", "items": [ { "kind": "rpack", "file": "ok_pc.rpack" }, { "kind": "rpack", "file": "gone_pc.rpack" } ] }""", "ok_pc.rpack");
        g.Mod("zdupe", """{ "id": "FIRST", "items": [ { "kind": "rpack", "file": "dupe_pc.rpack" } ] }""", "dupe_pc.rpack");
        g.Mod("badat", """{ "id": "badat", "items": [ { "kind": "rpack", "file": "x_pc.rpack", "at": "during:x" } ] }""", "x_pc.rpack");
        g.Mod("never", """{ "id": "never", "items": [ { "kind": "rpack", "file": "n_pc.rpack", "at": "before:no_such_pack" } ] }""", "n_pc.rpack");
        FakeInstall.Put(Path.Combine(g.Mods, "nojson", "stray_pc.rpack"));

        var rc = RuntimeContent.Read(g.Install);
        string M(string f) => $"ph_ft/work/bin/x64/Nightrunner/mods/{f}";
        string S(string f) => $"ph_ft/work/data_platform/pc/assets/{f}";
        Assert.Equal([
            S("engine_pc.rpack"), S("lang_speech_en_pc.rpack"),
            M("first/packs/first_pc.rpack"), M("late/late_pc.rpack"), M("auto/auto_pc.rpack"),   // (order, id); unlisted mods last
            M("first/packs/anim_pc.rpack"),                                                         // before:common_anims_pc comes later
            S("common_anims_pc.rpack"), S("common_meshes_pc.rpack"), S("dlc/level_pc.rpack"),
        ], g.Install.Rpacks(rc).Select(g.Rel));
        Assert.Equal(["ph_ft/source/data0.pak", "ph_ft/source/data1.pak", M("first/paks/first.pak")], g.Install.Paks(rc).Select(g.Rel));
        Assert.Contains(rc.Problems, p => p.Contains("mods/broken: file 'gone_pc.rpack' does not exist"));
        Assert.Contains(rc.Problems, p => p.Contains("mods/zdupe: id 'FIRST' is taken"));
        Assert.Contains(rc.Problems, p => p.Contains("mods/badat: at 'during:x'"));
        Assert.Contains(rc.Problems, p => p.Contains("before:no_such_pack never comes"));
        Assert.Contains(rc.Items, i => i.Source == "off" && !i.Enabled && i.Problem == "disabled in nightrunner.json");
        Assert.Contains(g.Mods, rc.WatchFolders.Select(f => Path.Combine(f, "mods")));
    }

    // ---- installed game: the modded model resolves to the mod's meshes and textures ---------------------

    /// <summary>
    /// A runtime layout in a temp folder over the real stock packs (the profile points its assets and source
    /// folders at the install; nothing is written there): a mod pack holding copies of every mesh
    /// <c>player_tpp_skeleton.model</c> draws plus one texture, and a mod pak holding the model. With runtime
    /// modding on, every drawn mesh and the texture resolve to the mod pack, and the model to the mod pak; off,
    /// everything is stock again.
    /// </summary>
    [Fact]
    public async Task ModdedModelDrawsTheModsMeshesAndTextures()
    {
        var install = Installs.Require("dltb");
        var ct = TestContext.Current.CancellationToken;
        using var tmp = new TempDir();
        using var stock = new RpackCatalog();
        await stock.LoadAsync(install.Rpacks(), install.Assets, ct);
        using var stockModels = new ModelCatalog(install.Paks());
        using var sdb = SdbFile.Open(install.Sdb("dx11"));
        var entry = stockModels.Find("player_tpp_skeleton.model")!;
        var doc = stockModels.Load(entry);
        MeshModel Decode(RpackCatalog c, int gid) { var (e, i) = c.Split(gid); return MeshDecoder.Decode(e.Pack!, i); }
        var res = ModelResolver.Resolve(doc, stock, sdb, g => Decode(stock, g));
        var drawn = res.Slots.SelectMany(s => s.Meshes.Where(m => m.Drawn && m.Found)).ToList();
        Assert.NotEmpty(drawn);
        var texture = drawn.SelectMany(m => m.Submeshes).SelectMany(s => s.Textures).First(t => t.Gids.Length > 0 || stock.Lookup(t.Texture, 0x20).Length > 0);
        int texGid = stock.Lookup(texture.Texture, 0x20)[0];
        var specs = drawn.Select(m => m.Gids[0]).Append(texGid).Distinct()
            .Select(gid => { var (e, i) = stock.Split(gid); return ResourceSpec.FromPack(e.Pack!, i); }).ToList();
        byte[] model = stockModels.Pak(entry.Pak).Read(entry.Member);

        void Pak(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var s = zip.CreateEntry(entry.Basename).Open();
            s.Write(model);
        }

        {
            string root = tmp.File("mods");
            string bin = Path.Combine(root, "bin");
            var profile = GameProfile.Dltb with
            {
                ExeTemplates = [Path.Combine(bin, FakeInstall.Exe)],
                Folders = new Dictionary<GameFolder, string>
                {
                    [GameFolder.Assets] = install.Assets!, [GameFolder.Audio] = install.Audio!, [GameFolder.Data] = install.Source!,
                },
            };
            FakeInstall.Put(Path.Combine(bin, FakeInstall.Exe));
            string pack = Path.Combine(bin, "Nightrunner", "mods", "b14", "packs", "b14_pc.rpack");
            string pak = Path.Combine(bin, "Nightrunner", "mods", "b14", "paks", "b14.pak");
            FakeInstall.Text(Path.Combine(bin, "Nightrunner", "mods", "b14", "mod.json"),
                             """{ "id": "b14", "items": [ { "kind": "rpack", "file": "packs/b14_pc.rpack" }, { "kind": "pak", "file": "paks/b14.pak" } ] }""");
            RpackWriter.Write(FakeInstall.Put(pack), specs);
            Pak(pak);

            var gi = new GameInstall(root, profile);
            var origins = new PackOrigins(gi);
            foreach (bool on in new[] { true, false })
            {
                var rc = on ? RuntimeContent.Read(gi) : null;
                using var catalog = new RpackCatalog();
                await catalog.LoadAsync(gi.Rpacks(rc), gi.Assets, ct);
                using var models = new ModelCatalog(gi.Paks(rc), p => !origins.IsStock(p));
                var picked = models.Find(entry.Basename)!;
                Assert.Equal(on, picked.Custom);
                Assert.Equal(on ? pak : entry.Pak, picked.Pak, ignoreCase: true);
                var shown = ModelResolver.Resolve(models.Load(picked), catalog, sdb, g => Decode(catalog, g));
                foreach (var m in shown.Slots.SelectMany(s => s.Meshes.Where(m => m.Drawn && m.Found)))
                    Assert.Equal(on, catalog.Split(m.Gids[0]).Entry.Path.Equals(pack, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(on, catalog.Split(catalog.Lookup(texture.Texture, 0x20)[0]).Entry.Path.Equals(pack, StringComparison.OrdinalIgnoreCase));
                if (on)   // and "Add to project" still starts from the stock copies
                    foreach (var m in shown.Slots.SelectMany(s => s.Meshes.Where(m => m.Drawn && m.Found)))
                        Assert.True(origins.IsStock(catalog.Split(StockCopy.Resource(catalog, m.Gids[0], origins)).Entry.Path));
            }
        }
    }
}

/// <summary>A DLTB-shaped root: bin with a placeholder exe, stock packs (empty files unless given) and dataN.paks.</summary>
internal sealed class FakeInstall
{
    public const string Exe = "DyingLightGame_TheBeast_x64_rwdi.exe";

    public readonly string Root;
    public readonly GameInstall Install;

    public FakeInstall(TempDir tmp, params string[] stockPacks)
    {
        Root = tmp.File("game");
        Directory.CreateDirectory(Bin);
        File.WriteAllBytes(Path.Combine(Bin, Exe), []);
        foreach (var p in stockPacks.Length > 0 ? stockPacks
                     : ["engine_pc.rpack", "lang_speech_en_pc.rpack", "common_meshes_pc.rpack", "common_anims_pc.rpack", "dlc/level_pc.rpack"])
            Put(Path.Combine(Assets, p));
        foreach (var p in new[] { "data0.pak", "data1.pak" }) Put(Path.Combine(Source, p));
        Install = new GameInstall(Root, GameProfile.Dltb);
    }

    public string Bin => Path.Combine(Root, "ph_ft", "work", "bin", "x64");
    public string Assets => Path.Combine(Root, "ph_ft", "work", "data_platform", "pc", "assets");
    public string Source => Path.Combine(Root, "ph_ft", "source");
    public string Mods => Path.Combine(Bin, "Nightrunner", "mods");

    public string Rel(string p) => Path.GetRelativePath(Root, p).Replace('\\', '/');

    public static string Put(string path, byte[]? data = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data ?? []);
        return path;
    }

    public static void Text(string path, string text, bool bom = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\r\n"), new UTF8Encoding(bom));
    }

    public string Settings => Path.Combine(Bin, "Nightrunner", RuntimeContent.SettingsName);

    /// <summary>
    /// NightrunnerProxy installed: a <c>dxgi.dll</c> carrying the UTF-16 <c>Nightrunner\Core.dll</c> it boots, and the
    /// files it needs in <c>Nightrunner\</c> — Core.dll, Core.runtimeconfig.json, Nightrunner.Api.dll, DLTB.dll (empty placeholders).
    /// </summary>
    public FakeInstall Proxy()
    {
        Put(Path.Combine(Bin, "dxgi.dll"), [.. new byte[16], .. Encoding.Unicode.GetBytes(RuntimeModding.ProxyMarker), .. new byte[16]]);
        foreach (var f in new[] { "Core.dll", "Core.runtimeconfig.json", "Nightrunner.Api.dll", "DLTB.dll" })
            Put(Path.Combine(Bin, "Nightrunner", f));
        return this;
    }

    /// <summary>A NightrunnerProxy mod folder: its mod.json and (empty) files.</summary>
    public void Mod(string folder, string json, params string[] files)
    {
        foreach (var f in files) Put(Path.Combine(Mods, folder, f));
        Text(Path.Combine(Mods, folder, RuntimeContent.ManifestName), json);
    }
}
