"""Cross-check driver for BuildCheck: rebuild mesh parts with the Python prototype (read-only).

Usage: python pybuild.py <jobs.json>
jobs.json: [{"id", "scene", "sidecar", "parts": {"<type hex>": "<file>"}, "name", "out"}]
Each job writes <out>/<type hex>.bin for the parts the prototype regenerates, or <out>/error.txt. One line per job on
stdout: {"id", "ok"}. Run with PYTHONDONTWRITEBYTECODE=1 so nothing is written into nightrunner-main.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "nightrunner-main"))

from nightrunner.mesh.codec import rebuild_from_files  # noqa: E402


def main() -> int:
    jobs = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    for job in jobs:
        out = Path(job["out"])
        out.mkdir(parents=True, exist_ok=True)
        parts = {int(t, 16): Path(p).read_bytes() for t, p in job["parts"].items()}
        try:
            res = rebuild_from_files(Path(job["scene"]), Path(job["sidecar"]), parts, job["name"].encode("utf-8"),
                                     pad_index_part=True, ignore_bone_changes=True)
            for t, data in ((0x10, res.image), (0x11, res.fixups), (0xF0, res.vertex), (0xF1, res.index)):
                if data is not None and t in parts:
                    (out / f"{t:02X}.bin").write_bytes(data)
            (out / "report.json").write_text(json.dumps(res.report, default=str), encoding="utf-8")
            ok = True
        except Exception as exc:  # noqa: BLE001 - the refusal text is compared, not raised
            (out / "error.txt").write_text(f"{type(exc).__name__}: {exc}", encoding="utf-8")
            ok = False
        print(json.dumps({"id": job["id"], "ok": ok}), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
