"""Reference files for the C# glTF port (Nightrunner.Tests/GltfTests.cs).

Usage: python gltf_fixtures.py <out folder> [--no-real], run from nightrunner-main (or with PYTHONPATH=<nightrunner-main>)
and PYTHONDONTWRITEBYTECODE=1; writes only into <out folder>. Without --no-real it also exports shipped meshes of the
detected installs under <out folder>/real: that is game data, never to be committed."""
import base64
import hashlib
import json
import math
import struct
import sys
import traceback
from pathlib import Path

import numpy as np

from nightrunner.cast import castlib
from nightrunner.cast.gltf import load_scene, save_scene

OUT = Path(sys.argv[1])
BASE = 0x534E495752545250
manifest = {}


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def fresh():
    castlib.castHashBase = BASE


# ---- synthetic scenes (Nightrunner.Tests/GltfTests.cs GltfSynth builds the same, call for call) -------------------

def tex_bytes(seed: int, n: int) -> bytes:
    return bytes((i * 31 + seed) & 0xFF for i in range(n))


def synth_skinned():
    c = castlib.Cast()
    root = c.CreateRoot()
    meta = root.CreateMetadata()
    meta.SetSoftware("nightrunner test")
    meta.SetUpAxis("y")
    mdl = root.CreateModel()
    mdl.SetName("synth")
    sk = mdl.CreateSkeleton()

    def bone(name, parent, lp, lr):
        b = sk.CreateBone()
        if name is not None:
            b.SetName(name)
        b.SetParentIndex(parent)
        if lp is not None:
            b.SetLocalPosition(lp)
        if lr is not None:
            b.SetLocalRotation(lr)
        return b

    b = bone("root", -1, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0))
    b.CreateProperty("bp_entity", "i").values = [0]
    b = bone("spine", 0, (0.0, 1.25, 0.5), (0.0, 0.7071067690849304, 0.0, 0.7071067690849304))
    b.CreateProperty("bp_flags", "i").values = [3]
    b.CreateProperty("bp_note", "s").values = ["x"]
    bone("arm.l", 1, (0.5, 0.25, -0.125), (0.5, 0.5, 0.5, 0.5))
    bone(None, 7, None, None)
    bone("tip", 2, (0.1, 0.2, 0.3), (0.1, 0.2, 0.3, 0.9))

    def material(name, slots, props):
        m = mdl.CreateMaterial()
        if name is not None:
            m.SetName(name)
        m.SetType("pbr")
        for slot, path in slots:
            f = m.CreateFile()
            f.SetPath(path)
            m.SetSlot(slot, f.Hash())
        for k, t, v in props:
            m.CreateProperty(k, t).values = v
        return m

    m0 = material("body.mat", [("albedo", "tex/body_dif.png"), ("normal", "tex/body nrm.png")],
                  [("bp_alpha_mode", "s", ["mask"]), ("bp_material_slot", "i", [0])])
    m1 = material("glass.mat", [("diffuse", "tex/glass.jpg")],
                  [("bp_alpha_mode", "s", ["blend"]), ("bp_values", "f", [0.5, 0.25])])
    material("missing.mat", [("albedo", "tex/missing.png")], [])
    material(None, [("albedo", "tex/body_dif.png")], [])

    inf, nan = float("inf"), float("nan")
    me = mdl.CreateMesh()
    me.SetName("quad")
    me.SetVertexPositionBuffer([(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.5), (1.0, 1.0, -0.5)])
    me.SetVertexNormalBuffer([(0.0, 0.0, 1.0), (0.0, 2.0, 0.0), (0.6, 0.8, 0.0), (0.0, 0.0, -1.0)])
    me.SetVertexTangentBuffer([(1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.3, 0.3, 0.3), (1.0, 0.0, 0.0)])
    me.CreateProperty("bp_tangent_sign", "f").values = [1.0, -1.0, 1.0, -1.0]
    me.SetUVLayerCount(2)
    me.SetVertexUVLayerBuffer(0, [(0.0, 0.0), (inf, 0.5), (0.25, nan), (1.0, 1.0)])
    me.SetVertexUVLayerBuffer(1, [(0.5, 0.5), (0.5, 0.5), (0.5, 0.5), (0.5, 0.5)])
    me.SetMaximumWeightInfluence(4)
    me.SetSkinningMethod("linear")
    me.SetVertexWeightBoneBuffer([0, 1, 0, 0, 1, 2, 4, 0, 2, 0, 0, 0, 3, 1, 2, 4])
    me.SetVertexWeightValueBuffer([1.0, 0.0, 0.0, 0.0, 0.5, 0.25, 0.25, 0.0, 0.5, 0.0, 0.0, 0.0,
                                   0.25, 0.25, 0.25, 0.125])
    me.SetFaceBuffer([0, 1, 2, 2, 1, 3])
    me.SetMaterial(m0.Hash())
    me.CreateProperty("bp_format", "b").values = [3]
    me.CreateProperty("bp_raw_00", "s").values = ["00ff"]
    me.CreateProperty("bp_vertex_id", "i").values = [10, 11, 12, 13]
    me.CreateProperty("bp_h", "h").values = [7, 8]
    me.CreateProperty("bp_l", "l").values = [4000000000]
    me.CreateProperty("bp_d", "d").values = [0.1]
    me.CreateProperty("bp_v", "3v").values = [1.0, 2.0, 3.0]
    me.CreateProperty("bp_empty", "i").values = []

    me = mdl.CreateMesh()
    me.SetName("six")
    me.SetVertexPositionBuffer([(0.0, 0.0, 0.0), (2.0, 0.0, 0.0), (0.0, 2.0, 0.0)])
    me.SetMaximumWeightInfluence(6)
    me.SetSkinningMethod("linear")
    me.SetVertexWeightBoneBuffer([0, 1, 2, 3, 4, 1,  4, 3, 2, 1, 0, 2,  1, 2, 3, 4, 0, 0])
    me.SetVertexWeightValueBuffer([0.1, 0.2, 0.2, 0.2, 0.2, 0.1,  0.5, 0.0, 0.25, 0.25, 0.0, 0.0,
                                   0.0, 0.0, 0.0, 0.0, 0.0, 0.0])
    me.SetFaceBuffer([0, 1, 2])
    me.SetMaterial(m1.Hash())

    me = mdl.CreateMesh()
    me.SetName("two")
    me.SetVertexPositionBuffer([(0.0, 0.0, 1.0), (2.0, 0.0, 1.0), (0.0, 2.0, 1.0)])
    me.SetMaximumWeightInfluence(2)
    me.SetVertexWeightBoneBuffer([1, 2, 2, 0, 3, 3])
    me.SetVertexWeightValueBuffer([0.75, 0.25, 1.0, 0.0, 0.3, 0.3])
    me.SetFaceBuffer([2, 1, 0])
    me.SetMaterial(12345)

    me = mdl.CreateMesh()
    me.SetName("empty")

    me = mdl.CreateMesh()
    me.SetVertexPositionBuffer([(-1.0, -2.0, -3.0), (4.0, 5.0, 6.0), (0.5, 0.5, 0.5)])
    me.SetFaceBuffer([0, 1, 2])
    return c


def synth_bones300():
    c = castlib.Cast()
    root = c.CreateRoot()
    mdl = root.CreateModel()
    mdl.SetName("chain")
    sk = mdl.CreateSkeleton()
    for i in range(300):
        b = sk.CreateBone()
        b.SetName(f"b{i}")
        b.SetParentIndex(i - 1)
        b.SetLocalPosition((0.0, 0.5, 0.0))
        b.SetLocalRotation((0.0, 0.0, 0.0, 1.0))
    me = mdl.CreateMesh()
    me.SetName("strip")
    me.SetVertexPositionBuffer([(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 149.5, 0.0)])
    me.SetMaximumWeightInfluence(4)
    me.SetVertexWeightBoneBuffer([299, 150, 0, 0, 0, 0, 0, 0, 256, 257, 0, 0])
    me.SetVertexWeightValueBuffer([0.5, 0.5, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.25, 0.25, 0.0, 0.0])
    me.SetFaceBuffer([0, 1, 2])
    return c


def synth_static():
    c = castlib.Cast()
    root = c.CreateRoot()
    mdl = root.CreateModel()
    me = mdl.CreateMesh()
    me.SetName("static")
    me.SetVertexPositionBuffer([(0.0, 0.0, 0.0), (3.0, 0.0, 0.0), (0.0, 3.0, 0.0), (3.0, 3.0, 0.0)])
    me.SetVertexNormalBuffer([(0.0, 0.0, 1.0)] * 4)
    me.SetUVLayerCount(1)
    me.SetVertexUVLayerBuffer(0, [(0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)])
    me.SetMaximumWeightInfluence(4)
    me.SetVertexWeightBoneBuffer([0] * 16)
    me.SetVertexWeightValueBuffer([1.0, 0.0, 0.0, 0.0] * 4)
    me.SetFaceBuffer([0, 1, 2, 2, 1, 3])
    me.CreateProperty("bp_note", "s").values = ["café \"q\" \\ ☃"]
    return c


def write_synth():
    d = OUT / "synth"
    (d / "tex").mkdir(parents=True, exist_ok=True)
    (d / "tex" / "body_dif.png").write_bytes(tex_bytes(1, 67))
    (d / "tex" / "body nrm.png").write_bytes(tex_bytes(2, 30))
    (d / "tex" / "glass.jpg").write_bytes(tex_bytes(3, 45))
    for name, fn in (("synth_skinned", synth_skinned), ("synth_bones300", synth_bones300), ("synth_static", synth_static)):
        fresh()
        c = fn()
        c.save(str(d / f"{name}.cast"))
        manifest[f"synth/{name}.cast"] = sha(d / f"{name}.cast")
        c = castlib.Cast.load(str(d / f"{name}.cast"))      # float32 values, as the C# tree holds them
        for ext in ("glb", "gltf"):
            p = d / f"{name}.{ext}"
            save_scene(c, p)
            manifest[f"synth/{name}.{ext}"] = sha(p)
            if ext == "gltf":
                manifest[f"synth/{name}.bin"] = sha(d / f"{name}.bin")


# ---- real meshes ---------------------------------------------------------------------------------------------------

def write_real():
    from nightrunner import games
    from nightrunner.cast.export import build_cast
    from nightrunner.container.rp6l import Pack
    from nightrunner.mesh.decode import decode_resource
    d = OUT / "real"
    d.mkdir(parents=True, exist_ok=True)
    installs = games.find_installs()
    wanted = {"dltb": ["wn_pistol_b_b", "sh_npc_ft_crane_hair_a", "player_kc_basic_torso_a_tpp", "veh_sedan_a"],
              "dl2": ["dummy_box"]}
    per_pack = {"dltb": 3, "dl2": 2}
    for gid, inst in installs.items():
        names = list(wanted.get(gid, []))
        got = []
        for rp in sorted(inst.rpacks()):
            try:
                pk = Pack.open(rp)
            except Exception:
                continue
            with pk:
                meshes = list(pk.resources_of_type(0x10))
                picks = [r for r in meshes if r.name in names]
                step = max(1, len(meshes) // per_pack[gid]) if meshes else 1
                if len(got) < 36:
                    picks += meshes[::step][:per_pack[gid]]
                for r in picks:
                    if r.name in got:
                        continue
                    try:
                        m = decode_resource(pk.resource(r.index))
                        cast, _ = build_cast(m)
                        fresh()
                        p = d / f"{gid}_{r.name}.cast"
                        cast.save(str(p))
                    except Exception as e:
                        print("skip", gid, r.name, type(e).__name__, e)
                        continue
                    got.append(r.name)
            if len(got) >= 36 and all(n in got for n in names):
                break
        print(gid, len(got), "meshes")
    for p in sorted(d.glob("*.cast")):
        if p.name.endswith(".read.cast"):
            continue
        c = castlib.Cast.load(str(p))
        manifest[f"real/{p.name}"] = sha(p)
        for ext in ("glb", "gltf"):
            q = p.with_suffix("." + ext)
            save_scene(c, q)
            manifest[f"real/{q.name}"] = sha(q)
            if ext == "gltf":
                manifest[f"real/{q.stem}.bin"] = sha(q.with_suffix(".bin"))


# ---- a foreign glTF for the reader --------------------------------------------------------------------------------

def write_foreign():
    d = OUT / "foreign"
    d.mkdir(parents=True, exist_ok=True)
    blob = bytearray()

    def add(arr, align=4):
        while len(blob) % align:
            blob.append(0)
        off = len(blob)
        blob.extend(arr.tobytes())
        return off

    pos = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [1, 1, 0]], dtype=np.float32)
    nrm = np.array([[0, 0, 1]] * 4, dtype=np.float32)
    tan = np.array([[1, 0, 0, -1], [1, 0, 0, 1], [1, 0, 0, -1], [1, 0, 0, 1]], dtype=np.float32)
    uvn = np.array([[0, 0], [255, 0], [0, 255], [128, 64]], dtype=np.uint8)
    idx16 = np.array([0, 1, 2, 2, 1, 3], dtype=np.uint16)
    j0 = np.array([[0, 1, 0, 0]] * 4, dtype=np.uint8)
    w0 = np.array([[0.5, 0.5, 0, 0]] * 4, dtype=np.float32)
    j1 = np.array([[1, 0, 0, 0]] * 4, dtype=np.uint16)
    w1 = np.array([[0.0, 0, 0, 0]] * 4, dtype=np.float32)
    vid = np.array([5, 6, 7, 8.5], dtype=np.float32)
    inter = np.zeros(4, dtype=[("p", np.float32, 3), ("u", np.float32, 2)])
    inter["p"] = pos * 2
    inter["u"] = [[0.25, 0.5]] * 4
    views, accs = [], []

    def acc(arr, ctype, kind, **kw):
        off = add(arr)
        views.append({"buffer": 0, "byteOffset": off, "byteLength": arr.nbytes})
        a = {"bufferView": len(views) - 1, "componentType": ctype, "count": len(arr), "type": kind}
        a.update(kw)
        accs.append(a)
        return len(accs) - 1

    a_pos = acc(pos, 5126, "VEC3", min=[0, 0, 0], max=[1, 1, 0])
    a_nrm = acc(nrm, 5126, "VEC3")
    a_tan = acc(tan, 5126, "VEC4")
    a_uvn = acc(uvn, 5121, "VEC2", normalized=True)
    a_idx = acc(idx16, 5123, "SCALAR")
    a_j0 = acc(j0, 5121, "VEC4")
    a_w0 = acc(w0, 5126, "VEC4")
    a_j1 = acc(j1, 5123, "VEC4")
    a_w1 = acc(w1, 5126, "VEC4")
    a_vid = acc(vid, 5126, "SCALAR")
    off = add(inter)
    views.append({"buffer": 0, "byteOffset": off, "byteLength": inter.nbytes, "byteStride": 20})
    iv = len(views) - 1
    accs.append({"bufferView": iv, "componentType": 5126, "count": 4, "type": "VEC3"})
    a_ipos = len(accs) - 1
    accs.append({"bufferView": iv, "byteOffset": 12, "componentType": 5126, "count": 4, "type": "VEC2"})
    a_iuv = len(accs) - 1
    accs.append({"componentType": 5126, "count": 4, "type": "VEC2"})
    a_zero = len(accs) - 1
    ibm = np.stack([np.eye(4)] * 3).astype(np.float32)
    a_ibm = acc(ibm.reshape(-1, 16), 5126, "MAT4")
    doc = {
        "asset": {"version": "2.0", "generator": 42},
        "scene": 0,
        "scenes": [{"nodes": [0, 6]}],
        "nodes": [
            {"name": "Armature", "translation": [0, 0, 1], "rotation": [0, 0.7071068, 0, 0.7071068],
             "scale": [2, 2, 2], "children": [1]},
            {"name": "hips", "translation": [0, 1, 0], "children": [2, 3],
             "extras": {"bp_entity": 4, "bp_flag": True, "bp_list": [1, 2.5], "bp_obj": {"a": [1, None]},
                        "other": 1, "bp_s": "text", "bp_bools": [True, False], "bp_f": 0.1}},
            {"matrix": [1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0.5, 0.25, 0, 1]},
            {"name": "hand", "rotation": [0, 0, 0.3826834, 0.9238795], "scale": [1, 3, 1]},
            {"name": "body", "mesh": 0, "skin": 0, "extras": {"bp_node": "n"}},
            {"name": "body2", "mesh": 0, "skin": 1},
            {"name": "prop", "translation": [5, 0, 0], "rotation": [0, 0, 0.7071068, 0.7071068], "scale": [1, 2, 1],
             "children": [7]},
            {"mesh": 1, "translation": [0, 1, 0]},
        ],
        "skins": [{"joints": [1, 2, 3], "inverseBindMatrices": a_ibm},
                  {"joints": [3, 1, 99 if False else 2]}],
        "meshes": [
            {"name": "bodymesh", "extras": {"bp_format": 3, "bp_vertex_id": 1, "bp_nonfinite_uv":
                                            {"0": {"rows": [1, 3], "hex": struct.pack("<4f", math.inf, 0.0, 0.5, math.nan).hex()}}},
             "primitives": [
                 {"attributes": {"POSITION": a_pos, "NORMAL": a_nrm, "TANGENT": a_tan, "TEXCOORD_0": a_uvn,
                                 "TEXCOORD_1": a_zero, "JOINTS_0": a_j0, "WEIGHTS_0": a_w0, "JOINTS_1": a_j1,
                                 "WEIGHTS_1": a_w1, "_BP_VERTEX_ID": a_vid},
                  "indices": a_idx, "material": 1},
                 {"attributes": {"POSITION": a_pos}, "mode": 1},
                 {"attributes": {"POSITION": a_ipos, "TEXCOORD_0": a_iuv}, "material": 5},
             ]},
            {"primitives": [{"attributes": {"POSITION": a_pos, "NORMAL": a_nrm, "TANGENT": a_tan}, "indices": a_idx}]},
        ],
        "materials": [{"name": "m0", "extras": {"bp_alpha_mode": "mask"}}, {"extras": {"bp_material_slot": 2}}],
        "accessors": accs,
        "bufferViews": views,
        "buffers": [{"byteLength": len(blob),
                     "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}],
    }
    p = d / "foreign.gltf"
    p.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    manifest["foreign/foreign.gltf"] = sha(p)
    # the same as a GLB with a BIN chunk, and a .gltf with an external .bin
    doc2 = json.loads(json.dumps(doc))
    doc2["buffers"] = [{"byteLength": len(blob)}]
    js = json.dumps(doc2).encode()
    js += b" " * (-len(js) % 4)
    raw = bytes(blob) + b"\0" * (-len(blob) % 4)
    g = d / "foreign.glb"
    g.write_bytes(struct.pack("<III", 0x46546C67, 2, 28 + len(js) + len(raw)) + struct.pack("<II", len(js), 0x4E4F534A)
                  + js + struct.pack("<II", len(raw), 0x004E4942) + raw)
    manifest["foreign/foreign.glb"] = sha(g)
    doc3 = json.loads(json.dumps(doc))
    doc3["buffers"] = [{"byteLength": len(blob), "uri": "foreign%20data.bin"}]
    (d / "foreign data.bin").write_bytes(bytes(blob))
    (d / "foreign_ext.gltf").write_text(json.dumps(doc3), encoding="utf-8")
    manifest["foreign/foreign_ext.gltf"] = sha(d / "foreign_ext.gltf")


def read_all():
    for p in sorted(list(OUT.rglob("*.glb")) + list(OUT.rglob("*.gltf"))):
        rel = p.relative_to(OUT).as_posix()
        fresh()
        try:
            c = load_scene(p)
            q = p.with_name(p.name + ".read.cast")
            c.save(str(q))
            manifest[rel + ".read.cast"] = sha(q)
            # the same with -0.0 bone rotation components written as +0.0 (LAPACK's eigenvectors carry signed zeros)
            c = castlib.Cast.load(str(q))
            for mdl in c.Roots()[0].ChildrenOfType(castlib.Model):
                sk = mdl.Skeleton()
                for b in (sk.Bones() if sk is not None else []):
                    for k in ("lr", "wr"):
                        if k in b.properties:
                            b.properties[k].values = [0.0 if v == 0 else v for v in b.properties[k].values]
            z = p.with_name(p.name + ".read.zero.cast")
            c.save(str(z))
            manifest[rel + ".read.zero.cast"] = sha(z)
        except Exception as e:
            manifest[rel + ".read.cast"] = f"error: {type(e).__name__}: {e}"
            traceback.print_exc()


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    write_synth()
    write_foreign()
    if "--no-real" not in sys.argv:
        write_real()
    read_all()
    (OUT / "manifest.json").write_text(json.dumps(manifest, indent=1, sort_keys=True), encoding="utf-8")
    print(len(manifest), "entries")
