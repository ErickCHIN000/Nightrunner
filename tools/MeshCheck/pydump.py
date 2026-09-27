"""Cross-check driver for MeshCheck: decode meshes with the Python prototype (read-only) and print one JSON line
per mesh with the fields the C# port must reproduce. Usage: python pydump.py <pack> <index> [<index> ...]

Run with PYTHONDONTWRITEBYTECODE=1 so nothing is written into nightrunner-main.
"""
import base64
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "nightrunner-main"))

import numpy as np  # noqa: E402

from nightrunner.container.rp6l import Pack  # noqa: E402
from nightrunner.mesh import variants  # noqa: E402
from nightrunner.mesh.decode import decode_resource  # noqa: E402


def b64(a, dtype):
    return base64.b64encode(np.ascontiguousarray(a, dtype=dtype).tobytes()).decode("ascii")


def dump(r):
    m = decode_resource(r)
    entries = []
    for e in m.geometry_entries:
        d = {"format": e.format, "vertex_count": e.vertex_count,
             "submeshes": [[int(s.material_slot), int(s.index_count), [int(x) for x in s.palette]] for s in e.submeshes],
             "indices": b64(np.concatenate([s.indices for s in e.submeshes]) if e.submeshes else [], "<u2")}
        v = e.vertices
        if v is not None:
            d.update(pos=b64(v.positions, "<f4"), uv0=b64(v.uv0, "<f4"),
                     uv1=None if v.uv1 is None else b64(v.uv1, "<f4"),
                     normals=b64(v.normals, "<f4"), tangents=b64(v.tangents, "<f4"), sign=b64(v.tangent_sign, "i1"),
                     weights=None if v.weights is None else b64(v.raw["weights"], "u1"),
                     joints=None if v.joints is None else b64(v.joints, "u1"))
        entries.append(d)
    sk = None
    if m.skin_raw is not None:
        dec = variants.decode(m.skin_raw, m)
        if "error" in dec:
            sk = {"error": dec["error"]}
        else:
            sk = {"table": dec["material_table"], "skins": [{
                "name": v["name"], "flags": v["raw"]["flags"], "tail": v["tail_hex"], "e_hi": v["raw"]["e_hi"],
                "pairs": [[p["slot"], p["material"]] for p in v["material_map"]],
                "remap": [[x["key"], x["value"], x["hi"]] for x in v["remap"]],
                "refs": [[x["record"], x["lo_hi"], x["raw"]] for x in v["refs"]]} for v in dec["variants"]]}
    return {"index": r.index, "name": r.name, "layout": m.layout,
            "entities": [[en.name.hex(), en.parent, en.type] for en in m.entities],
            "materials": [x.name.hex() for x in m.materials], "capacity": m.material_capacity,
            "entries": entries, "skins": sk, "warnings": list(m.warnings)}


def main():
    pack, indices = sys.argv[1], [int(x) for x in sys.argv[2:]]
    with Pack.open(pack) as pk:
        for i in indices:
            try:
                out = dump(pk.resource(i))
            except Exception as exc:  # noqa: BLE001
                out = {"index": i, "error": f"{type(exc).__name__}: {exc}"}
            sys.stdout.write(json.dumps(out) + "\n")


if __name__ == "__main__":
    main()
