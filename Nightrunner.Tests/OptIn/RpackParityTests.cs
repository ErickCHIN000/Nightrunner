// Opt-in (compiled only with -p:OptInTests=true; see tools/parity/README.md): byte parity of the pack writer with the
// Python prototype's PackWriter. NIGHTRUNNER_PROTOTYPE = the prototype checkout (nightrunner-main), optional
// NIGHTRUNNER_PYTHON = interpreter (default "python"). Each case is described as JSON, the prototype writes its packs from
// that description (PYTHONDONTWRITEBYTECODE=1, nothing written beside the prototype), the port writes the same cases, and
// the files are compared byte for byte; refusals must agree. The synthetic cases' port output is frozen in
// RpackLayoutTests.SyntheticCasesAreFrozen, which always runs.
using System.Diagnostics;
using System.Text.Json;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

public partial class RpackLayoutTests
{
    // ---- parity with the prototype (NIGHTRUNNER_PROTOTYPE) ------------------------------------------------

    [Fact]
    [Trait("Category", "OptIn")]
    public void PrototypeParitySynthetic()
    {
        var proto = Parity.Prototype();
        using var tmp = new TempDir();
        var cases = Parity.SyntheticCases(tmp);
        Parity.Run(proto, tmp, cases);
    }

    [Fact]
    [Trait("Category", "OptIn")]
    public void PrototypeParityShipped()
    {
        var proto = Parity.Prototype();
        var dltb = Installs.Require("dltb");
        using var tmp = new TempDir();
        var cases = Parity.ShippedCases(dltb.Assets!, "dltb");
        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_TESTS_NO_INSTALL") != "1"
            && Core.Games.GameInstall.FindInstalls().TryGetValue("dl2", out var dl2) && dl2.Assets is { } dl2Assets)
            cases.AddRange(Parity.ShippedCases(dl2Assets, "dl2"));
        Parity.Run(proto, tmp, cases);
    }

    internal static partial class Parity
    {
        public static string Prototype()
        {
            var proto = Environment.GetEnvironmentVariable("NIGHTRUNNER_PROTOTYPE");
            if (string.IsNullOrEmpty(proto)) Assert.Skip("NIGHTRUNNER_PROTOTYPE not set");
            if (!File.Exists(Path.Combine(proto, "nightrunner", "container", "rp6l.py")))
                Assert.Skip($"NIGHTRUNNER_PROTOTYPE has no nightrunner/container/rp6l.py");
            return proto;
        }

        /// <summary>Run the prototype on every case, then the port, and compare.</summary>
        public static void Run(string proto, TempDir tmp, List<PackCase> cases)
        {
            var py = Directory.CreateDirectory(tmp.File("py")).FullName;
            var cs = Directory.CreateDirectory(tmp.File("cs")).FullName;
            var casesPath = tmp.File("cases.json");
            File.WriteAllText(casesPath, JsonSerializer.Serialize(cases));
            var gen = tmp.File("gen.py");
            File.WriteAllText(gen, Generator);
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("NIGHTRUNNER_PYTHON") is { Length: > 0 } exe ? exe : "python")
            {
                WorkingDirectory = proto,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(gen);
            psi.ArgumentList.Add(casesPath);
            psi.ArgumentList.Add(py);
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            psi.Environment["PYTHONPATH"] = proto;
            using (var proc = Process.Start(psi)!)
            {
                var err = proc.StandardError.ReadToEndAsync();
                proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                Assert.True(proc.ExitCode == 0, $"prototype failed: {err.Result}");
            }

            var packs = new Dictionary<string, RpackFile>();
            var failures = new List<string>();
            int identical = 0;
            var refused = new List<string>();
            long bytes = 0;
            try
            {
                foreach (var c in cases)
                {
                    using var pyRes = JsonDocument.Parse(File.ReadAllText(Path.Combine(py, c.Name + ".json")));
                    var p = pyRes.RootElement;
                    bool pyOk = p.GetProperty("ok").GetBoolean();
                    var dst = Path.Combine(cs, c.Name + ".rpack");
                    var mine = Build(c, dst, packs);
                    if (pyOk != mine.Ok)
                    {
                        failures.Add($"{c.Name}: prototype {(pyOk ? "wrote" : "refused: " + p.GetProperty("error").GetString())}, "
                                     + $"port {(mine.Ok ? "wrote" : "refused: " + mine.Error)}");
                        continue;
                    }
                    if (!pyOk) { refused.Add(c.Name); continue; }
                    var pyWarnings = p.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
                    if (p.GetProperty("layout").GetString() != mine.Layout || !pyWarnings.SequenceEqual(mine.Warnings))
                        failures.Add($"{c.Name}: layout/warnings {p.GetProperty("layout")} [{string.Join("; ", pyWarnings)}] vs "
                                     + $"{mine.Layout} [{string.Join("; ", mine.Warnings)}]");
                    using var a = File.OpenRead(Path.Combine(py, c.Name + ".rpack"));
                    using var b = File.OpenRead(dst);
                    bytes += a.Length;
                    if (SameStream(a, b)) identical++;
                    else failures.Add($"{c.Name}: bytes differ ({a.Length:N0} vs {b.Length:N0})");
                }
            }
            finally
            {
                foreach (var pk in packs.Values) pk.Dispose();
            }
            TestContext.Current.SendDiagnosticMessage($"parity: {identical} identical ({bytes:N0} bytes), {refused.Count} refused by both ({string.Join(", ", refused)}), {failures.Count} failures");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
            Assert.Equal(cases.Count, identical + refused.Count);
        }

        /// <summary>Real resources: meshes, textures and a mix, as file slices and through FromPack, every layout.</summary>
        public static List<PackCase> ShippedCases(string assets, string game)
        {
            var cases = new List<PackCase>();
            string At(string rel) => Path.Combine(assets, rel);

            // whole small packs, from their own options, every layout (dltb only; dl2's layout differs)
            if (game == "dltb")
            {
                foreach (var rel in Rp6lReaderTests.SmallPacks.Where(r => File.Exists(At(r))))
                {
                    var src = At(rel);
                    int n;
                    long size = new FileInfo(src).Length;
                    using (var pk = RpackFile.Open(src)) n = pk.Count;
                    var whole = Enumerable.Range(0, n).Select(i => Whole(src, i)).ToList();
                    var stem = $"{game}_{Path.GetFileNameWithoutExtension(rel)}";
                    cases.Add(new($"{stem}_auto", whole, OptionsFromPack: src));
                    cases.Add(new($"{stem}_contiguous", whole, Layout: "contiguous", OptionsFromPack: src));
                    cases.Add(new($"{stem}_preserve", whole, Layout: "preserve", OptionsFromPack: src));
                    cases.Add(new($"{stem}_fill", whole, Layout: "preserve", OptionsFromPack: src, Fill: src, FinalSize: size));
                }
            }

            var meshes = new List<ResCase>();
            var textures = new List<ResCase>();
            var method0 = new List<ResCase>();
            var others = new List<ResCase>();
            var anims = new List<ResCase>();
            string? meshPack = At("common_meshes_pc.rpack");
            if (File.Exists(meshPack))
            {
                using var pk = RpackFile.Open(meshPack);
                var idx = Enumerable.Range(0, pk.Count).Where(i => pk.Logicals[i].Type == 0x10).ToList();
                for (int j = 0; j < 40 && j < idx.Count; j++) meshes.Add(Slices(pk, idx[j * idx.Count / 40]));
                cases.Add(new($"{game}_meshes_frompack", [.. idx.Take(12).Select(i => Whole(meshPack, i))], Field08: 0x1000));
            }
            var texPack = At("common_textures_0_pc.rpack");
            if (File.Exists(texPack))
            {
                using var pk = RpackFile.Open(texPack);
                for (int j = 0; j < 20; j++) textures.Add(Slices(pk, j * (pk.Count / 20)));
            }
            var engine = At("engine_pc.rpack");
            if (File.Exists(engine))
            {
                using var pk = RpackFile.Open(engine);
                method0.AddRange(Enumerable.Range(0, pk.Count).Where(i => pk.Logicals[i].Type == 0x10).Take(3).Select(i => Slices(pk, i)));
                others.AddRange(Enumerable.Range(0, pk.Count).Where(i => pk.Logicals[i].Type == 0x61).Select(i => Slices(pk, i)));
            }
            foreach (var rel in new[] { "dlc_ft_prologue/reg1_pc.rpack", "dlc_ft_prologue_envprobes_pc.rpack", "online_hub_envprobes_pc.rpack" })
            {
                if (!File.Exists(At(rel))) continue;
                using var pk = RpackFile.Open(At(rel));
                others.AddRange(Enumerable.Range(0, Math.Min(pk.Count, 4)).Select(i => Slices(pk, i)));
            }
            var animPack = At("player_anims_stream_pc.rpack");
            if (File.Exists(animPack))
            {
                using var pk = RpackFile.Open(animPack);
                anims.AddRange(Enumerable.Range(0, pk.Count).Where(i => pk.Logicals[i].Type == 0x40).Take(3).Select(i => Slices(pk, i)));
            }

            // resized: vertex grows by 48, index shrinks by 16, texture bitmap grows (a rebuilt mesh / texture)
            List<ResCase> Resized(List<ResCase> src) =>
            [
                .. src.Select((r, j) => r with
                {
                    Parts = [.. r.Parts.Select(p => p.Type switch
                    {
                        0xF0 => p with { File = null, Size = p.Size + 48, Seed = j },
                        0xF1 => p with { File = null, Size = Math.Max(0, p.Size - 16), Seed = j + 1 },
                        0x21 => p with { File = null, Size = p.Size + 4096 + j, Seed = j },
                        _ => p,
                    })],
                }),
            ];

            if (meshes.Count > 0)
            {
                cases.Add(new($"{game}_meshes_auto", meshes, Field08: 0x1000));
                cases.Add(new($"{game}_meshes_grouped", meshes, Field08: 0x1000, Layout: "grouped"));
                cases.Add(new($"{game}_meshes_f0", meshes));
                cases.Add(new($"{game}_meshes_resized", Resized(meshes), Field08: 0x1000));
            }
            if (textures.Count > 0)
            {
                cases.Add(new($"{game}_textures_auto", textures));
                cases.Add(new($"{game}_textures_f1000", textures, Field08: 0x1000));
                cases.Add(new($"{game}_textures_resized", Resized(textures)));
            }
            // a mod pack: meshes + textures + method-0 meshes + other types, field08 by the project rule
            var mix = new List<ResCase>();
            for (int j = 0; j < 10; j++)
            {
                if (j < meshes.Count) mix.Add(meshes[j]);
                if (j < textures.Count) mix.Add(textures[j]);
                if (j < method0.Count) mix.Add(method0[j]);
                if (j < others.Count) mix.Add(others[j]);
            }
            if (mix.Count > 0)
            {
                uint f08 = RpackWriter.ProjectField08(null, mix.Any(r => r.Type == 0x10), [0]);
                cases.Add(new($"{game}_mix_project", mix, Field08: f08));
                cases.Add(new($"{game}_mix_resized", Resized(mix), Field08: f08));
                cases.Add(new($"{game}_mix_grouped", mix));
            }
            if (anims.Count > 0)
            {
                cases.Add(new($"{game}_anim_stream_contiguous", [.. mix.Take(4), .. anims], Field08: 0x1000));   // refused: mixes stream
                cases.Add(new($"{game}_anim_stream_grouped", [.. mix.Take(4), .. anims]));
            }
            return cases;
        }

        /// <summary>The prototype side: reads the case list, builds each pack with PackWriter, records the outcome.</summary>
        public const string Generator = """
            import json, sys
            from pathlib import Path
            from nightrunner.container.rp6l import Pack, PackWriter, ResourceSpec, PartSpec, PartSource


            def payload(n, seed):
                return bytes(((seed * 37 + i * 7 + 1) & 0xFF) or 1 for i in range(n))


            def main(cases_path, out_dir):
                cases = json.loads(Path(cases_path).read_text(encoding="utf-8"))
                out = Path(out_dir)
                packs = {}

                def pack(p):
                    if p not in packs:
                        packs[p] = Pack.open(p)
                    return packs[p]

                for c in cases:
                    res = {"name": c["Name"]}
                    try:
                        specs = []
                        for r in c["Resources"]:
                            if r["FromPack"]:
                                specs.append(ResourceSpec.from_pack(pack(r["FromPack"]), r["Index"]))
                                continue
                            parts = []
                            for p in r["Parts"]:
                                if p["File"]:
                                    src = PartSource(path=p["File"], offset=p["Offset"], size=p["Size"])
                                else:
                                    src = PartSource(payload(p["Size"], p["Seed"]))
                                parts.append(PartSpec(type=p["Type"], source=src, align_raw=p["AlignRaw"],
                                                      storage_flags=p["StorageFlags"], storage_metadata=p["StorageMetadata"],
                                                      flag_bits=p["FlagBits"], fc=p["Fc"], offset_units=p["OffsetUnits"],
                                                      storage_index=p["StorageIndex"]))
                            specs.append(ResourceSpec(name=bytes.fromhex(r["NameHex"]), type=r["Type"], flags=r["Flags"],
                                                      parts=parts, name_index=r["NameIndex"]))
                        if c["OptionsFromPack"]:
                            w = PackWriter.from_pack(pack(c["OptionsFromPack"]), c["Layout"])
                        else:
                            order = [tuple(k) for k in c["StorageOrder"]] if c["StorageOrder"] else None
                            template = pack(c["TemplateFrom"]).storages if c["TemplateFrom"] else None
                            w = PackWriter(c["Field08"], c["Flags"], c["Layout"], storage_order=order,
                                           template_storages=template, name_blob_order=c["NameBlobOrder"])
                        for s in specs:
                            w.add(s)
                        rep = w.write(out / (c["Name"] + ".rpack"), fill=c["Fill"], final_size=c["FinalSize"])
                        res.update(ok=True, layout=rep.layout, size=rep.size, warnings=rep.warnings)
                    except Exception as e:
                        res.update(ok=False, error=type(e).__name__ + ": " + str(e))
                    (out / (c["Name"] + ".json")).write_text(json.dumps(res), encoding="utf-8")
                for p in packs.values():
                    p.close()


            if __name__ == "__main__":
                main(sys.argv[1], sys.argv[2])
            """;
    }
}
