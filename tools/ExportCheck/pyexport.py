"""Cross-check driver for ExportCheck: write the per-mesh Cast + <stem>.mesh.json with the Python prototype
(`gui/meshdata.py::export_cast_files`, fresh castlib hash counter per file, exactly as `nr mesh export`).
Usage: python pyexport.py <pack> <index> <out.cast> [<index> <out.cast> ...]   — one JSON line per mesh on stdout.

Run with PYTHONDONTWRITEBYTECODE=1 so nothing is written into nightrunner-main. Output goes only where <out.cast> says.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "nightrunner-main"))

from nightrunner.container.rp6l import Pack  # noqa: E402
from nightrunner.gui.meshdata import export_cast_files  # noqa: E402


def main():
    pack, rest = sys.argv[1], sys.argv[2:]
    with Pack.open(pack) as pk:
        for i in range(0, len(rest), 2):
            index, out = int(rest[i]), Path(rest[i + 1])
            t = time.perf_counter()
            try:
                res = export_cast_files(pk, index, out)
                line = {"index": index, "ok": True, "seconds": time.perf_counter() - t,
                        "bone_frame_residual": res["bone_frame_residual"]}
            except Exception as exc:  # noqa: BLE001
                line = {"index": index, "ok": False, "error": f"{type(exc).__name__}: {exc}"}
            sys.stdout.write(json.dumps(line) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
