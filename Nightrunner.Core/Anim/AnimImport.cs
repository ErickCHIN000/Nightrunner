using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using CastAnimation = Nightrunner.Core.Cast.Animation;
using File = System.IO.File;

namespace Nightrunner.Core.Anim;

/// <summary>What an import made of a scene: the clip, and what it had to fill in or leave out.</summary>
public sealed record AnimImportResult(Anm2Clip Clip, IReadOnlyList<string> Notes, int Keys, double Fps);

/// <summary>
/// An animation scene back to a clip: a Cast animation (<c>rq</c>, <c>t*</c>, <c>s*</c> curves per bone, the form
/// <see cref="AnimCast"/> writes) or a glTF animation (rotation / translation / scale channels on named nodes, the
/// form the .glb export writes and Blender re-exports). Values are parent-local TRS in engine space (Y up, metres),
/// the same space the export wrote, so an unedited round trip gives the clip back to its quantisation.
/// </summary>
/// <remarks>
/// Tracks are named bones (bound by <c>h41</c>, as the engine binds them) or <c>0x%08X</c> hashes (a track the export
/// could not name). Keys are resampled to whole frames at <c>fps</c>: frame <c>f</c> is time <c>f / fps</c>, rotations
/// nlerp with a sign fix, translation and scale lerp, the value held past the last key. A track missing translation or
/// scale takes that channel from the template clip's same track at frame 0 (the shipped constant), else it is refused;
/// template tracks the scene lacks are dropped and listed. Only <c>absolute</c> curves are read.
/// </remarks>
public static class AnimImport
{
    private sealed class Track
    {
        public (double T, Quaternion Q)[]? Rotation;
        public (double T, Vector3 V)[]? Translation;
        public (double T, Vector3 V)[]? Scale;
    }

    public static AnimImportResult Import(string path, double fps, Anm2Clip? template = null)
    {
        if (!(fps > 0)) throw new AnimBankFormatException($"fps {fps} is not positive");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var tracks = ext switch
        {
            ".cast" => FromCast(path),
            ".glb" or ".gltf" => FromGltf(path),
            _ => throw new AnimBankFormatException($"{Path.GetFileName(path)}: not a .cast, .glb or .gltf"),
        };
        if (tracks.Count == 0) throw new AnimBankFormatException($"{Path.GetFileName(path)}: no animation curves on named nodes");
        var notes = new List<string>();

        double end = tracks.Values.SelectMany(t => Times(t)).DefaultIfEmpty(0).Max();
        int keys = Math.Max(1, (int)Math.Round(end * fps) + 1);
        var hashes = new List<uint>();
        var names = new List<string>();
        foreach (var name in tracks.Keys)
        {
            uint h = HashOf(name);
            if (hashes.Contains(h)) { notes.Add($"{name}: a second track with the same hash, left out"); continue; }
            hashes.Add(h);
            names.Add(name);
        }
        int T = hashes.Count;
        var rot = new Quaternion[keys * T];
        var tra = new Vector3[keys * T];
        var sca = new Vector3[keys * T];
        for (int t = 0; t < T; t++)
        {
            var track = tracks[names[t]];
            int src = template is null ? -1 : Array.IndexOf(template.TrackHashes, hashes[t]);
            for (int k = 0; k < keys; k++)
            {
                double time = k / fps;
                rot[k * T + t] = track.Rotation is { Length: > 0 } r ? SampleQ(r, time)
                    : src >= 0 ? template!.Rotation(0, src) : Quaternion.Identity;
                tra[k * T + t] = track.Translation is { Length: > 0 } tr ? SampleV(tr, time)
                    : src >= 0 ? template!.Translation(0, src) : throw new AnimBankFormatException($"{names[t]}: no translation and no template track to take it from");
                sca[k * T + t] = track.Scale is { Length: > 0 } s ? SampleV(s, time)
                    : src >= 0 ? template!.Scale(0, src) : Vector3.One;
            }
            if (track.Rotation is null) notes.Add($"{names[t]}: no rotation, {(src >= 0 ? "template frame 0" : "identity")} used");
            if (track.Translation is null) notes.Add($"{names[t]}: no translation, template frame 0 used");
            if (track.Scale is null && src >= 0) notes.Add($"{names[t]}: no scale, template frame 0 used");
        }
        if (template is not null)
        {
            var dropped = template.TrackHashes.Where(h => !hashes.Contains(h)).ToList();
            if (dropped.Count > 0) notes.Add($"{dropped.Count} template track(s) not in the scene, left out: {string.Join(", ", dropped.Take(8).Select(h => $"0x{h:X8}"))}{(dropped.Count > 8 ? ", ..." : "")}");
            int added = hashes.Count(h => !template.TrackHashes.Contains(h));
            if (added > 0) notes.Add($"{added} track(s) the template does not have");
            if (template.KeyCount != keys) notes.Add($"{keys} keys, the template has {template.KeyCount}: keep the SeqTrack ranges consistent");
        }
        var clip = Anm2Encoder.FromTrs([.. hashes], keys, rot, tra, sca);
        return new AnimImportResult(clip, notes, keys, fps);
    }

    /// <summary>A track name's hash: <c>0x%08X</c> as written for unnamed tracks, else <c>h41</c> of the name.</summary>
    public static uint HashOf(string name) =>
        name.Length == 10 && name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint h) ? h : Anm2Hash.H41(name);

    private static IEnumerable<double> Times(Track t) =>
        (t.Rotation?.Select(x => x.T) ?? []).Concat(t.Translation?.Select(x => x.T) ?? []).Concat(t.Scale?.Select(x => x.T) ?? []);

    // ---- Cast ------------------------------------------------------------------------------------------------------

    private static Dictionary<string, Track> FromCast(string path)
    {
        var root = CastFile.Load(path).Roots().FirstOrDefault() ?? throw new AnimBankFormatException($"{Path.GetFileName(path)}: no root node");
        var anim = root.ChildrenOfType<CastAnimation>().FirstOrDefault() ?? throw new AnimBankFormatException($"{Path.GetFileName(path)}: no animation");
        if (anim.CurveModeOverrides().Count > 0) throw new AnimBankFormatException($"{Path.GetFileName(path)}: curve mode overrides are not read (absolute curves only)");
        double fr = anim.Framerate() is float f && f > 0 ? f : 30;
        var tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var group in anim.Curves().GroupBy(c => c.NodeName() ?? ""))
        {
            if (group.Key.Length == 0) continue;
            var byProp = new Dictionary<string, Curve>(StringComparer.Ordinal);
            foreach (var c in group)
            {
                string mode = c.Property("m")?.StringValue ?? "absolute";
                if (mode != "absolute") throw new AnimBankFormatException($"{group.Key}.{c.KeyPropertyName()}: curve mode '{mode}' (absolute only)");
                byProp[c.KeyPropertyName() ?? ""] = c;
            }
            var track = new Track();
            if (byProp.TryGetValue("rq", out var rq))
            {
                var (k, v) = Keys(rq, 4, group.Key);
                track.Rotation = k.Select((f0, i) => (f0 / fr, Quaternion.Normalize(new Quaternion(v[i * 4], v[i * 4 + 1], v[i * 4 + 2], v[i * 4 + 3])))).ToArray();
            }
            track.Translation = Vec3(byProp, "t", fr, group.Key);
            track.Scale = Vec3(byProp, "s", fr, group.Key);
            if (track.Rotation is not null || track.Translation is not null || track.Scale is not null) tracks[group.Key] = track;
        }
        return tracks;
    }

    private static (uint[] Keys, float[] Values) Keys(Curve c, int width, string node)
    {
        var k = c.KeyFrameBuffer() ?? [];
        if (c.KeyValueBuffer() is not float[] v || v.Length != k.Length * width)
            throw new AnimBankFormatException($"{node}.{c.KeyPropertyName()}: {k.Length} keys but the values are not {width} floats per key");
        return (k, v);
    }

    private static (double, Vector3)[]? Vec3(Dictionary<string, Curve> byProp, string prefix, double fr, string node)
    {
        var axes = "xyz".Select(a => byProp.GetValueOrDefault(prefix + a)).ToArray();
        if (axes.All(a => a is null)) return null;
        if (axes.Any(a => a is null)) throw new AnimBankFormatException($"{node}: {prefix}x/{prefix}y/{prefix}z curves are not all present");
        // axes may be keyed apart: sample each at the union of their key times
        var parts = axes.Select(a => Keys(a!, 1, node)).ToArray();
        var times = parts.SelectMany(p => p.Keys).Distinct().Order().ToArray();
        return times.Select(f0 => (f0 / fr, new Vector3(Lerp1(parts[0], f0), Lerp1(parts[1], f0), Lerp1(parts[2], f0)))).ToArray();
    }

    private static float Lerp1((uint[] Keys, float[] Values) p, uint frame)
    {
        int i = Array.BinarySearch(p.Keys, frame);
        if (i >= 0) return p.Values[i];
        i = ~i;
        if (i == 0) return p.Values[0];
        if (i >= p.Keys.Length) return p.Values[^1];
        float a = (float)(frame - p.Keys[i - 1]) / (p.Keys[i] - p.Keys[i - 1]);
        return p.Values[i - 1] + (p.Values[i] - p.Values[i - 1]) * a;
    }

    // ---- glTF ------------------------------------------------------------------------------------------------------

    private static Dictionary<string, Track> FromGltf(string path)
    {
        var (doc, bin) = LoadGltf(path);
        var anims = doc["animations"] as JsonArray;
        if (anims is null || anims.Count == 0) throw new AnimBankFormatException($"{Path.GetFileName(path)}: no animations");
        if (anims.Count > 1) throw new AnimBankFormatException($"{Path.GetFileName(path)}: {anims.Count} animations; keep one per file");
        var nodes = doc["nodes"] as JsonArray ?? [];
        var anim = anims[0]!;
        var samplers = anim["samplers"] as JsonArray ?? [];
        var tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var ch in anim["channels"] as JsonArray ?? [])
        {
            int node = (int?)ch!["target"]?["node"] ?? -1;
            string? name = node >= 0 && node < nodes.Count ? (string?)nodes[node]!["name"] : null;
            string pathKind = (string?)ch["target"]?["path"] ?? "";
            if (name is null || pathKind == "weights") continue;
            var s = samplers[(int)ch["sampler"]!]!;
            string interp = (string?)s["interpolation"] ?? "LINEAR";
            if (interp == "CUBICSPLINE") throw new AnimBankFormatException($"{name}.{pathKind}: CUBICSPLINE keys are not read (bake to LINEAR)");
            var t = Accessor(doc, bin, (int)s["input"]!, 1);
            int width = pathKind == "rotation" ? 4 : 3;
            var v = Accessor(doc, bin, (int)s["output"]!, width);
            if (v.Length != t.Length * width) throw new AnimBankFormatException($"{name}.{pathKind}: {t.Length} times, {v.Length / width} values");
            if (!tracks.TryGetValue(name, out var track)) tracks[name] = track = new Track();
            // STEP keys hold until the next: two samples per key reproduce that under linear sampling
            IEnumerable<int> order = Enumerable.Range(0, t.Length);
            switch (pathKind)
            {
                case "rotation":
                    track.Rotation = Step(order.Select(i => ((double)t[i], Quaternion.Normalize(new Quaternion(v[i * 4], v[i * 4 + 1], v[i * 4 + 2], v[i * 4 + 3])))).ToArray(), interp);
                    break;
                case "translation":
                    track.Translation = Step(order.Select(i => ((double)t[i], new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]))).ToArray(), interp);
                    break;
                case "scale":
                    track.Scale = Step(order.Select(i => ((double)t[i], new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]))).ToArray(), interp);
                    break;
            }
        }
        // the armature helper the export adds is not a bone
        foreach (var k in tracks.Keys.Where(k => k.EndsWith("_armature", StringComparison.Ordinal)).ToList()) tracks.Remove(k);
        return tracks;
    }

    private static (double, T)[] Step<T>((double T, T V)[] keys, string interp)
    {
        if (interp != "STEP") return keys;
        var r = new List<(double, T)>();
        for (int i = 0; i < keys.Length; i++)
        {
            r.Add(keys[i]);
            if (i + 1 < keys.Length) r.Add((Math.BitDecrement(keys[i + 1].T), keys[i].V));
        }
        return [.. r];
    }

    private static (JsonNode Doc, byte[] Bin) LoadGltf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Gltf.GlbMagic)
        {
            int pos = 12;
            JsonNode? doc = null;
            byte[] bin = [];
            while (pos + 8 <= bytes.Length)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4));
                if (len < 0 || pos + 8 + len > bytes.Length) throw new AnimBankFormatException($"{Path.GetFileName(path)}: GLB chunk runs past the file");
                if (type == Gltf.ChunkJson) doc = JsonNode.Parse(bytes.AsSpan(pos + 8, len));
                else if (type == Gltf.ChunkBin) bin = bytes.AsSpan(pos + 8, len).ToArray();
                pos += 8 + len;
            }
            return (doc ?? throw new AnimBankFormatException($"{Path.GetFileName(path)}: GLB without JSON"), bin);
        }
        var d = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
        string? uri = (string?)d["buffers"]?[0]?["uri"];
        if (uri is null) return (d, []);
        if (uri.StartsWith("data:", StringComparison.Ordinal))
            return (d, Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]));
        return (d, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path) ?? "", Uri.UnescapeDataString(uri))));
    }

    /// <summary>A float accessor (normalised integer outputs are refused by name) as a flat array.</summary>
    private static float[] Accessor(JsonNode doc, byte[] bin, int index, int width)
    {
        var a = doc["accessors"]![index]!;
        int count = (int)a["count"]!;
        if ((int)a["componentType"]! != Gltf.Float) throw new AnimBankFormatException($"accessor {index}: component type {(int)a["componentType"]!} (float only)");
        if (a["sparse"] is not null) throw new AnimBankFormatException($"accessor {index}: sparse accessors are not read");
        var view = doc["bufferViews"]![(int)a["bufferView"]!]!;
        int offset = ((int?)view["byteOffset"] ?? 0) + ((int?)a["byteOffset"] ?? 0);
        int stride = (int?)view["byteStride"] ?? width * 4;
        var r = new float[count * width];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < width; c++)
                r[i * width + c] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(offset + i * stride + c * 4));
        return r;
    }

    // ---- sampling --------------------------------------------------------------------------------------------------

    private static Quaternion SampleQ((double T, Quaternion Q)[] k, double t)
    {
        int i = Find(k.Length, j => k[j].T, t, out double a);
        if (a <= 0 || i + 1 >= k.Length) return k[i].Q;
        var q0 = k[i].Q;
        var q1 = k[i + 1].Q;
        if (Quaternion.Dot(q0, q1) < 0) q1 = -q1;
        return Quaternion.Normalize(Quaternion.Lerp(q0, q1, (float)a));
    }

    private static Vector3 SampleV((double T, Vector3 V)[] k, double t)
    {
        int i = Find(k.Length, j => k[j].T, t, out double a);
        if (a <= 0 || i + 1 >= k.Length) return k[i].V;
        return Vector3.Lerp(k[i].V, k[i + 1].V, (float)a);
    }

    /// <summary>The key at or before <paramref name="t"/> and the fraction toward the next (keys sorted by time).</summary>
    private static int Find(int n, Func<int, double> time, double t, out double a)
    {
        a = 0;
        if (t <= time(0)) return 0;
        if (t >= time(n - 1)) return n - 1;
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (time(mid) <= t) lo = mid; else hi = mid;
        }
        double span = time(hi) - time(lo);
        a = span > 0 ? (t - time(lo)) / span : 0;
        return lo;
    }
}
