# Historical cross-checks against the Python prototype

The C# port was checked, file by file, against the archived Python predecessor (the Python
prototype by the same author, MIT; checked out as `nightrunner-main/` in the repository root; git-ignored). These checks are **not part of the
normal build or test run**: `dotnet build` and `dotnet test` need neither Python nor the prototype. What they
established is frozen in the normal suite as SHA-256 tables (see below), so they only need re-running when a format
decision is revisited.

Requirements: Python 3 with numpy, the prototype checkout, and (for anything touching game data) the game installs.
Always run Python with `PYTHONDONTWRITEBYTECODE=1`: nothing may be written into the prototype. Write outputs to a
scratch folder outside the repository; outputs made from game data (`real/` folders, exported meshes) must never be
committed.

## Fixture generators (this folder)

| Script | Writes | Used by |
|---|---|---|
| `cast_fixtures.py <out>` (env `NR_PROTO=<nightrunner-main>`) | castlib-built `synth_*.cast`, hand-made `edge_*` / `unknown_*` streams and castlib's re-saves, `nul_in_string.cast`, `unterminated.cast`, `manifest.json` (SHA-256s) | the hashes in `Nightrunner.Tests/CastLibTests.cs` (always run, no files needed) |
| `gltf_fixtures.py <out> [--no-real]` (run with `PYTHONPATH=<nightrunner-main>`) | `synth/`, `foreign/` glTF round trips; `real/`: shipped meshes exported by the prototype (game data) | the hashes in `Nightrunner.Tests/GltfTests.cs` (always run); the opt-in glTF parity tests |

## Opt-in tests (`Nightrunner.Tests/OptIn/`)

Not compiled unless asked, so a normal run neither runs nor lists them:

```
dotnet test --project Nightrunner.Tests -p:OptInTests=true --filter-trait "Category=OptIn"
```

| Test | Needs |
|---|---|
| `RpackLayoutTests.PrototypeParitySynthetic`, `PrototypeParityShipped` | `NIGHTRUNNER_PROTOTYPE=<nightrunner-main>` (optional `NIGHTRUNNER_PYTHON`); the shipped cases also the installs. The prototype's PackWriter builds every case from the same JSON description; bytes, layout, warnings and refusals must agree |
| `GltfTests.PrototypeExportsMatch`, `PrototypeReadsMatch` | `NIGHTRUNNER_GLTF_FIXTURES=<out of gltf_fixtures.py>` |
| `ModelSplitTests.SmokeSplitOfAnEditedPrototypeScene` | `NIGHTRUNNER_SPLIT_SMOKE=<a Blender re-export (.cast/.glb/.gltf) of a prototype single-Cast export of player_kc_basic_tpp>`; made by hand from game data, so not in the repository. The only check against real Blender output |

Without their variables they skip.

## Cross-check tools

These compare the port with the prototype directly (they find `tools/<Tool>/py*.py` and `nightrunner-main/` above
their build output, or take `--script`):

- `tools/ExportCheck <out> [--game dltb|dl2] [--name MESH ...]`: per-mesh `.cast` + `.mesh.json` (via `pyexport.py`).
- `tools/BuildCheck [--count N] [--name MESH ...] --out <dir>`: edited-scene rebuilds (via `pybuild.py`).
- `tools/MeshCheck`: mesh decode (via `pydump.py`).

## What the normal suite freezes instead

| Always-run / install-backed test | Frozen from |
|---|---|
| `CastLibTests` (`BuiltTreeMatchesPrototype`, `EdgeStreamResavesLikePrototype`, `NulInStringReadsAsPrototype`, …) | `cast_fixtures.py` manifest |
| `GltfTests` hash tables | `gltf_fixtures.py` synth/foreign |
| `CastExportTests.ExportBytesAreFrozen`, `SyntheticExportBytesAreFrozen` | ExportCheck on the 17 DLTB samples + 2 DL2 meshes; the prototype's export of `MeshSynth.SkinnedDl2` |
| `MeshBuildTests.EditedBuildsAreFrozen` | BuildCheck on 5 DLTB meshes × its edited scenes (55 jobs) |
| `RpackLayoutTests.SyntheticCasesAreFrozen` | `PrototypeParitySynthetic` (52 cases: 35 packs, 17 refusals) |

When one of those fails because the port changed on purpose, re-run the matching check above first, then take the new
values from the failure message.
