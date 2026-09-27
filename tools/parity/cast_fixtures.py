"""Builds Cast fixtures with the prototype's castlib (read-only import by file path) for the C# port's tests.

Usage: python cast_fixtures.py <out folder>, with PYTHONDONTWRITEBYTECODE=1 and NR_PROTO=<path to nightrunner-main>.
Writes only into <out folder>.
Every builder here is mirrored line for line in Nightrunner.Tests/CastLibTests.cs (CastSynth).
"""
import hashlib
import importlib.util
import io
import json
import os
import struct
import subprocess
import sys

HERE = os.path.abspath(sys.argv[1])
os.makedirs(HERE, exist_ok=True)
PROTO = os.environ["NR_PROTO"]
spec = importlib.util.spec_from_file_location("castlib", os.path.join(PROTO, "nightrunner", "cast", "castlib.py"))
castlib = importlib.util.module_from_spec(spec)
spec.loader.exec_module(castlib)
BASE = 0x534E495752545250
NAN_F = float("nan")


def fresh():
    castlib.castHashBase = BASE


def fv(i, k=0):
    return ((i * 7919 + k * 104729) % 20011) / 1000.0 - 10.0


def uv(i, k=0):
    return ((i * 7919 + k * 104729) % 20011) / 20011.0


def iv(i):
    return (i * 2654435761) % 2 ** 32


def P(node, name, kind, vals):
    node.CreateProperty(name, kind).values = list(vals)


# ---- synthetic trees ------------------------------------------------------------------------------------------

def build_types():
    fresh()
    c = castlib.Cast()
    r = c.CreateRoot()
    n = r.CreateChild(castlib.CastNode(0x65707974))  # 'type', unknown to castlib
    P(n, "b1", "b", [0, 1, 127, 128, 255])
    P(n, "h1", "h", [0, 1, 0x7FFF, 0x8000, 0xFFFF])
    P(n, "i1", "i", [0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF])
    P(n, "l1", "l", [0, 1, 2 ** 63 - 1, 2 ** 63, 2 ** 64 - 1])
    P(n, "f1", "f", [0.0, -0.0, 1.0, -1.5, 0.1, 1e-45, 3.4028234663852886e38, float("inf"), float("-inf"), NAN_F,
                     16777217.0, 1.0000000596046448])
    P(n, "d1", "d", [0.0, -0.0, 0.1, 1e308, 5e-324, float("inf"), NAN_F, -2.5])
    P(n, "s1", "s", ["plain"])
    P(n, "s2", "s", [""])
    P(n, "s3", "s", ["héllo wörld — ✓ 日本語 \U0001F389"])
    P(n, "v2", "2v", [fv(i, 1) for i in range(10)])
    P(n, "v3", "3v", [fv(i, 2) for i in range(12)])
    P(n, "v4", "4v", [fv(i, 3) for i in range(16)])
    for t in ("b", "h", "i", "l", "f", "d", "2v", "3v", "4v"):
        P(n, "empty_" + t, t, [])
    P(n, "bp_ñame ✓", "i", [7])
    P(n, "", "b", [1])
    P(n, "long_" + "x" * 300, "h", [1, 2, 3])
    m = r.CreateMetadata()
    m.SetAuthor("Ångström")
    m.SetSoftware("castport synth")
    m.SetUpAxis("y")
    m.SetSceneRoot("scenes/üñí")
    return c


def build_model():
    fresh()
    c = castlib.Cast()
    r = c.CreateRoot()
    meta = r.CreateMetadata()
    meta.SetUpAxis("y")
    meta.SetSoftware("nightrunner castport")
    mdl = r.CreateModel()
    mdl.SetName("synth_model")
    mdl.SetPosition((1.0, 2.0, 3.0))
    mdl.SetRotation((0.0, 0.0, 0.0, 1.0))
    mdl.SetScale((1.0, 1.0, 1.0))
    skel = mdl.CreateSkeleton()
    bones = []
    for i in range(300):
        b = skel.CreateBone()
        b.SetName("bone_%03d" % i)
        b.SetParentIndex(-1 if i % 100 == 0 else (i * 7 + 3) % i)
        b.SetSegmentScaleCompensate(i % 2 == 0)
        b.SetLocalPosition((fv(i, 1), fv(i, 2), fv(i, 3)))
        b.SetLocalRotation((uv(i, 4), uv(i, 5), uv(i, 6), uv(i, 7)))
        if i % 7 != 0:
            b.SetWorldPosition([fv(i, 8), fv(i, 9), fv(i, 10)])
            b.SetWorldRotation([uv(i, 11), uv(i, 12), uv(i, 13), uv(i, 14)])
        b.SetScale((1.0, 1.0, 1.0))
        P(b, "bp_entity", "i", [i])
        P(b, "bp_flags", "i", [iv(i)])
        bones.append(b)
    ik = skel.CreateIKHandle()
    ik.SetName("ik_leg")
    ik.SetStartBone(bones[1].Hash())
    ik.SetEndBone(bones[3].Hash())
    ik.SetTargetBone(bones[5].Hash())
    ik.SetTargetOffset((0.5, -0.25, 0.125))
    ik.SetPoleVectorBone(bones[7].Hash())
    ik.SetPoleBone(bones[9].Hash())
    ik.SetUseTargetRotation(True)
    ik2 = skel.CreateIKHandle()
    ik2.SetName("ik_min")
    ik2.SetUseTargetRotation(False)
    cn = skel.CreateConstraint()
    cn.SetName("pt")
    cn.SetConstraintType("pt")
    cn.SetConstraintBone(bones[10].Hash())
    cn.SetTargetBone(bones[11].Hash())
    cn.SetMaintainOffset(True)
    cn.SetCustomOffset((1.0, 2.0, 3.0))
    cn.SetWeight(0.75)
    cn.SetSkipX(True)
    cn.SetSkipY(False)
    cn.SetSkipZ(True)
    cn2 = skel.CreateConstraint()
    cn2.SetName("or")
    cn2.SetConstraintType("or")
    cn2.SetCustomOffset((0.0, 0.0, 0.0, 1.0))
    cn2.SetMaintainOffset(False)
    cn3 = skel.CreateConstraint()
    cn3.SetName("sc")
    cn3.SetCustomOffset((1.0, 2.0))  # neither 3 nor 4 values: castlib writes nothing

    m1 = mdl.CreateMaterial()
    m1.SetName("mat_a.mat")
    m1.SetType("pbr")
    f1 = m1.CreateFile()
    f1.SetPath("textures/a_dif.png")
    m1.SetSlot("albedo", f1.Hash())
    f2 = m1.CreateFile()
    f2.SetPath("textures/ä_nrm.png")
    m1.SetSlot("normal", f2.Hash())
    P(m1, "bp_material_slot", "i", [0])
    col = m1.CreateChild(castlib.Color())
    col.SetName("tint")
    col.SetColorSpace("linear")
    col.SetRgba((0.25, 0.5, 0.75, 1.0))
    m1.SetSlot("tint", col.Hash())
    m2 = mdl.CreateMaterial()
    m2.SetName("mat_b")

    big = mdl.CreateMesh()
    big.SetName("big")
    big.SetMaterial(m1.Hash())
    big.SetUVLayerCount(2)
    big.SetColorLayerCount(2)
    n = 70000
    big.SetVertexPositionBuffer([(fv(i, 1), fv(i, 2), fv(i, 3)) for i in range(n)])
    big.SetVertexNormalBuffer([(uv(i, 4), uv(i, 5), uv(i, 6)) for i in range(n)])
    big.SetVertexTangentBuffer([(uv(i, 7), uv(i, 8), uv(i, 9)) for i in range(n)])
    big.SetVertexUVLayerBuffer(0, [(uv(i, 10), uv(i, 11)) for i in range(n)])
    big.SetVertexUVLayerBuffer(1, [(uv(i, 12), uv(i, 13)) for i in range(n)])
    big.SetVertexColorBuffer(0, [iv(i) for i in range(n)])
    big.SetVertexColorBuffer(1, [(uv(i, 14), uv(i, 15), uv(i, 16), 1.0) for i in range(n)])
    big.SetFaceBuffer([i + j for i in range(n - 2) for j in range(3)])
    big.SetMaximumWeightInfluence(4)
    big.SetSkinningMethod("linear")
    big.SetVertexWeightBoneBuffer([(i * 4 + j) % 300 for i in range(n) for j in range(4)])
    big.SetVertexWeightValueBuffer([uv(i * 4 + j, 17) for i in range(n) for j in range(4)])
    P(big, "bp_vertex_id", "i", range(n))
    P(big, "bp_tangent_sign", "f", [1.0 if i % 3 else -1.0 for i in range(n)])
    P(big, "bp_raw_00", "s", ["00112233445566778899aabbccddeeff"])
    P(big, "bp_format", "b", [3])

    small = mdl.CreateMesh()
    small.SetName("small_b")
    small.SetMaterial(m2.Hash())
    small.SetUVLayerCount(1)
    small.SetVertexPositionBuffer([(fv(i, 1), fv(i, 2), fv(i, 3)) for i in range(10)])
    small.SetVertexUVLayerBuffer(0, [(uv(i, 1), uv(i, 2)) for i in range(10)])
    small.SetFaceBuffer([0, 1, 2, 2, 3, 9])
    small.SetMaximumWeightInfluence(1)
    small.SetSkinningMethod("quaternion")
    small.SetVertexWeightBoneBuffer([i * 25 for i in range(10)])
    small.SetVertexWeightValueBuffer([1.0] * 10)

    med = mdl.CreateMesh()
    med.SetName("medium_h")
    med.SetVertexPositionBuffer([(fv(i, 3), fv(i, 4), fv(i, 5)) for i in range(1000)])
    med.SetFaceBuffer([(i * 3 + j) % 1000 for i in range(900) for j in range(3)])
    med.SetVertexColorBuffer(0, [(uv(i, 1), uv(i, 2), uv(i, 3), uv(i, 4)) for i in range(1000)])

    empty = mdl.CreateMesh()
    empty.SetName("empty")
    empty.SetVertexColorBuffer(0, [])  # no first element to test: castlib picks 4v
    P(empty, "vp", "3v", [])
    P(empty, "f", "i", [])

    legacy = mdl.CreateMesh()
    legacy.SetName("legacy_vc")
    P(legacy, "vc", "i", [0xFF00FF00, 0x80808080])

    hair = mdl.CreateHair()
    hair.SetName("hair")
    hair.SetSegmentBuffer([3, 4, 5])
    hair.SetParticleBuffer([(fv(i, 1), fv(i, 2), fv(i, 3)) for i in range(15)])
    hair.SetMaterial(m2.Hash())
    hair2 = mdl.CreateHair()
    hair2.SetName("hair_i")
    hair2.SetSegmentBuffer([1, 70000])

    bs = mdl.CreateBlendShape()
    bs.SetName("smile")
    bs.SetBaseShape(small.Hash())
    bs.SetTargetShapeVertexIndices([0, 2, 4])
    bs.SetTargetShapeVertexPositions([(fv(i, 6), fv(i, 7), fv(i, 8)) for i in range(3)])
    bs.SetTargetWeightScale(1.5)
    bs2 = mdl.CreateBlendShape()
    bs2.SetName("frown")
    bs2.SetBaseShape(med.Hash())
    bs2.SetTargetShapeVertexIndices([0, 300, 999])
    c.CreateRoot()
    return c


def build_anim():
    fresh()
    c = castlib.Cast()
    r = c.CreateRoot()
    a = r.CreateAnimation()
    a.SetName("walk")
    a.SetFramerate(30.0)
    a.SetLooping(True)
    sk = a.CreateSkeleton()
    b0 = sk.CreateBone()
    b0.SetName("root")
    b0.SetParentIndex(-1)
    cv = a.CreateCurve()
    cv.SetNodeName("root")
    cv.SetKeyPropertyName("tx")
    cv.SetKeyFrameBuffer(list(range(200)))
    cv.SetFloatKeyValueBuffer([fv(i) for i in range(200)])
    cv.SetMode("absolute")
    cv.SetAdditiveBlendWeight(0.5)
    cv2 = a.CreateCurve()
    cv2.SetNodeName("root")
    cv2.SetKeyPropertyName("rq")
    cv2.SetKeyFrameBuffer(list(range(0, 3000, 3)))
    cv2.SetVec4KeyValueBuffer([(uv(i, 1), uv(i, 2), uv(i, 3), uv(i, 4)) for i in range(1000)])
    cv2.SetMode("relative")
    cv3 = a.CreateCurve()
    cv3.SetNodeName("spine")
    cv3.SetKeyPropertyName("vis")
    cv3.SetKeyFrameBuffer([0, 70000])
    cv3.SetByteKeyValueBuffer([1, 0])
    cv3.SetMode("additive")
    cmo = a.CreateCurveModeOverride()
    cmo.SetNodeName("spine")
    cmo.SetMode("additive")
    cmo.SetOverrideTranslationCurves(True)
    cmo.SetOverrideRotationCurves(False)
    cmo.SetOverrideScaleCurves(True)
    nt = a.CreateNotification()
    nt.SetName("footstep")
    nt.SetKeyFrameBuffer([5, 25, 45])
    a2 = r.CreateAnimation()
    a2.SetName("idle")
    a2.SetLooping(False)
    return c


def build_misc():
    fresh()
    c = castlib.Cast()
    r1 = c.CreateRoot()
    inst = r1.CreateInstance()
    inst.SetName("inst")
    f = inst.CreateChild(castlib.File())
    f.SetPath("other.cast")
    inst.SetReferenceFile(f.Hash())
    inst.SetPosition((1.0, -2.0, 3.5))
    inst.SetRotation((0.0, 0.70710678, 0.0, 0.70710678))
    inst.SetScale((2.0, 2.0, 2.0))
    unk = r1.CreateChild(castlib.CastNode(0x6B6E7578))
    P(unk, "a", "b", [1])
    P(unk, "b", "h", [2])
    P(unk, "a", "i", [3])  # overwrite keeps the first position, takes the new type
    u2 = unk.CreateChild(castlib.CastNode(0))
    P(u2, "z", "d", [1.25])
    u2.CreateChild(castlib.Mesh()).SetName("under_unknown")
    unk.CreateChild(castlib.CastNode(0xFFFFFFFF))
    m = r1.CreateModel()
    m.SetName("first")
    m.SetScale((2.0, 2.0, 2.0))
    m.SetName("second")
    m.properties.pop("s")
    m.SetScale((3.0, 3.0, 3.0))
    me1 = m.CreateMesh()
    me1.SetName("removed")
    me2 = m.CreateMesh()
    me2.SetName("kept")
    m.childNodes.remove(me1)
    c.CreateRoot()
    r3 = c.CreateRoot()
    r3.CreateMetadata().SetUpAxis("z")
    return c


def build_extended(anim_path):
    fresh()
    c = castlib.Cast.load(anim_path)
    r = c.Roots()[0]
    m = r.CreateModel()
    m.SetName("added")
    m.CreateSkeleton().CreateBone().SetName("b")
    c.CreateRoot()
    return c


# ---- hand-made byte streams (castlib cannot write these) -----------------------------------------------------

def rprop(tid, name, count, payload):
    nb = name.encode("utf-8") if isinstance(name, str) else name
    return struct.pack("<2sHI", tid, len(nb), count) + nb + payload


def rnode(ident, hsh, props, children, length=None, pcount=None, ccount=None):
    body = b"".join(props) + b"".join(children)
    ln = 0x18 + len(body) if length is None else length
    return struct.pack("<IIQII", ident, ln, hsh, len(props) if pcount is None else pcount,
                       len(children) if ccount is None else ccount) + body


def rfile(roots, reserved=0, trailing=b""):
    return struct.pack("<4I", 0x74736163, 1, len(roots), reserved) + b"".join(roots) + trailing


def s0(text):
    return text.encode("utf-8") + b"\0"


def edge_files():
    out = {}
    root = 0x746F6F72
    out["edge_header"] = rfile([rnode(root, 5, [rprop(b"s\0", "n", 1, s0("r"))], [])], reserved=0xDEADBEEF,
                               trailing=b"\x01\x02\x03\x04\x05\x06\x07")
    out["edge_length"] = rfile([rnode(root, 6, [], [rnode(0x6C646F6D, 7, [rprop(b"b\0", "x", 1, b"\x09")], [],
                                                          length=0)], length=0xFFFFFFFF)])
    out["edge_duplicates"] = rfile([rnode(root, 8, [rprop(b"s\0", "n", 1, s0("one")), rprop(b"b\0", "x", 1, b"\x01"),
                                                    rprop(b"h\0", "y", 1, b"\x02\x00"), rprop(b"s\0", "n", 1, s0("two")),
                                                    rprop(b"i\0", "x", 2, b"\x03\0\0\0\x04\0\0\0")], [])])
    out["edge_strings"] = rfile([rnode(root, 9, [rprop(b"s\0", "zero", 0, s0("counted zero")),
                                                 rprop(b"s\0", "three", 3, s0("counted three")),
                                                 rprop(b"s\0", "after", 1, s0("é"))], [])])
    out["edge_typeids"] = rfile([rnode(root, 10, [rprop(b"\0b", "lead", 1, b"\x05"), rprop(b"f\0", "trail", 1,
                                                                                         struct.pack("<f", 2.5)),
                                                  rprop(b"2v", "v", 1, struct.pack("<2f", 1, 2))], [])])
    out["edge_nodes"] = rfile([
        rnode(0x6873656D, 0xFFFFFFFFFFFFFFFF, [rprop(b"s\0", "n", 1, s0("mesh at root"))], []),
        rnode(0, 0, [], [rnode(0x64636261, 0x0123456789ABCDEF, [], [rnode(0xFFFFFFFF, 1, [], [])])]),
        rnode(root, 11, [], [rnode(0x656E6F62, 12, [rprop(b"i\0", "p", 1, b"\xff\xff\xff\xff")], [])]),
    ])
    return out


def unknown_prop_files():
    root = 0x746F6F72
    out = {}
    # unknown type 'zz' (castlib has no size for it) in a child node followed by a sibling with a known type
    bad = rnode(0x6C646F6D, 21, [rprop(b"s\0", "n", 1, s0("before")), rprop(b"zz", "odd", 3, b"\xAA" * 7),
                                 rprop(b"b\0", "after", 1, b"\x01")], [rnode(0x6873656D, 22, [], [])])
    out["unknown_prop"] = rfile([rnode(root, 20, [], [bad, rnode(0x6C646F6D, 23, [rprop(b"b\0", "ok", 1, b"\x02")],
                                                                 [])])])
    out["unknown_prop_root"] = rfile([rnode(root, 30, [rprop(b"\xff\xfe", "raw", 1, b"\x00" * 5)], []),
                                      rnode(root, 31, [], [])])
    return out


def sha(b):
    return hashlib.sha256(b).hexdigest()


def main():
    manifest = {"files": {}, "notes": {}}

    def save(name, cast):
        p = os.path.join(HERE, name + ".cast")
        cast.save(p)
        with open(p, "rb") as f:
            data = f.read()
        manifest["files"][name] = {"sha256": sha(data), "size": len(data), "next_hash": castlib.castHashBase - BASE}
        return p

    save("synth_types", build_types())
    save("synth_model", build_model())
    anim = save("synth_anim", build_anim())
    save("synth_misc", build_misc())
    save("synth_extended", build_extended(anim))

    for name, data in edge_files().items():
        p = os.path.join(HERE, name + ".cast")
        with open(p, "wb") as f:
            f.write(data)
        fresh()
        c = castlib.Cast.load(p)
        q = os.path.join(HERE, name + ".resaved.cast")
        c.save(q)
        with open(q, "rb") as f:
            rs = f.read()
        manifest["files"][name] = {"sha256": sha(data), "size": len(data)}
        manifest["files"][name + ".resaved"] = {"sha256": sha(rs), "size": len(rs)}

    for name, data in unknown_prop_files().items():
        p = os.path.join(HERE, name + ".cast")
        with open(p, "wb") as f:
            f.write(data)
        manifest["files"][name] = {"sha256": sha(data), "size": len(data)}
        try:
            castlib.Cast.load(p)
            manifest["notes"][name] = "loaded"
        except Exception as e:  # noqa: BLE001
            manifest["notes"][name] = "%s: %s" % (type(e).__name__, e)

    # castlib behaviours worth recording
    try:
        prop = castlib.CastProperty(name="bp_owner_entity", type="i")
        prop.values = [-1]
        prop.save(io.BytesIO())
        manifest["notes"]["pack_i_minus_one"] = "packed"
    except Exception as e:  # noqa: BLE001
        manifest["notes"]["pack_i_minus_one"] = "%s: %s" % (type(e).__name__, e)
    try:
        castlib.castTypeForMaximum([])
        manifest["notes"]["type_for_maximum_empty"] = "ok"
    except Exception as e:  # noqa: BLE001
        manifest["notes"]["type_for_maximum_empty"] = "%s: %s" % (type(e).__name__, e)
    fresh()
    c = castlib.Cast()
    c.CreateRoot().CreateModel().SetName("a\0b")
    buf = os.path.join(HERE, "nul_in_string.cast")
    c.save(buf)
    try:
        back = castlib.Cast.load(buf)
        manifest["notes"]["nul_in_string"] = "reads back as %r, %d root nodes" % (
            back.Roots()[0].ChildrenOfType(castlib.Model)[0].Name() if back.Roots() else None, len(back.Roots()))
    except Exception as e:  # noqa: BLE001
        manifest["notes"]["nul_in_string"] = "%s: %s" % (type(e).__name__, e)
    # an unterminated string at end of file: CastString_t.load loops on read(1) == b'' forever
    trunc = rfile([rnode(0x746F6F72, 1, [rprop(b"s\0", "n", 1, b"abc")], [])])
    tp = os.path.join(HERE, "unterminated.cast")
    with open(tp, "wb") as f:
        f.write(trunc)
    code = ("import importlib.util,sys;s=importlib.util.spec_from_file_location('c',sys.argv[1]);"
            "m=importlib.util.module_from_spec(s);s.loader.exec_module(m);m.Cast.load(sys.argv[2]);print('returned')")
    try:
        r = subprocess.run([sys.executable, "-c", code, os.path.join(PROTO, "nightrunner", "cast", "castlib.py"), tp],
                           capture_output=True, text=True, timeout=5, env=dict(os.environ, PYTHONDONTWRITEBYTECODE="1"))
        manifest["notes"]["unterminated_string"] = "exit %d %s %s" % (r.returncode, r.stdout.strip(),
                                                                       r.stderr.strip()[-200:])
    except subprocess.TimeoutExpired:
        manifest["notes"]["unterminated_string"] = "timeout after 5 s (no progress at EOF)"
    manifest["notes"]["linear_to_srgb_0.5"] = castlib.CastColor.linearToSRGB(0.5)
    manifest["notes"]["srgb_to_linear_0.5"] = castlib.CastColor.sRGBToLinear(0.5)
    manifest["notes"]["to_integer"] = castlib.CastColor.toInteger((0.1, 0.5, 1.2, -0.3))
    manifest["notes"]["from_integer"] = castlib.CastColor.fromInteger(0x80FF4001)

    with open(os.path.join(HERE, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)
    print(json.dumps(manifest, indent=1))


if __name__ == "__main__":
    main()
