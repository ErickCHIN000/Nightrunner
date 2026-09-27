using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Nightrunner.Core.Cast;

/// <summary>A glTF / GLB file that cannot be read or a scene that cannot be written (the prototype's <c>GltfError</c>).</summary>
public sealed class GltfException(string message) : Exception(message);

/// <summary>
/// glTF 2.0 (.gltf / .glb) ↔ the in-memory Cast scene (port of <c>cast/gltf.py</c>). <see cref="Save"/> writes Cast, glTF or
/// GLB by extension; <see cref="Load"/> returns a <see cref="CastFile"/> for any of the three.
/// </summary>
/// <remarks>
/// Cast → glTF: numbers unchanged (engine space, Y up, no unit or handedness change). Skeleton → one node per bone
/// (local TRS from <c>lp</c>/<c>lr</c>, rotation normalised and rounded to 9 decimals, scale 1) under one non-joint
/// <c>&lt;model&gt;_armature</c> node, one skin whose inverse bind matrices invert the rest globals composed from those
/// TRS. Mesh → node + mesh with one triangle primitive: POSITION (min/max), NORMAL / TANGENT (rows already unit to 1e-5
/// kept bit-exact, others normalised; TANGENT.w = sign of <c>bp_tangent_sign</c>), TEXCOORD_n (inf/NaN written as 0,
/// the exact values in <c>extras.bp_nonfinite_uv</c>), JOINTS_0/WEIGHTS_0 (4 lanes: top 4 by weight, rows not summing
/// to 1 renormalised; u8 joints up to 256 bones, else u16), <c>_BP_VERTEX_ID</c> (float), u32 indices; other
/// <c>bp_*</c> → <c>extras</c>. Material → PBR (albedo/diffuse → baseColorTexture, normal → normalTexture, metallic 0,
/// roughness 0.75, double-sided; <c>bp_alpha_mode</c> mask → MASK 0.5, blend → BLEND). Images: .gltf references them by
/// relative, percent-encoded URI; .glb embeds the files that exist and drops the others.
/// <para/>
/// glTF → Cast: the first skin gives the skeleton (joint order, parents through non-joint helpers, a root joint under a
/// helper takes its world matrix); every node with a mesh gives one Cast mesh per triangle primitive (<c>#n</c> for the
/// extra ones); unskinned meshes are moved by their node's world matrix, skinned ones read as stored; <c>extras</c>
/// and <c>_BP_VERTEX_ID</c> come back as <c>bp_*</c>; TANGENT.w → <c>bp_tangent_sign</c>; JOINTS_n/WEIGHTS_n sets are
/// concatenated.
/// <para/>
/// The JSON is <c>json.dumps</c>' (see <see cref="GltfJson"/>): compact in a .glb, <c>indent=1</c> in a .gltf with the
/// platform line ending (Python's text mode writes <c>os.linesep</c>). Where the prototype would crash (a reshape, an
/// index, a value <c>struct.pack</c> cannot store) this refuses with <see cref="GltfException"/>.
/// </remarks>
public static class Gltf
{
    public const uint GlbMagic = 0x46546C67;   // 'glTF'
    public const uint ChunkJson = 0x4E4F534A;  // 'JSON'
    public const uint ChunkBin = 0x004E4942;   // 'BIN\0'
    public const int Float = 5126, UInt = 5125, UShort = 5123, UByte = 5121;
    public const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;
    public static readonly IReadOnlyList<string> Extensions = [".cast", ".gltf", ".glb"];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // ---- dispatch -----------------------------------------------------------------------------------------------

    /// <summary><c>load_scene</c>: .gltf / .glb through <see cref="ReadGltf"/>, anything else as a Cast file.</summary>
    public static CastFile Load(string path)
    {
        string ext = Suffix(path).ToLowerInvariant();
        return ext is ".gltf" or ".glb" ? ReadGltf(path) : CastFile.Load(path);
    }

    /// <summary><c>save_scene</c>: Cast / glTF / GLB by extension. Material file paths are relative to
    /// <paramref name="baseDir"/> (default: the output folder).</summary>
    public static void Save(CastFile cast, string path, string? baseDir = null)
    {
        string ext = Suffix(path).ToLowerInvariant();
        if (ext == ".cast") cast.Save(path);
        else if (ext is ".gltf" or ".glb") WriteGltf(cast, path, baseDir);
        else throw new GltfException($"unknown scene extension '{ext}' (use {string.Join(", ", Extensions)})");
    }

    // ---- writer -------------------------------------------------------------------------------------------------

    private sealed class Buf
    {
        public readonly MemoryStream Data = new();
        public readonly List<object?> Views = [];
        public readonly List<object?> Accessors = [];

        public int View(ReadOnlySpan<byte> raw, int? target = null)
        {
            while (Data.Length % 4 != 0) Data.WriteByte(0);
            var v = Obj(("buffer", 0), ("byteOffset", Data.Length), ("byteLength", raw.Length));
            if (target is { } t) v["target"] = t;
            Data.Write(raw);
            Views.Add(v);
            return Views.Count - 1;
        }

        public int Accessor(ReadOnlySpan<byte> raw, int count, int ctype, string kind, int? target,
                            double[]? min = null, double[]? max = null)
        {
            var a = Obj(("bufferView", View(raw, target)), ("componentType", ctype), ("count", count), ("type", kind));
            if (min is not null && count > 0)
            {
                a["min"] = min.Cast<object?>().ToList();
                a["max"] = max!.Cast<object?>().ToList();
            }
            Accessors.Add(a);
            return Accessors.Count - 1;
        }

        public int Floats(float[] values, int count, string kind, int? target, bool minmax = false)
        {
            double[]? min = null, max = null;
            if (minmax) (min, max) = MinMax(values, count, values.Length / Math.Max(count, 1));
            return Accessor(Bytes(values), count, Float, kind, target, min, max);
        }
    }

    /// <summary><c>cast_to_gltf</c>: writes <paramref name="path"/> as .glb (binary) or .gltf + .bin.</summary>
    public static void WriteGltf(CastFile cast, string path, string? baseDir = null)
    {
        bool binary = Suffix(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        string outDir = Parent(path);
        string baseFolder = string.IsNullOrEmpty(baseDir) ? outDir : baseDir;
        if (cast.RootNodes.Count == 0) throw new GltfException("the scene has no root node");
        var root = cast.RootNodes[0];
        var mdl = root.ChildOfType<Model>() ?? throw new GltfException("the scene has no Model");
        string modelName = Or(mdl.Name(), "model");
        var buf = new Buf();
        var nodes = new List<object?>();
        var top = new List<object?>();
        var doc = Obj(("asset", Obj(("version", "2.0"), ("generator", Generator(root)))),
                      ("scene", 0), ("scenes", new List<object?> { Obj(("name", modelName), ("nodes", top)) }));

        // skeleton
        var bones = mdl.Skeleton()?.Bones() ?? [];
        if (bones.Count > 0)
        {
            int nb = bones.Count;
            var joints = new List<object?>();
            var jointNode = new int[nb];
            var trans = new double[nb][];
            var rots = new double[nb][];
            for (int i = 0; i < nb; i++)
            {
                var b = bones[i];
                var lp = b.LocalPosition() is { Length: > 0 } p ? Array.ConvertAll(p, x => (double)Q(x)) : [0.0, 0.0, 0.0];
                var lr = b.LocalRotation() is { Length: > 0 } r ? Array.ConvertAll(r, x => (double)Q(x)) : [0.0, 0.0, 0.0, 1.0];
                if (lp.Length != 3) throw new GltfException($"bone {i}: local position has {lp.Length} values, not 3");
                if (lr.Length != 4) throw new GltfException($"bone {i}: local rotation has {lr.Length} values, not 4");
                double norm = Math.Max(Math.Sqrt(lr[0] * lr[0] + lr[1] * lr[1] + lr[2] * lr[2] + lr[3] * lr[3]), 1e-12);
                var q = new double[4];
                for (int c = 0; c < 4; c++) q[c] = Math.Clamp(PyRound(lr[c] / norm, 9), -1.0, 1.0);
                trans[i] = lp;
                rots[i] = q;
                var node = Obj(("name", Or(b.Name(), $"bone_{i}")), ("translation", Boxed(lp)), ("rotation", Boxed(q)));
                var ex = Props(b);
                if (ex.Count > 0) node["extras"] = ex;
                nodes.Add(node);
                jointNode[i] = nodes.Count - 1;
                joints.Add(nodes.Count - 1);
            }
            var parents = bones.Select(b => b.ParentIndex()).ToArray();
            // one non-joint root node holds every root bone: glTF skins need a common root
            var armChildren = new List<object?>();
            nodes.Add(Obj(("name", modelName + "_armature"), ("children", armChildren)));
            int arm = nodes.Count - 1;
            top.Add(arm);
            for (int i = 0; i < nb; i++)
            {
                int p = parents[i];
                if (p >= 0 && p < nb)
                {
                    var pn = (OrderedDictionary<string, object?>)nodes[jointNode[p]]!;
                    if (!pn.TryGetValue("children", out var ch)) pn["children"] = ch = new List<object?>();
                    ((List<object?>)ch!).Add(jointNode[i]);
                }
                else armChildren.Add(jointNode[i]);
            }
            var glob = new double[nb][,];
            var ibm = new float[nb * 16];
            for (int i = 0; i < nb; i++)
            {
                var inv = Inverse(World(i, parents, trans, rots, glob));
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++) ibm[i * 16 + r * 4 + c] = (float)inv[c, r];
            }
            doc["skins"] = new List<object?>
            {
                Obj(("name", modelName + "_skin"), ("joints", joints),
                    ("inverseBindMatrices", buf.Floats(ibm, nb, "MAT4", null)), ("skeleton", arm)),
            };
        }

        // materials + images
        var images = new List<object?>();
        var imageIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var textures = new List<object?>();
        var materials = new List<object?>();
        var matIndex = new Dictionary<ulong, int>();

        int? Texture(CastNode? fileNode, string what)
        {
            if (fileNode is null) return null;
            if (fileNode is not File file) throw new GltfException($"{what} is a {fileNode.GetType().Name} node, not a File");
            string? rel = file.Path();
            if (string.IsNullOrEmpty(rel)) return null;
            if (!imageIndex.TryGetValue(rel, out int index))
            {
                string src = System.IO.Path.Combine(baseFolder, rel);
                var img = Obj(("name", Stem(rel)));
                if (binary)
                {
                    if (!System.IO.File.Exists(src)) return null;
                    img["bufferView"] = buf.View(System.IO.File.ReadAllBytes(src));
                    img["mimeType"] = Suffix(src).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                }
                else img["uri"] = Quote(RelativeUri(src, outDir));
                images.Add(img);
                textures.Add(Obj(("source", images.Count - 1), ("sampler", 0)));
                imageIndex[rel] = index = textures.Count - 1;
            }
            return index;
        }

        foreach (var m in mdl.Materials())
        {
            var slots = m.Slots();
            string matName = Or(m.Name(), "material");
            var pbr = Obj(("metallicFactor", 0.0), ("roughnessFactor", 0.75));
            var g = Obj(("name", matName), ("doubleSided", true), ("pbrMetallicRoughness", pbr));
            var albedo = slots.GetValueOrDefault("albedo") ?? slots.GetValueOrDefault("diffuse");
            if (Texture(albedo, $"material '{matName}' albedo") is { } t) pbr["baseColorTexture"] = Obj(("index", t));
            if (Texture(slots.GetValueOrDefault("normal"), $"material '{matName}' normal") is { } tn) g["normalTexture"] = Obj(("index", tn));
            var ex = Props(m);
            var mode = ex.GetValueOrDefault("bp_alpha_mode") as string;
            if (mode == "mask") { g["alphaMode"] = "MASK"; g["alphaCutoff"] = 0.5; }
            else if (mode == "blend") g["alphaMode"] = "BLEND";
            if (ex.Count > 0) g["extras"] = ex;
            materials.Add(g);
            matIndex[m.Hash] = materials.Count - 1;
        }

        // meshes
        var meshes = new List<object?>();
        var meshList = mdl.Meshes();
        for (int k = 0; k < meshList.Count; k++)
        {
            var m = meshList[k];
            string name = Or(m.Name(), $"mesh_{k}");
            var pos = Quiet(m.VertexPositionBuffer() ?? []);
            int n = Rows(pos, 3, name, "positions");
            if (n == 0) continue;
            var attrs = Obj(("POSITION", buf.Floats(pos, n, "VEC3", ArrayBuffer, minmax: true)));
            if (m.VertexNormalBuffer() is { Length: > 0 } nrm)
                attrs["NORMAL"] = buf.Floats(Unit(Quiet(nrm)), Rows(nrm, 3, name, "normals"), "VEC3", ArrayBuffer);
            if (m.VertexTangentBuffer() is { Length: > 0 } tan)
            {
                if (Rows(tan, 3, name, "tangents") != n) throw new GltfException($"{name}: {tan.Length / 3} tangents for {n} vertices");
                var t = Unit(Quiet(tan));
                var sign = m.Property("bp_tangent_sign");
                var sv = sign is not null && sign.ValueCount == n ? sign.ToDoubleArray() : null;
                var t4 = new float[n * 4];
                for (int i = 0; i < n; i++)
                {
                    t4[i * 4] = t[i * 3]; t4[i * 4 + 1] = t[i * 3 + 1]; t4[i * 4 + 2] = t[i * 3 + 2];
                    t4[i * 4 + 3] = sv is not null && sv[i] < 0 ? -1f : 1f;
                }
                attrs["TANGENT"] = buf.Floats(t4, n, "VEC4", ArrayBuffer);
            }
            var nonfinite = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            for (int layer = 0; layer < m.UVLayerCount(); layer++)
            {
                if (m.VertexUVLayerBuffer(layer) is not { Length: > 0 } uv) continue;
                var a = Quiet(uv);
                int rows = Rows(a, 2, name, $"uv layer {layer}");
                var badRows = new List<object?>();
                var hex = new StringBuilder();
                for (int r = 0; r < rows; r++)
                {
                    if (float.IsFinite(a[r * 2]) && float.IsFinite(a[r * 2 + 1])) continue;
                    // glTF forbids inf / NaN (shipped meshes carry some): write 0 and keep the exact values in extras
                    badRows.Add(r);
                    hex.Append(Convert.ToHexStringLower(Bytes(a.AsSpan(r * 2, 2).ToArray())));
                    if (a == uv) a = (float[])uv.Clone();
                    for (int c = 0; c < 2; c++) if (!float.IsFinite(a[r * 2 + c])) a[r * 2 + c] = 0f;
                }
                if (badRows.Count > 0) nonfinite[layer.ToString(CultureInfo.InvariantCulture)] = Obj(("rows", badRows), ("hex", hex.ToString()));
                attrs[$"TEXCOORD_{layer}"] = buf.Floats(a, rows, "VEC2", ArrayBuffer);
            }
            int mi = m.MaximumWeightInfluence();
            bool skinned = mi != 0 && bones.Count > 0 && m.VertexWeightBoneBuffer() is { Length: > 0 };
            if (skinned)
            {
                var (jb, jw) = Weights(m, n, mi, name);
                if (bones.Count <= 256)
                    attrs["JOINTS_0"] = buf.Accessor(Array.ConvertAll(jb, x => unchecked((byte)x)), n, UByte, "VEC4", ArrayBuffer);
                else
                    attrs["JOINTS_0"] = buf.Accessor(Bytes(Array.ConvertAll(jb, x => unchecked((ushort)x))), n, UShort, "VEC4", ArrayBuffer);
                attrs["WEIGHTS_0"] = buf.Floats(jw, n, "VEC4", ArrayBuffer);
            }
            if (m.Property("bp_vertex_id") is { } vid && vid.ValueCount == n)
                attrs["_BP_VERTEX_ID"] = buf.Floats(Array.ConvertAll(vid.ToDoubleArray(), x => Q((float)x)), n, "SCALAR", ArrayBuffer);
            var faces = m.FaceBuffer() ?? [];
            var prim = Obj(("attributes", attrs), ("mode", 4),
                           ("indices", buf.Accessor(Bytes(faces), faces.Length, UInt, "SCALAR", ElementArrayBuffer)));
            if (m.Property("m") is { } mp)
            {
                if (mp.ValueCount == 0) throw new GltfException($"{name}: material property 'm' holds no value");
                if (mp.Type <= CastPropertyType.Long && matIndex.TryGetValue(mp.IntegerAt(0), out int mix)) prim["material"] = mix;
            }
            var gm = Obj(("name", name), ("primitives", new List<object?> { prim }));
            var mex = Props(m);
            mex.Remove("bp_vertex_id");
            mex.Remove("bp_tangent_sign");
            if (nonfinite.Count > 0) mex["bp_nonfinite_uv"] = nonfinite;
            if (mex.Count > 0) gm["extras"] = mex;
            meshes.Add(gm);
            var node = Obj(("name", name), ("mesh", meshes.Count - 1));
            if (skinned) node["skin"] = 0;
            nodes.Add(node);
            top.Add(nodes.Count - 1);
        }

        var animations = Animations(root, bones, buf);
        doc["nodes"] = nodes;
        if (meshes.Count > 0) doc["meshes"] = meshes;
        if (animations.Count > 0) doc["animations"] = animations;
        if (materials.Count > 0) doc["materials"] = materials;
        if (textures.Count > 0)
        {
            doc["textures"] = textures;
            doc["images"] = images;
            doc["samplers"] = new List<object?> { Obj(("magFilter", 9729), ("minFilter", 9987), ("wrapS", 10497), ("wrapT", 10497)) };
        }
        doc["accessors"] = buf.Accessors;
        doc["bufferViews"] = buf.Views;
        while (buf.Data.Length % 4 != 0) buf.Data.WriteByte(0);
        var bin = buf.Data.ToArray();
        if (outDir.Length > 0) Directory.CreateDirectory(outDir);
        string tmp = path + ".partial";
        if (binary)
        {
            doc["buffers"] = new List<object?> { Obj(("byteLength", bin.Length)) };
            var js = Encoding.ASCII.GetBytes(GltfJson.Compact(doc));
            int pad = -js.Length & 3;
            long total = 12 + 8 + js.Length + pad + 8 + (long)bin.Length;
            if (total > uint.MaxValue) throw new GltfException($"GLB of {total} bytes does not fit its u32 length");
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                Span<byte> h = stackalloc byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(h, GlbMagic);
                BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 2);
                BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)total);
                fs.Write(h);
                BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)(js.Length + pad));
                BinaryPrimitives.WriteUInt32LittleEndian(h[4..], ChunkJson);
                fs.Write(h[..8]);
                fs.Write(js);
                for (int i = 0; i < pad; i++) fs.WriteByte(0x20);
                BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)bin.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(h[4..], ChunkBin);
                fs.Write(h[..8]);
                fs.Write(bin);
            }
        }
        else
        {
            string binPath = System.IO.Path.ChangeExtension(path, ".bin");
            System.IO.File.WriteAllBytes(binPath, bin);
            doc["buffers"] = new List<object?> { Obj(("byteLength", bin.Length), ("uri", System.IO.Path.GetFileName(binPath))) };
            System.IO.File.WriteAllText(tmp, GltfJson.Indented(doc, Environment.NewLine), StrictUtf8);
        }
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Cast animations → glTF animations (not in the prototype, which writes none): per bone the <c>rq</c> curve →
    /// rotation, <c>tx ty tz</c> → translation, <c>sx sy sz</c> → scale, LINEAR, times = key / framerate. Bone nodes
    /// come first in the node list, so a curve targets the node of the bone it names; curves naming no bone are
    /// skipped. Only <c>absolute</c> curves are written (additive and relative ones are refused).
    /// </summary>
    private static List<object?> Animations(CastNode root, IReadOnlyList<Bone> bones, Buf buf)
    {
        var result = new List<object?>();
        var anims = root.ChildrenOfType<Animation>();
        if (anims.Count == 0) return result;
        var nodeOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < bones.Count; i++) nodeOf.TryAdd(Or(bones[i].Name(), $"bone_{i}"), i);
        foreach (var anim in anims)
        {
            double fps = anim.Framerate() is float f && f > 0 ? f : 30;
            var samplers = new List<object?>();
            var channels = new List<object?>();
            var inputs = new Dictionary<string, int>(StringComparer.Ordinal);
            int Input(uint[] keys)
            {
                string k = string.Join(',', keys);
                if (inputs.TryGetValue(k, out int a)) return a;
                var t = Array.ConvertAll(keys, x => (float)(x / fps));
                return inputs[k] = buf.Floats(t, t.Length, "SCALAR", null, minmax: true);
            }
            void Channel(int node, string path, uint[] keys, float[] values, string kind)
            {
                samplers.Add(Obj(("input", Input(keys)), ("interpolation", "LINEAR"), ("output", buf.Floats(values, keys.Length, kind, null))));
                channels.Add(Obj(("sampler", samplers.Count - 1), ("target", Obj(("node", node), ("path", path)))));
            }
            foreach (var group in anim.ChildrenOfType<Curve>().GroupBy(c => c.NodeName() ?? ""))
            {
                if (!nodeOf.TryGetValue(group.Key, out int node)) continue;
                var byProp = new Dictionary<string, Curve>(StringComparer.Ordinal);
                foreach (var c in group)
                {
                    if ((c.Property("m")?.StringValue ?? "absolute") != "absolute")
                        throw new GltfException($"{group.Key}.{c.KeyPropertyName()}: curve mode '{c.Property("m")?.StringValue}' is not written to glTF (absolute only)");
                    byProp[c.KeyPropertyName() ?? ""] = c;
                }
                if (byProp.TryGetValue("rq", out var rq) && rq.KeyFrameBuffer() is { Length: > 0 } rk && rq.KeyValueBuffer() is float[] rv)
                    Channel(node, "rotation", rk, rv, "VEC4");
                foreach (var (prefix, path) in new[] { ("t", "translation"), ("s", "scale") })
                {
                    var axes = "xyz".Select(a => byProp.GetValueOrDefault(prefix + a)).ToArray();
                    if (axes.Any(a => a is null)) continue;
                    var keys = axes[0]!.KeyFrameBuffer() ?? [];
                    if (keys.Length == 0 || axes.Any(a => !(a!.KeyFrameBuffer() ?? []).AsSpan().SequenceEqual(keys) || a.KeyValueBuffer() is not float[] v || v.Length != keys.Length))
                        throw new GltfException($"{group.Key}: {path} curves differ in keys; glTF needs one key list for x, y and z");
                    var vals = new float[keys.Length * 3];
                    for (int c = 0; c < 3; c++)
                    {
                        var v = (float[])axes[c]!.KeyValueBuffer()!;
                        for (int k = 0; k < keys.Length; k++) vals[k * 3 + c] = v[k];
                    }
                    Channel(node, path, keys, vals, "VEC3");
                }
            }
            if (channels.Count > 0)
                result.Add(Obj(("name", Or(anim.Name(), "animation")), ("channels", channels), ("samplers", samplers)));
        }
        return result;
    }

    private static string Generator(CastNode root) =>
        root.ChildOfType<Metadata>()?.Software() is { Length: > 0 } s ? s : "nightrunner";

    /// <summary>World matrix of bone <paramref name="i"/> from the written TRS, memoised (a parent cycle is refused;
    /// the prototype recurses until Python's recursion limit).</summary>
    private static double[,] World(int i, int[] parents, double[][] trans, double[][] rots, double[,]?[] glob)
    {
        if (glob[i] is { } done) return done;
        var chain = new List<int>();
        int j = i;
        while (glob[j] is null)
        {
            chain.Add(j);
            int p = parents[j];
            if (!(p >= 0 && p < parents.Length && p != j)) break;
            if (chain.Count > parents.Length) throw new GltfException($"bone {i}: the parent chain is a cycle");
            j = p;
        }
        for (int c = chain.Count - 1; c >= 0; c--)
        {
            int b = chain[c], p = parents[b];
            var loc = Trs(trans[b], rots[b], null);
            glob[b] = p >= 0 && p < parents.Length && p != b ? Mul(glob[p]!, loc) : loc;
        }
        return glob[i]!;
    }

    /// <summary><c>_unit</c>: float rows; rows already unit to 1e-5 are kept bit-exact, others normalised.</summary>
    private static float[] Unit(float[] v)
    {
        var r = new float[v.Length / 3 * 3];
        for (int i = 0; i + 2 < v.Length; i += 3)
        {
            double x = v[i], y = v[i + 1], z = v[i + 2];
            double ln = Math.Sqrt(x * x + y * y + z * z);
            if (Math.Abs(ln - 1) <= 1e-5) { r[i] = v[i]; r[i + 1] = v[i + 1]; r[i + 2] = v[i + 2]; continue; }
            double d = Math.Max(ln, 1e-12);
            r[i] = Q((float)(x / d)); r[i + 1] = Q((float)(y / d)); r[i + 2] = Q((float)(z / d));
        }
        return r;
    }

    /// <summary>JOINTS_0 / WEIGHTS_0 rows: top 4 lanes by weight, zero-weight lanes point at bone 0, rows summing to 1
    /// within 1e-5 written bit-exact, others renormalised, empty rows (1,0,0,0). Equal weights keep lane order here; the
    /// prototype's <c>np.argsort</c> is numpy's unstable SIMD sort, whose order among ties depends on the CPU.</summary>
    private static (long[] Joints, float[] Weights) Weights(Mesh m, int n, int mi, string name)
    {
        var wbSrc = m.VertexWeightBoneBuffer()!;
        var wvSrc = m.VertexWeightValueBuffer() ?? throw new GltfException($"{name}: weight bones without weight values");
        if (wbSrc.Length != (long)n * mi) throw new GltfException($"{name}: {wbSrc.Length} weight bones for {n} vertices × {mi} influences");
        if (wvSrc.Length != (long)n * mi) throw new GltfException($"{name}: {wvSrc.Length} weight values for {n} vertices × {mi} influences");
        var jb = new long[n * 4];
        var jw = new float[n * 4];
        var lanes = new int[mi];
        var b = new long[4];
        var w = new double[4];
        for (int v = 0; v < n; v++)
        {
            int o = v * mi;
            if (mi > 4)
            {
                for (int l = 0; l < mi; l++) lanes[l] = l;
                // np.argsort(-wv): ascending on the negated weight, NaN last
                Array.Sort(lanes, (x, y) =>
                {
                    double a = -(double)Q(wvSrc[o + x]), c = -(double)Q(wvSrc[o + y]);
                    int r = double.IsNaN(a) ? (double.IsNaN(c) ? 0 : 1) : double.IsNaN(c) ? -1 : a.CompareTo(c);
                    return r != 0 ? r : x.CompareTo(y);
                });
                for (int l = 0; l < 4; l++) { b[l] = wbSrc[o + lanes[l]]; w[l] = Q(wvSrc[o + lanes[l]]); }
            }
            else
                for (int l = 0; l < 4; l++) { b[l] = l < mi ? wbSrc[o + l] : 0; w[l] = l < mi ? Q(wvSrc[o + l]) : 0; }
            double s = 0;
            for (int l = 0; l < 4; l++)
            {
                if (!(w[l] > 0)) b[l] = 0;
                s += (float)w[l];
            }
            for (int l = 0; l < 4; l++)
            {
                jb[v * 4 + l] = b[l];
                jw[v * 4 + l] = Math.Abs(s - 1) <= 1e-5 ? Q((float)w[l])
                    : s > 0 ? Q((float)(w[l] / Math.Max(s, 1e-12)))
                    : l == 0 ? 1f : 0f;
            }
        }
        return (jb, jw);
    }

    /// <summary><c>_props</c>: every <c>bp_*</c> property, one value as a scalar, otherwise a list.</summary>
    private static OrderedDictionary<string, object?> Props(CastNode node)
    {
        var o = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, p) in node.Properties)
        {
            if (!k.StartsWith("bp_", StringComparison.Ordinal)) continue;
            var v = PropertyValues(p);
            o[k] = v.Count == 1 ? v[0] : v;
        }
        return o;
    }

    private static List<object?> PropertyValues(CastProperty p)
    {
        switch (p.Type)
        {
            case CastPropertyType.String: return p.StringValue is { } s ? [s] : [];
            case CastPropertyType.Byte or CastPropertyType.Short or CastPropertyType.Integer or CastPropertyType.Long:
                var ints = new List<object?>(p.ValueCount);
                for (int i = 0; i < p.ValueCount; i++) ints.Add(p.IntegerAt(i));
                return ints;
            case CastPropertyType.Double: return p.Doubles.Select(x => (object?)x).ToList();
            default: return p.Floats.Select(x => (object?)(double)x).ToList();
        }
    }

    private static (double[] Min, double[] Max) MinMax(float[] v, int rows, int width)
    {
        var min = new double[width];
        var max = new double[width];
        for (int c = 0; c < width; c++)
        {
            float lo = v[c], hi = v[c];
            for (int r = 1; r < rows; r++)
            {
                float x = v[r * width + c];
                if (float.IsNaN(lo) || float.IsNaN(x)) { lo = hi = float.NaN; break; }
                if (x < lo) lo = x;
                if (x > hi) hi = x;
            }
            min[c] = lo;
            max[c] = hi;
        }
        return (min, max);
    }

    // ---- reader -------------------------------------------------------------------------------------------------

    private sealed class AccessorData(double[] data, int count, int width)
    {
        public readonly double[] Data = data;
        public readonly int Count = count, Width = width;
        public double this[int row, int col] => Data[row * Width + col];
    }

    /// <summary><c>gltf_to_cast</c>: the glTF / GLB at <paramref name="path"/> as a Cast scene.</summary>
    public static CastFile ReadGltf(string path)
    {
        var (doc, bufs) = LoadDocument(path);
        var nodes = AsList(GetOr(doc, "nodes", new List<object?>()), "nodes").Select((x, i) => AsObj(x, $"nodes[{i}]")).ToList();
        var parent = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++)
            foreach (var c in AsList(GetOr(nodes[i], "children", new List<object?>()), $"nodes[{i}].children"))
                if (c is BigInteger ci && ci >= int.MinValue && ci <= int.MaxValue) parent[(int)ci] = i;

        double[,] World(int i)
        {
            var m = NodeMatrix(nodes[i], i);
            int steps = 0;
            while (parent.TryGetValue(i, out int p))
            {
                if (++steps > nodes.Count) throw new GltfException($"nodes[{i}]: the parent chain is a cycle");
                i = p;
                m = Mul(NodeMatrix(nodes[i], i), m);
            }
            return m;
        }

        var cast = new CastFile();
        var root = cast.CreateRoot();
        var meta = root.CreateMetadata();
        meta.SetUpAxis("y");
        var asset = GetOr(doc, "asset", null);
        var generator = GltfJson.Truthy(asset) ? GetOr(AsObj(asset, "asset"), "generator", null) : null;
        meta.SetSoftware(GltfJson.PyStr(GltfJson.Truthy(generator) ? generator : "glTF"));
        var mdl = root.CreateModel();
        var scenesValue = GetOr(doc, "scenes", null);
        var scenes = GltfJson.Truthy(scenesValue) ? AsList(scenesValue, "scenes") : [new OrderedDictionary<string, object?>()];
        var scene = AsObj(Item(scenes, GetOr(doc, "scene", BigInteger.Zero), "scenes"), "scene");
        mdl.SetName(Name(GetOr(scene, "name", null), Stem(System.IO.Path.GetFileName(path)), "scene name"));

        var skinsValue = GetOr(doc, "skins", null);
        var skins = GltfJson.Truthy(skinsValue) ? AsList(skinsValue, "skins") : [];
        var joints = skins.Count > 0 ? AsList(Get(AsObj(skins[0], "skins[0]"), "joints", "skins[0]"), "skins[0].joints")
                                       .Select(j => Index(j, nodes.Count, "skins[0].joints")).ToList() : [];
        var jointPos = new Dictionary<int, int>();
        for (int k = 0; k < joints.Count; k++) jointPos[joints[k]] = k;
        if (joints.Count > 0)
        {
            var sk = mdl.CreateSkeleton();
            for (int k = 0; k < joints.Count; k++)
            {
                int j = joints[k];
                var n = nodes[j];
                var b = sk.CreateBone();
                b.SetName(Name(GetOr(n, "name", null), $"joint_{k}", $"nodes[{j}].name"));
                int? p = parent.TryGetValue(j, out int pp) ? pp : null;
                // skip non-joint helper nodes (armature objects)
                for (int steps = 0; p is { } q && !jointPos.ContainsKey(q); steps++)
                {
                    if (steps > nodes.Count) throw new GltfException($"nodes[{j}]: the parent chain is a cycle");
                    p = parent.TryGetValue(q, out int up) ? up : null;
                }
                b.SetParentIndex(p is { } jp ? jointPos[jp] : -1);
                var m = NodeMatrix(n, j);
                if (p is null && parent.ContainsKey(j)) m = World(j);   // root joint under a helper: bake the helper's transform
                var sc = ColumnNorms(m);
                var r = new double[3, 3];
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++) r[row, col] = m[row, col] / (sc[col] > 1e-12 ? sc[col] : 1.0);
                b.SetLocalPosition([F(m[0, 3]), F(m[1, 3]), F(m[2, 3])]);
                b.SetLocalRotation(Fs(CastExport.QuaternionXyzw(r)));
                b.SetScale(Fs(sc));
                b.SetSegmentScaleCompensate(false);
                var w = World(j);
                var ws = ColumnNorms(w);
                var wr = new double[3, 3];
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++) wr[row, col] = w[row, col] / (ws[col] > 1e-12 ? ws[col] : 1.0);
                b.SetWorldPosition([F(w[0, 3]), F(w[1, 3]), F(w[2, 3])]);
                b.SetWorldRotation(Fs(CastExport.QuaternionXyzw(wr)));
                SetExtras(b, GetOr(n, "extras", null), $"nodes[{j}].extras");
            }
        }

        var matHash = new Dictionary<int, ulong>();
        var mats = AsList(GetOr(doc, "materials", new List<object?>()), "materials");
        for (int i = 0; i < mats.Count; i++)
        {
            var g = AsObj(mats[i], $"materials[{i}]");
            var mn = mdl.CreateMaterial();
            mn.SetName(Name(GetOr(g, "name", null), $"material_{i}", $"materials[{i}].name"));
            mn.SetType("pbr");
            SetExtras(mn, GetOr(g, "extras", null), $"materials[{i}].extras");
            matHash[i] = mn.Hash;
        }

        for (int ni = 0; ni < nodes.Count; ni++)
        {
            var n = nodes[ni];
            if (!n.TryGetValue("mesh", out var meshRef)) continue;
            var gm = AsObj(Item(AsList(Get(doc, "meshes", "document"), "meshes"), meshRef, "meshes"), $"nodes[{ni}].mesh");
            string name = Name(GetOr(n, "name", null), null, $"nodes[{ni}].name") is { Length: > 0 } nn ? nn
                : Name(GetOr(gm, "name", null), $"mesh_{ni}", "mesh name");
            bool skinned = n.ContainsKey("skin");
            var xf = skinned ? null : World(ni);
            // the node's skin may differ from skin 0: remap its joints into skin 0's order by node index
            long[]? jmap = null;
            if (skinned && !(n["skin"] is BigInteger s0 && s0.IsZero))
                jmap = AsList(Get(AsObj(Item(skins, n["skin"], "skins"), "skin"), "joints", "skin"), "skin joints")
                    .Select(j => j is BigInteger bj && bj >= int.MinValue && bj <= int.MaxValue && jointPos.TryGetValue((int)bj, out int jp) ? (long)jp : 0L).ToArray();
            var prims = AsList(GetOr(gm, "primitives", new List<object?>()), "primitives");
            var gmExtras = GetOr(gm, "extras", null);
            for (int pi = 0; pi < prims.Count; pi++)
            {
                var prim = AsObj(prims[pi], "primitive");
                if (Number(GetOr(prim, "mode", new BigInteger(4)), "primitive mode") != 4) continue;
                var at = AsObj(Get(prim, "attributes", "primitive"), "attributes");
                var pos = ReadAccessor(doc, bufs, Get(at, "POSITION", "attributes"), 3, "POSITION");
                var nrm = at.TryGetValue("NORMAL", out var na) ? ReadAccessor(doc, bufs, na, 3, "NORMAL") : null;
                var tan = at.TryGetValue("TANGENT", out var ta) ? ReadAccessor(doc, bufs, ta, 4, "TANGENT") : null;
                double[] P = pos.Data, N = nrm?.Data ?? [], T = tan?.Data ?? [];
                if (xf is not null)
                {
                    P = Transform(pos, xf, translate: true);
                    if (nrm is not null)
                    {
                        var r3 = new double[3, 3];
                        for (int row = 0; row < 3; row++)
                            for (int col = 0; col < 3; col++) r3[row, col] = xf[row, col];
                        var inv = Inverse(r3);
                        N = new double[nrm.Data.Length];
                        for (int v = 0; v < nrm.Count; v++)
                            for (int c = 0; c < 3; c++)
                                N[v * 3 + c] = Dot3(nrm[v, 0], inv[0, c], nrm[v, 1], inv[1, c], nrm[v, 2], inv[2, c]);
                    }
                    if (tan is not null) T = Transform(tan, xf, translate: false);
                }
                long[] idx;
                if (prim.TryGetValue("indices", out var ia))
                {
                    var ix = ReadAccessor(doc, bufs, ia, null, "indices");
                    idx = Array.ConvertAll(ix.Data, ToLong);
                }
                else
                {
                    idx = new long[pos.Count];
                    for (int i = 0; i < idx.Length; i++) idx[i] = i;
                }
                var me = mdl.CreateMesh();
                me.SetName(pi == 0 ? name : $"{name}#{pi}");
                me.SetVertexPositionBuffer(Fn(P));
                if (nrm is not null) me.SetVertexNormalBuffer(Fn(N));
                if (tan is not null)
                {
                    var t3 = new float[tan.Count * 3];
                    var sign = new float[tan.Count];
                    for (int v = 0; v < tan.Count; v++)
                    {
                        for (int c = 0; c < 3; c++) t3[v * 3 + c] = Q((float)T[v * 4 + c]);
                        sign[v] = T[v * 4 + 3] < 0 ? -1f : 1f;
                    }
                    me.SetVertexTangentBuffer(t3);
                    me.CreateProperty("bp_tangent_sign", "f").SetValues(sign);
                }
                int layers = 0;
                var nonfiniteValue = GltfJson.Truthy(gmExtras) ? GetOr(AsObj(gmExtras, "mesh extras"), "bp_nonfinite_uv", null) : null;
                var nonfinite = GltfJson.Truthy(nonfiniteValue) ? nonfiniteValue : null;
                while (at.TryGetValue($"TEXCOORD_{layers}", out var ua))
                {
                    var acc = ReadAccessor(doc, bufs, ua, 2, $"TEXCOORD_{layers}");
                    var uv = Fn(acc.Data);
                    var nf = nonfinite is OrderedDictionary<string, object?> nfo ? nfo.GetValueOrDefault(layers.ToString(CultureInfo.InvariantCulture)) : null;
                    if (GltfJson.Truthy(nf) && pi == 0) RestoreNonFinite(uv, AsObj(nf, "bp_nonfinite_uv entry"));
                    me.SetVertexUVLayerBuffer(layers, uv);
                    layers++;
                }
                me.SetUVLayerCount(layers);
                me.SetColorLayerCount(0);
                me.SetFaceBuffer(UInts(idx, $"{name}: indices"));
                if (skinned)
                {
                    var sets = new List<(AccessorData J, AccessorData W)>();
                    for (int k = 0; at.ContainsKey($"JOINTS_{k}") && at.ContainsKey($"WEIGHTS_{k}"); k++)
                        sets.Add((ReadAccessor(doc, bufs, at[$"JOINTS_{k}"], null, $"JOINTS_{k}"),
                                  ReadAccessor(doc, bufs, at[$"WEIGHTS_{k}"], null, $"WEIGHTS_{k}")));
                    if (sets.Count > 0)
                    {
                        int rows = sets[0].J.Count;
                        if (sets.Any(x => x.J.Count != rows || x.W.Count != rows || x.J.Width != x.W.Width))
                            throw new GltfException($"{name}: JOINTS_n / WEIGHTS_n sets differ in shape");
                        int width = sets.Sum(x => x.J.Width);
                        var jb = new long[rows * width];
                        var wv = new float[rows * width];
                        for (int v = 0, col = 0; v < rows; v++, col = 0)
                            foreach (var (J, W) in sets)
                                for (int c = 0; c < J.Width; c++, col++)
                                {
                                    long jj = ToLong(J[v, c]);
                                    if (jmap is not null)
                                    {
                                        if (jj < -jmap.Length || jj >= jmap.Length) throw new GltfException($"{name}: joint {jj} outside the node's skin");
                                        jj = jmap[jj < 0 ? jj + jmap.Length : jj];
                                    }
                                    jb[v * width + col] = jj;
                                    wv[v * width + col] = F(W[v, c]);
                                }
                        me.SetMaximumWeightInfluence(width);
                        me.SetSkinningMethod("linear");
                        me.SetVertexWeightBoneBuffer(UInts(jb, $"{name}: joints"));
                        me.SetVertexWeightValueBuffer(wv);
                    }
                }
                if (at.TryGetValue("_BP_VERTEX_ID", out var va))
                {
                    var vid = ReadAccessor(doc, bufs, va, null, "_BP_VERTEX_ID").Data;
                    var ids = new uint[vid.Length];
                    for (int i = 0; i < ids.Length; i++)
                    {
                        double r = Math.Round(vid[i], MidpointRounding.ToEven);
                        if (!(r >= 0 && r <= uint.MaxValue)) throw new GltfException($"{name}: _BP_VERTEX_ID {GltfJson.FloatRepr(vid[i], false)} does not fit u32");
                        ids[i] = (uint)r;
                    }
                    me.CreateProperty("bp_vertex_id", "i").SetValues(ids);
                }
                var merged = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var src in new[] { gmExtras, GetOr(n, "extras", null) })
                    if (GltfJson.Truthy(src))
                        foreach (var (key, val) in AsObj(src, "extras")) merged[key] = val;
                foreach (var (key, val) in merged)
                    if (key.StartsWith("bp_", StringComparison.Ordinal) && key is not ("bp_vertex_id" or "bp_tangent_sign" or "bp_nonfinite_uv"))
                        SetProperty(me, key, val);
                if (prim.TryGetValue("material", out var mref) && mref is BigInteger mb && mb >= 0 && mb < int.MaxValue
                    && matHash.TryGetValue((int)mb, out ulong mh))
                    me.SetMaterial(mh);
            }
        }
        return cast;
    }

    /// <summary>Rows the editor left at the 0 placeholder get their original (non-finite) value back.</summary>
    private static void RestoreNonFinite(float[] uv, OrderedDictionary<string, object?> nf)
    {
        var rows = AsList(Get(nf, "rows", "bp_nonfinite_uv"), "rows").Select(x => x is BigInteger b ? (long)b
            : throw new GltfException("bp_nonfinite_uv rows are not integers")).ToArray();
        if (Get(nf, "hex", "bp_nonfinite_uv") is not string hex || hex.Length % 16 != 0)
            throw new GltfException("bp_nonfinite_uv hex is not whole (u, v) float pairs");
        byte[] raw;
        try { raw = Convert.FromHexString(hex); }
        catch (FormatException) { throw new GltfException("bp_nonfinite_uv hex is not hexadecimal"); }
        var vals = new float[raw.Length / 4];
        for (int i = 0; i < vals.Length; i++) vals[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
        int count = uv.Length / 2;
        if (rows.Length != vals.Length / 2 || rows.Any(r => r >= count)) return;
        for (int i = 0; i < rows.Length; i++)
        {
            long r = rows[i] < 0 ? rows[i] + count : rows[i];
            if (r < 0) throw new GltfException($"bp_nonfinite_uv row {rows[i]} is outside the layer");
            bool keep = true;
            for (int c = 0; c < 2; c++)
            {
                float placeholder = float.IsFinite(vals[i * 2 + c]) ? vals[i * 2 + c] : 0f;
                if (uv[r * 2 + c] != placeholder) keep = false;
            }
            if (keep) { uv[r * 2] = vals[i * 2]; uv[r * 2 + 1] = vals[i * 2 + 1]; }
        }
    }

    private static double[] Transform(AccessorData a, double[,] xf, bool translate)
    {
        var o = new double[a.Data.Length];
        for (int v = 0; v < a.Count; v++)
        {
            for (int c = 0; c < 3; c++)
            {
                double x = Dot3(a[v, 0], xf[c, 0], a[v, 1], xf[c, 1], a[v, 2], xf[c, 2]);
                o[v * a.Width + c] = translate ? x + xf[c, 3] : x;
            }
            for (int c = 3; c < a.Width; c++) o[v * a.Width + c] = a[v, c];
        }
        return o;
    }

    /// <summary>One element of an (n×3)·(3×3) product as numpy's BLAS matmul forms it: an accumulator starting at +0
    /// and fused multiply-adds (so an all −0 sum is +0, as the prototype's is).</summary>
    private static double Dot3(double a0, double b0, double a1, double b1, double a2, double b2) =>
        Math.FusedMultiplyAdd(a2, b2, Math.FusedMultiplyAdd(a1, b1, Math.FusedMultiplyAdd(a0, b0, 0.0)));

    private static double[] ColumnNorms(double[,] m)
    {
        var s = new double[3];
        for (int c = 0; c < 3; c++) s[c] = Math.Sqrt(m[0, c] * m[0, c] + m[1, c] * m[1, c] + m[2, c] * m[2, c]);
        return s;
    }

    /// <summary><c>_set_prop</c>: bools → <c>b</c>, integers → <c>i</c>, numbers → <c>f</c>, anything else → <c>s</c>
    /// (<c>str()</c> of the value). The prototype keeps several strings in memory and writes only the first; that, an
    /// integer outside u32 and a float <c>struct.pack</c> refuses are refused here.</summary>
    private static void SetProperty(CastNode node, string key, object? val)
    {
        var vals = val as List<object?> ?? [val];
        if (vals.Count == 0) return;
        if (vals.All(v => v is bool))
            node.CreateProperty(key, "b").SetValues(vals.Select(v => (byte)((bool)v! ? 1 : 0)).ToArray());
        else if (vals.All(v => v is BigInteger))
            node.CreateProperty(key, "i").SetValues(vals.Select(v => (BigInteger)v! is var b && b >= 0 && b <= uint.MaxValue
                ? (uint)b : throw new GltfException($"property '{key}': {b} does not fit u32")).ToArray());
        else if (vals.All(v => v is BigInteger or double or bool))
            node.CreateProperty(key, "f").SetValues(vals.Select(v => F(v switch { bool b => b ? 1.0 : 0.0, BigInteger i => (double)i, _ => (double)v! })).ToArray());
        else if (vals.Count == 1)
            node.CreateProperty(key, "s").SetString(GltfJson.PyStr(vals[0]));
        else throw new GltfException($"property '{key}': a Cast string property holds one value, not {vals.Count}");
    }

    private static void SetExtras(CastNode node, object? extras, string what)
    {
        if (!GltfJson.Truthy(extras)) return;
        foreach (var (key, val) in AsObj(extras, what))
            if (key.StartsWith("bp_", StringComparison.Ordinal)) SetProperty(node, key, val);
    }

    private static (OrderedDictionary<string, object?> Doc, List<byte[]> Buffers) LoadDocument(string path)
    {
        var raw = System.IO.File.ReadAllBytes(path);
        OrderedDictionary<string, object?> doc;
        var bufs = new List<byte[]>();
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == GlbMagic)
        {
            if (raw.Length < 12) throw new GltfException($"{path}: GLB header is {raw.Length} bytes, needs 12");
            object? json = null;
            bool haveJson = false;
            var chunks = new List<byte[]>();
            long off = 12;
            while (off + 8 <= raw.Length)
            {
                uint ln = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)off));
                uint kind = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)off + 4));
                long end = Math.Min(off + 8 + ln, raw.Length);
                var body = raw.AsSpan((int)(off + 8), (int)(end - off - 8));
                if (kind == ChunkJson) { json = GltfJson.Parse(Utf8(body, path)); haveJson = true; }
                else if (kind == ChunkBin) chunks.Add(body.ToArray());
                off += 8 + ln;
            }
            if (!haveJson) throw new GltfException($"{path}: GLB without a JSON chunk");
            doc = AsObj(json, "document");
            foreach (var b in AsList(GetOr(doc, "buffers", new List<object?>()), "buffers"))
            {
                var bo = AsObj(b, "buffer");
                bufs.Add(bo.TryGetValue("uri", out var uri) ? ReadUri(path, uri) : chunks.Count > 0 ? chunks[0] : []);
            }
            return (doc, bufs);
        }
        doc = AsObj(GltfJson.Parse(Utf8(raw, path)), "document");
        foreach (var b in AsList(GetOr(doc, "buffers", new List<object?>()), "buffers"))
            bufs.Add(ReadUri(path, GetOr(AsObj(b, "buffer"), "uri", "")));
        return (doc, bufs);
    }

    private static string Utf8(ReadOnlySpan<byte> bytes, string path)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new GltfException($"{path}: the JSON is not valid UTF-8"); }
    }

    private static byte[] ReadUri(string path, object? uriValue)
    {
        if (uriValue is not string uri) throw new GltfException($"{path}: buffer uri is not a string");
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0) throw new GltfException($"{path}: data URI without a comma");
            // base64.b64decode (validate=False) drops characters outside the alphabet
            var clean = new string(uri[(comma + 1)..].Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=').ToArray());
            try { return Convert.FromBase64String(clean); }
            catch (FormatException) { throw new GltfException($"{path}: data URI is not valid base64"); }
        }
        string file = System.IO.Path.Combine(Parent(path), Uri.UnescapeDataString(uri));
        try { return System.IO.File.ReadAllBytes(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GltfException($"{path}: buffer '{uri}' cannot be read ({e.Message})");
        }
    }

    /// <summary><c>_accessor</c>: <paramref name="width"/>, when given, is the component count the Cast needs (the
    /// prototype fails later, on a reshape or when saving, for any other).</summary>
    private static AccessorData ReadAccessor(OrderedDictionary<string, object?> doc, List<byte[]> bufs, object? index, int? width, string what)
    {
        var a = AsObj(Item(AsList(Get(doc, "accessors", "document"), "accessors"), index, "accessors"), what);
        int ctype = (int)Number(Get(a, "componentType", what), what);
        var (size, reader, max) = ctype switch
        {
            5120 => (1, (Func<byte[], int, double>)((d, o) => (sbyte)d[o]), 127.0),
            5121 => (1, (d, o) => d[o], 255.0),
            5122 => (2, (d, o) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o)), 32767.0),
            5123 => (2, (d, o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)), 65535.0),
            5125 => (4, (d, o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)), 4294967295.0),
            5126 => (4, (d, o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o)), 0.0),
            _ => throw new GltfException($"{what}: component type {ctype} is not supported"),
        };
        int nc = Get(a, "type", what) switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
            var t => throw new GltfException($"{what}: accessor type {GltfJson.PyRepr(t)} is not supported"),
        };
        if (width is { } w && nc != w) throw new GltfException($"{what}: {nc} components per element, the Cast needs {w}");
        long count = (long)Number(Get(a, "count", what), what);
        if (count < 0 || count * nc > Array.MaxLength) throw new GltfException($"{what}: count {count} is out of range");
        if (a.ContainsKey("sparse")) throw new GltfException("sparse accessors are not supported");
        var o = new double[count * nc];
        if (a.TryGetValue("bufferView", out var vref))
        {
            var v = AsObj(Item(AsList(Get(doc, "bufferViews", "document"), "bufferViews"), vref, "bufferViews"), "bufferView");
            var data = bufs[Index(Get(v, "buffer", "bufferView"), bufs.Count, "buffers")];
            long start = (long)Number(GetOr(v, "byteOffset", BigInteger.Zero), "byteOffset") + (long)Number(GetOr(a, "byteOffset", BigInteger.Zero), "byteOffset");
            var strideValue = GetOr(v, "byteStride", null);
            long stride = GltfJson.Truthy(strideValue) ? (long)Number(strideValue, "byteStride") : size * nc;
            if (start < 0 || stride < 0) throw new GltfException($"{what}: negative offset or stride");
            if (count > 0 && start + (count - 1) * stride + size * nc > data.Length || start > data.Length)
                throw new GltfException($"{what}: {count} elements at offset {start}, stride {stride} run past the {data.Length}-byte buffer");
            for (long r = 0; r < count; r++)
                for (int c = 0; c < nc; c++) o[r * nc + c] = reader(data, (int)(start + r * stride + c * size));
        }
        if (ctype != 5126 && GltfJson.Truthy(GetOr(a, "normalized", null)))
            for (int i = 0; i < o.Length; i++) o[i] /= max;
        return new AccessorData(o, (int)count, nc);
    }

    private static double[,] NodeMatrix(OrderedDictionary<string, object?> n, int index)
    {
        if (n.TryGetValue("matrix", out var mv))
        {
            var v = Numbers(mv, 16, $"nodes[{index}].matrix");
            var m = new double[4, 4];
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++) m[r, c] = v[c * 4 + r];
            return m;
        }
        return Trs(Numbers(GetOr(n, "translation", new List<object?> { 0.0, 0.0, 0.0 }), 3, $"nodes[{index}].translation"),
                   Numbers(GetOr(n, "rotation", new List<object?> { 0.0, 0.0, 0.0, 1.0 }), 4, $"nodes[{index}].rotation"),
                   Numbers(GetOr(n, "scale", new List<object?> { 1.0, 1.0, 1.0 }), 3, $"nodes[{index}].scale"));
    }

    // ---- math -----------------------------------------------------------------------------------------------------

    private static double[,] QuatToMat(double[] q)
    {
        double x = q[0], y = q[1], z = q[2], w = q[3];
        double n = x * x + y * y + z * z + w * w;
        if (n < 1e-20) return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        double s = 2.0 / n;
        return new double[,]
        {
            { 1 - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w) },
            { s * (x * y + z * w), 1 - s * (x * x + z * z), s * (y * z - x * w) },
            { s * (x * z - y * w), s * (y * z + x * w), 1 - s * (x * x + y * y) },
        };
    }

    private static double[,] Trs(double[] t, double[] q, double[]? s)
    {
        var r = QuatToMat(q);
        var m = new double[4, 4];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++) m[row, col] = r[row, col] * (s?[col] ?? 1.0);
            m[row, 3] = t[row];
        }
        m[3, 3] = 1;
        return m;
    }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        var c = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) c[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j] + a[i, 3] * b[3, j];
        return c;
    }

    /// <summary>LU with partial pivoting (as LAPACK <c>getrf</c>/<c>getrs</c>, which <c>np.linalg.inv</c> calls); an exactly
    /// singular matrix is refused, as numpy raises.</summary>
    private static double[,] Inverse(double[,] a)
    {
        int n = a.GetLength(0);
        var lu = (double[,])a.Clone();
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (Math.Abs(lu[i, k]) > Math.Abs(lu[p, k])) p = i;
            if (lu[p, k] == 0) throw new GltfException("singular matrix");
            if (p != k)
            {
                for (int j = 0; j < n; j++) (lu[k, j], lu[p, j]) = (lu[p, j], lu[k, j]);
                (perm[k], perm[p]) = (perm[p], perm[k]);
            }
            double rcp = 1.0 / lu[k, k];
            for (int i = k + 1; i < n; i++)
            {
                lu[i, k] *= rcp;
                for (int j = k + 1; j < n; j++) lu[i, j] -= lu[i, k] * lu[k, j];
            }
        }
        var inv = new double[n, n];
        var x = new double[n];
        for (int col = 0; col < n; col++)
        {
            for (int i = 0; i < n; i++) x[i] = perm[i] == col ? 1 : 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < i; j++) x[i] -= lu[i, j] * x[j];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = i + 1; j < n; j++) x[i] -= lu[i, j] * x[j];
                x[i] /= lu[i, i];
            }
            for (int i = 0; i < n; i++) inv[i, col] = x[i];
        }
        return inv;
    }

    /// <summary>Python <c>round(x, digits)</c>: correctly rounded to <paramref name="digits"/> decimals, ties to even.</summary>
    internal static double PyRound(double x, int digits)
    {
        if (!double.IsFinite(x) || x == 0) return x;
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        exp -= 1075;                                     // x = ±mant · 2^exp
        if (exp >= 0) return x;                          // an integer already
        var num = new BigInteger(mant) * BigInteger.Pow(10, digits);
        var den = BigInteger.One << -exp;
        var q = BigInteger.DivRem(num, den, out var rem);
        int cmp = (rem * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += 1;
        if (q.IsZero) return x < 0 ? -0.0 : 0.0;
        double r = double.Parse(q.ToString(CultureInfo.InvariantCulture) + "e-" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return x < 0 ? -r : r;
    }

    // ---- small helpers --------------------------------------------------------------------------------------------

    private static OrderedDictionary<string, object?> Obj(params (string Key, object? Value)[] items)
    {
        var o = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in items) o[k] = v;
        return o;
    }

    private static List<object?> Boxed(double[] v) => v.Select(x => (object?)x).ToList();

    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    /// <summary>A name read from JSON: <c>value or fallback</c>; a truthy non-string is refused (Cast strings only).</summary>
    private static string Name(object? value, string? fallback, string what) =>
        !GltfJson.Truthy(value) ? fallback ?? "" : value as string ?? throw new GltfException($"{what} is not a string");

    private static int Rows(float[] v, int width, string name, string what) =>
        v.Length % width == 0 ? v.Length / width : throw new GltfException($"{name}: {v.Length} {what} values are not whole rows of {width}");

    /// <summary>NaNs quieted, as the prototype's float32 → Python float → float32 round trip (x86 conversions) leaves them.</summary>
    private static float Q(float x) =>
        float.IsNaN(x) ? BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(x) | 0x00400000u) : x;

    private static float[] Quiet(float[] v)
    {
        float[]? copy = null;
        for (int i = 0; i < v.Length; i++)
            if (float.IsNaN(v[i]) && Q(v[i]) is var q && BitConverter.SingleToUInt32Bits(q) != BitConverter.SingleToUInt32Bits(v[i]))
                (copy ??= (float[])v.Clone())[i] = q;
        return copy ?? v;
    }

    /// <summary><c>struct.pack('f', x)</c>: rounds to nearest; a finite value beyond float range is refused (OverflowError).</summary>
    private static float F(double x)
    {
        float y = (float)x;
        if (float.IsInfinity(y) && !double.IsInfinity(x)) throw new GltfException($"{GltfJson.FloatRepr(x, false)} is too large for a Cast float");
        return Q(y);
    }

    private static float[] Fs(double[] v) => Array.ConvertAll(v, F);

    /// <summary><c>astype(np.float32)</c>: rounds to nearest, overflow gives inf.</summary>
    private static float[] Fn(double[] v) => Array.ConvertAll(v, x => Q((float)x));

    /// <summary><c>astype(np.int64)</c> on x86: truncation; NaN and out-of-range values give <see cref="long.MinValue"/>
    /// (.NET saturates instead).</summary>
    private static long ToLong(double x) => x >= -9.2233720368547758E18 && x < 9.2233720368547758E18 ? (long)x : long.MinValue;

    private static uint[] UInts(long[] v, string what)
    {
        if (v.Length == 0) throw new GltfException($"{what}: empty (castTypeForMaximum has no maximum)");
        return Array.ConvertAll(v, x => x is >= 0 and <= uint.MaxValue ? (uint)x : throw new GltfException($"{what}: {x} does not fit u32"));
    }

    private static byte[] Bytes<T>(T[] v) where T : struct =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    private static object? GetOr(OrderedDictionary<string, object?> o, string key, object? fallback) =>
        o.TryGetValue(key, out var v) ? v : fallback;

    private static object? Get(OrderedDictionary<string, object?> o, string key, string what) =>
        o.TryGetValue(key, out var v) ? v : throw new GltfException($"{what} has no '{key}'");

    private static OrderedDictionary<string, object?> AsObj(object? v, string what) =>
        v as OrderedDictionary<string, object?> ?? throw new GltfException($"{what} is not a JSON object");

    private static List<object?> AsList(object? v, string what) =>
        v as List<object?> ?? throw new GltfException($"{what} is not a JSON array");

    /// <summary>An index into a list. Negative indices are refused (Python would count from the end).</summary>
    private static int Index(object? v, int count, string what) =>
        v is BigInteger b && b >= 0 && b < count ? (int)b : throw new GltfException($"{what}: index {GltfJson.PyRepr(v)} is out of range ({count})");

    private static object? Item(List<object?> list, object? index, string what) => list[Index(index, list.Count, what)];

    private static double Number(object? v, string what) => v switch
    {
        BigInteger b => (double)b,
        double d => d,
        bool b => b ? 1 : 0,
        _ => throw new GltfException($"{what}: {GltfJson.PyRepr(v)} is not a number"),
    };

    private static double[] Numbers(object? v, int count, string what)
    {
        var l = AsList(v, what);
        if (l.Count != count) throw new GltfException($"{what} has {l.Count} values, not {count}");
        return l.Select(x => Number(x, what)).ToArray();
    }

    /// <summary>The folder of <paramref name="path"/> ("" for a bare file name, which means the current folder).</summary>
    private static string Parent(string path) => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "";

    /// <summary>pathlib's <c>name</c> of a Windows path: the last component, trailing separators ignored.</summary>
    private static string LastComponent(string path)
    {
        var t = path.TrimEnd('/', '\\');
        int i = t.LastIndexOfAny(['/', '\\']);
        return i >= 0 ? t[(i + 1)..] : t;
    }

    /// <summary>pathlib's <c>suffix</c>: from the last dot, unless the name starts with it or ends with it.</summary>
    internal static string Suffix(string path)
    {
        var name = LastComponent(path);
        int i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[i..] : "";
    }

    /// <summary>pathlib's <c>stem</c>.</summary>
    internal static string Stem(string path)
    {
        var name = LastComponent(path);
        int i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[..i] : name;
    }

    /// <summary><c>Path(os.path.relpath(src, out_dir)).as_posix()</c>; another drive is refused (relpath raises).</summary>
    private static string RelativeUri(string src, string outDir)
    {
        string full = System.IO.Path.GetFullPath(src);
        string rel = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(string.IsNullOrEmpty(outDir) ? "." : outDir), full);
        if (System.IO.Path.IsPathRooted(rel)) throw new GltfException($"texture '{src}' is on another drive than the output folder");
        return rel.Replace('\\', '/');
    }

    /// <summary><c>urllib.parse.quote(s)</c>: UTF-8, everything but letters, digits, <c>_.-~</c> and <c>/</c> percent-encoded.</summary>
    internal static string Quote(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~' or (byte)'/') sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
