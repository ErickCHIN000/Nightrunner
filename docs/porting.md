# Porting history

Nightrunner began as a Python prototype by the same author. That project is archived and no longer maintained.
It is **historical reference only**: the C# code and the documents in
this folder are the spec. You do not need it to build, run or test Nightrunner.

This document records how the C# port relates to it: what each area became, how parity was checked, and every place
where the C# deliberately behaves differently (*Divergences*). Python behaviour that looked wrong was not silently fixed
in the port; it is recorded here instead.

Related: [formats.md](formats.md) (file formats), [architecture.md](architecture.md) (how the application works),
[runtime-integration.md](runtime-integration.md) (NightrunnerProxy).

Rules carried over from the predecessor and still in force: offline file work only, game installs are read-only,
unknown bytes are preserved verbatim, refuse rather than approximate, no absolute paths in source.

## Using the predecessor for cross-checks

A few tools and tests compare against the predecessor when a checkout of it sits beside this repository as
`nightrunner-main/` (git-ignored). Run it read-only, with `PYTHONDONTWRITEBYTECODE=1`, and never modify it:

- `tools/MeshCheck --xcheck N` drives `tools/MeshCheck/pydump.py`, which decodes the same meshes with the Python package.
- `tools/ExportCheck` (per-mesh Cast and sidecar export) and `tools/BuildCheck` (mesh writer, edited scenes).
- `CastLibTests` include cases that compare against files the Python cast library wrote; they run only with
  `NIGHTRUNNER_CAST_FIXTURES` pointing at such a folder.

Everything else runs without it.

## Status by area

| Area | Predecessor modules (approx. size) | C# home | Current status |
|---|---|---|---|
| Container (RP6L v4) | `container/rp6l.py`, `catalogue.py`, `validate.py`, `census.py` (~1.6k lines) | `Core/Rpack` | Reader (memory-mapped) and writer (contiguous, grouped, preserve, auto; byte-identical to the predecessor). The predecessor's `validate`/`census` commands are not ported as such; `tools/RpackCheck` is the census |
| Util | `util/binio.py`, `names.py`, `hashing.py`, `jsonio.py`, `schema.py` (~0.5k) | inlined where used | name folding and alignment ported |
| Game install | `games.py` (~0.3k) | `Core/Games` | three profiles (DLTB, DL2, DL1 slot), folder map, Steam scan, validation, remembered roots |
| Game backends | none (the predecessor is DLTB/DL2 only) | `Core/Backends` | per-game capability interfaces; DL1 is an unimplemented slot |
| Texture | `texture/{imgc,dds,png,codec,formats,cli}.py` (~1.9k) | `Core/Texture` | IMGC read and write; decode to BGRA; re-encode BC1/2/3/7, BC4/BC5 unsigned and SNORM, BC6H UF16/SF16 (own encoder), plain formats, RGBA16F; DDS export (byte-identical to the predecessor) and import (its rules); float DDS and Radiance `.hdr` as HDR sources |
| Mesh | `mesh/*.py` (~2.7k) | `Core/Mesh` | decode (both layouts), byte-exact re-encode, skins (`0x12`) decode/encode + `.skn` text, sidecar; writing for DLTB (import, rebuild, image patch, identity); cloth (`0xF3`) decode/encode/remap, which the predecessor does not have |
| ClassReader | `classreader/{graph,image,fixups}.py`, `types/classreader.py` (~1.3k) | `Core/Mesh/ClassReader` | fixups, image, graph, layout detection, `ImagePatch` |
| SDB | `sdb/{reader,export,cli}.py` (~1.1k) | `Core/SDB` | reader (both layouts), preset decode, material resolver, search index, `.mat` text export; read-only by design. The predecessor's JSON export and CLI are not ported |
| PAK + `.model` | `pak/model_json.py`, `pak/cli.py` (~0.4k) | `Core/Model` | pak index and override order, `.model` v6 document, resolver, merged skeleton, material usage, slot/material editing, PAK writer |
| Audio | `audio/{aesp,bnk,wem,build,resolve,preview,pinhead}.py` (~1.6k) | `Core/Audio` | **experimental**, contributed by metalheadbangg: archive and bank browsing, event names, optional native decode, playback, raw WEM and WAV export. No import, replacement, bank rebuild or runtime loading; the predecessor's `build.py` is not ported. See [formats.md](formats.md#audio-experimental) |
| Cast / glTF | `cast/{castlib,export,import_,gltf,split,assemble}.py` (~4.2k) | `Core/Cast` | cast library, per-mesh export + sidecar, glTF both ways, model scene, import, split (byte-identical where deterministic) |
| Prefabs | none | `Core/Prefab` | new in the port: container (byte-exact), decoder (DLTB and DL2 layouts), DL2 text prefabs, placement, DLTB value edits, project item |
| Animation | `types/anim.py` (header words), `types/animscr.py`, `animgraph.py`, `animcustom.py` (~0.4k) | `Core/Anim` | largely new in the port: ANM2 clips (header versions 0–3, bit-exact re-encode), sequence/graph banks (byte-exact), custom-resource headers, playback, layers, Cast/glb export, import, facial / lip-sync pose mix |
| Project / build | `project.py`, `extract.py`, `build.py`, `select.py`, `roundtrip.py`, `update.py` (~2.8k) | `Core/Project`, `Core/Export` | textures, meshes, model scenes, `.model` overrides, clips and prefab edits (rpack + PAK); geometry, clips and prefabs DLTB only; no audio build |
| GUI | `gui/` (PySide6, ~12k) | `Nightrunner.UI` (WPF) | Raw, Textures, Materials, Meshes, Models, Prefabs, Animations, Audio, Viewport, Projects, Build, Mods, Inspector, Log; dockable, with saved workspace views |

`types/` in the predecessor (anim, animscr, animgraph, animcustom, prefab, area, envprobe, voxelizer, raw) is a thin
structural-dump layer over the container; the parts that matter were ported with their consumers.

## Tests

`Nightrunner.Tests` (xUnit v3, `net10.0-windows`; `global.json` selects the Microsoft Testing Platform runner that
xunit.v3 needs on SDK 10) has two tiers. **Synthetic tests** always run: `Synth.cs` ports the predecessor's
`tests/synth.py` pack builders plus IMGC and SDB stream builders, and `MeshSynth` builds a synthetic skinned DL2 mesh.
**Install-backed tests** start with `Installs.Require("dltb"|"dl2")`, which skips cleanly when no install is detected;
`NIGHTRUNNER_TESTS_NO_INSTALL=1` forces that path on a machine that has one. Each test file starts with the list of
Python tests it does not port and why. Run `dotnet test Nightrunner.Tests` for the current pass/skip counts.

Ported from the predecessor's suite: `test_rp6l_reader`, `test_rp6l_writer`, the byte-exact cases of `test_texture`
(with `TestDds`, the reader included), `test_sdb_layout` (including "a DL2 stream labelled with the DLTB version word
throws, and vice versa"), and the decode side of `test_classreader`, `test_mesh_decode`, `test_mesh_dl2` and
`test_mesh_variants` (its sample meshes and DL2 fixtures are read by name from the installs). `CastLibTests` port the
cast library's behaviour; `ModelTests` cover parse, pak override order, the material join, the merged skeleton and
"used by"; `SknSourceTests` check the `.skn` emitter against the DL2 DevTools sources.

## Parity checks

Measured against the predecessor, read-only; dates are when each was run.

- **Container writer**: byte-identical to the predecessor's writer; a rebuilt `menu_level_ft_persistent_pc.rpack` matches
  the shipped file byte for byte.
- **SDB** (2026-09-22): the C# reader matches the Python reader counter for counter on all four databases
  ([formats.md](formats.md#sdb-shader--material-database)).
- **Mesh decode** (`MeshCheck --xcheck 60 --seed 7`, plus `player_kc_basic_torso_a_tpp`, `wn_pistol_b_b`, `dummy_box` by
  name): layout, bone names / parents / types, material names and capacity, per-entry format / vertex count / submesh
  slots, counts and palettes, indices, positions, UV0/UV1, tangent signs, weights, joints (all bit-exact), normals and
  tangents (max |Δ| ≤ 1e-6), skins and the warning list: **DLTB 63/63, DL2 62/62 equal**. The predecessor's own
  `mesh dump` has no indices or skins, hence the small driver.
- **Mesh writer** (`BuildCheck`, 65 meshes × 10 edit kinds, same scene and sidecar on both sides): 657 jobs, 0
  mismatches where both build; 13 refused by both (the predecessor's format-0 self-check); new-material jobs differ by
  design (Divergences).
- **Model split** (19 jobs: edits of the Crane head and of the whole player model, plus a real Blender re-export):
  `mesh.json` and raw parts 167/167 identical, `model.cast` 29 identical and 138 differing only in bone quaternion noise
  (< 1e-12), `split_report.json` equal.
- **Full project build**: the Blender re-export of the player model (16 new objects) with a `/1` report, plus a
  `player_tpp_skeleton` override with no gear, built by the predecessor and by C#: `assets_2_pc.rpack` byte-identical
  (3,267,360 bytes) and both PAK members identical (PAK archives compared by member; zip timestamps differ).
- **Export** (`ExportCheck` per mesh; a scratch harness for models):

| | compared | identical | otherwise |
|---|---|---|---|
| per-mesh `.cast` (DLTB 34, DL2 29) | 63 | 43 byte-identical | 20 structurally equal; only bone `lr`/`wr` quaternions differ, ≤ 2.84e-14 (LAPACK vs Jacobi eigenvectors, ±0) |
| `.mesh.json` sidecar | 63 | 63 byte-identical | — |
| glTF writer (38 Casts as `.glb` and `.gltf`+`.bin`) | 76 | 76 byte-identical | — |
| glTF reader (read back to Cast) | 79 | 56 byte-identical | 23 differ only in bone rotation noise (≤ 1.8e-15, ±0) |
| model scene (DLTB: `player_kc_basic_tpp`, `dlc_ft_db_srv_man_pose_laying_back_a`, `player_kc_crafter_fpp`; DL2: `npc_abandon_pk_wanda`, `man_pk_shopkeeper_02_a`, `dlc_opera_wmn_srv_01_a`) | 6 models, 127 meshes | bone names/order/parents, faces, vertex ids, UVs, weights: all identical | positions differ exactly on parts whose own rig now supplies the rest pose — the predecessor's deformation (Divergences), e.g. Crane's head 17.9 mm, beard 15.6, eyebrows 7.0 |

  PNG export is not byte-identical to the predecessor's (Qt's encoder); the pixels are the same.

## Divergences

What the C# does differently from the predecessor, and why.

**Found by the test port and fixed in C# to match the predecessor** (the C# had been more lenient): the writer ordered
storages by first appearance (now the stock order), did not pad the file to 16 bytes (a rebuilt
`menu_level_ft_persistent_pc.rpack` was 559,892 bytes against 559,904 shipped; now byte-identical), accepted part types
missing from the catalogue, and could leave a half-written file behind; the reader accepted a name without its
terminating NUL; `ImgcHeader.Parse` accepted a part longer than the padded header (`strictLength: false` keeps the
lenient mode, as `strict_length=False` does); `LevelLayout` accepted paddings other than 16 and 0; the preview truncated
where the predecessor rounds (RGBA16F 0.5 → 127 instead of 128, SNORM +1 → 254 instead of 255). After the change
`RpackCheck` still parses all 50,176 stock DLTB IMGC headers.

**Deliberate: names resolved up front.** The reader refuses a name offset outside the blob, or an unterminated name,
when the pack is opened (names are resolved up front for search); the predecessor fails only when that name is read.

**Mesh re-encode, by scope.** The decode-side re-encoder (`MeshEncoder`) does not requantise: it refuses a vertex whose
tangent frame or weights were edited (`MeshUnsupportedException`). The predecessor's encoder re-derives a qtangent from
an edited frame (`encode_qtangent`) and re-quantises edited weights (`quantize_weights`); in C# that is the mesh writer's
job (`MeshBuild`), which does both. Unedited and position/UV-edited meshes re-encode exactly as the predecessor's do.
`MeshSkins.Decode` returns an object with `Error` where `variants.decode` returns `{"error": …}`.

**Predecessor bug, not ported: new materials overwrite skin materials.** `mesh/imagepatch.py::_append_materials` writes a
new material into class-11 entry `count` and then increments `count`. The entries `count..capacity-1` are not spares:
they are the skin-only materials that `Replace` pairs point at (every one is a `Replace` target: 184,192/184,192 DLTB,
113,804/113,804 DL2), so adding a material overwrites a skin's material and every skin that used it now draws the new
one. The predecessor treats these entries as spares; the skins census contradicts it. The C# writer grows the class-11
array instead.

**Mesh writing.** Besides overwriting a skin-only entry, the predecessor's `_append_materials` refuses a table whose
count equals its capacity (7,678 of 21,408 DLTB meshes); C# grows the table. Growing was checked safe by census: table
entries carry relocation slots only at +0x08 and +0x10, nothing else points into the table, and the record count always
equals capacity; anything outside that pattern is refused by name. A new entry is zero except its name pointer, with
+0x18..+0x1F copied from the last own material — the predecessor's rule for spare entries, not a measurement.
`imagepatch.apply` writes vertex count 0 for an entry whose format cannot be decoded (C# keeps the count or refuses; no
shipped DLTB mesh hits it). The predecessor's format-0 self-check tolerance (1e-2) refuses small moves on
large-coordinate format-0 meshes with only "positions differ". The predecessor carries part `0xF3` (cloth) verbatim
through any rebuild, so a changed vertex count or order leaves the cloth pointing at the old vertices; C# remaps it or
refuses a cloth vertex with no source. A NaN frame's qtangent relies on numpy's undefined NaN-to-int cast; C# writes a
zero quad (the same bytes).

**Rest pose of an assembled model (refinement of the predecessor's rule).** The predecessor takes every bone's rest from
the first supplier (the preset skeleton) and bakes the difference into each part's vertices (`rebind_matrices`:
Σw·(G_skel·inv_bind_part)), so heads bound to their own face rig come out deformed. C# starts from the skeleton's rest
and, for a bone where a skinned part's own bind (inverse of its inverse bind) differs by more than 0.5 mm or 0.5°, lets
a part supply the rest. Which part: taken literally ("the part with the most weight on that bone"), a beard out-weighs
the head on lip bones (68.5 vs 5.6 on one corner) and the head moves 1.10 mm. C# ranks candidates first by the part's
total weight on off-skeleton bones (the rig owner), then by weight on the bone, and counts LOD 0 only (a part with more
LODs would otherwise out-vote one with fewer). Result on `player_kc_basic_tpp`: head 0.005 mm, beard 1.343, eyebrows
0.001, torso 0.101.

**Model scene report format.** Because of the rest rule above, C# writes `nightrunner.model_cast/2` where the predecessor
writes `/1`; the splitter undoes each file with the rule it was baked with (`/1` first supplier, `/2` part rigs) and
records it as `rest_rule`.

**Drawn entry of a `.model` slot.** The predecessor picks the `selected` entry, else the first. The engine draws the
first entry and ignores `selected` (verified in game with the earlier runtime (removed); pending current-runtime
validation), so C# shows the first as drawn (`ModelSlot.Drawn`), keeps the predecessor's pick as `Chosen`, and the
build moves the chosen entry first.

**Cast library** (port of the vendored `castlib.py`), byte-identical output on every fixture. Behaviour that looks wrong
in the Python, kept or refused as noted: an unterminated string at end of file makes `CastString_t.load` loop forever
(C# throws); an unknown property type fails the whole load (C# keeps that node's bytes verbatim and refuses to edit it);
strings containing NUL are written silently and read back truncated (C# `SetString` refuses them); `export.py` writes
`bp_owner_entity = -1` into an unsigned `i` property, which `struct.pack` refuses — any mesh entry without an owner
entity cannot be exported by the predecessor (the C# exporter does not copy this); empty integer buffers raise (C#
refuses too); `CastColor.linearToSRGB` has the wrong formula (0.5 → 1.749; ported as is); `Material.Slots()` includes
`bp_material_slot` (ported as is); `Constraint.SetCustomOffset` silently ignores lengths other than 3/4 (ported as is);
re-saving collapses duplicate property names, rewrites a string's count as 1 and drops the reserved header word and
trailing bytes (C# does the same); node hashes come from one process-wide counter (C# uses a per-file
`CastHashSequence`, same base).

**Per-mesh export and sidecar.** `bp_owner_entity = -1` in an unsigned `i` property crashes the predecessor's export (no
shipped mesh in either game has an entry without an owner); C# writes `0xFFFFFFFF` (Cast's encoding of −1, as for a bone
parent) and the sidecar keeps `owner_entity: null`. A submesh that declares indices but has no index part crashes Python
with a bare `ValueError`; C# refuses it by name. Float buffers pass float32 → double → float32 in Python, quieting
signalling NaNs; C# does the same so files match (the exact bits stay in the sidecar). `name_hex` in C# is the UTF-8 of
the decoded name — identical for every valid UTF-8 name. .NET's shortest float format is wrong at some powers of two
(2^-25), so `MeshSidecar.PyFloat` falls back to exact digits (checked on 301,272 values).

**glTF (`cast/gltf.py`)**, ported as is unless it crashes: more than 4 influences sort weights with numpy's unstable
`argsort` (CPU-dependent ties; C# keeps lane order; real exports have 4); a self-parented bone becomes its own child
(invalid glTF); a parent cycle is a `RecursionError` (C# refuses); extras change type on read (`l`/`h`/`b` → `i`,
`d`/vectors → `f`; values outside u32 crash on save — C# refuses on load; a multi-string list saves only its first — C#
refuses); normalised signed accessors are not clamped (−128 → −1.0079); the `.bin` URI is not percent-encoded; a tangent
count mismatch crashes (C# refuses) while a normal count mismatch writes invalid glTF; a texture slot on a non-File node
crashes (C# refuses); negative JSON indices count from the end (C# refuses); the reader ignores inverseBindMatrices, does
not restore textures, stores `_BP_VERTEX_ID` as float (ids ≥ 2^24 lose precision), restores non-finite UVs on the first
primitive only, and a missing texture is dropped from `.glb` silently. Exporting straight to `.glb` from a mesh writes
in-memory doubles (unlike `.cast` then `.glb`); C# matches the second route.

**Splitter.** `_unchanged_geometry` counts rewritten group members, not vertices, so `weights_kept` can go negative
(ported as is); `_apply` reassigns a submesh's material before its refusal checks, so a refused submesh can still change
material (ported as is); a skinned vertex with all-zero weights, a face index past the vertex count, a non-triangle face
count, or a UV/normal count mismatch aborts the whole Python split — C# refuses that submesh by name; node hashes depend
on what ran earlier in the process (C# starts every template at the base value); `/1` reports store absolute pack paths
(C# resolves packs by label); the Python test `test_topology_change_and_loose_vertices` asserts
`sub["faces"] == sub["faces"]`.

**Pack writer.** Grouped layout refuses a zero-size part that shares an offset with the next group's first part
("overlaps previous payload"); `preserve` never checks sizes (refused one level up, as in `build.py`); alignment below 16
is written without complaint; `final_size` smaller than the payload is ignored and a short `fill` shifts later parts (C#
takes only a path, zero-padded); the owner index is recomputed even in `preserve`; logical flags are copied, never
recomputed (a mesh from a field08-0 pack keeps `0x01` inside a `0x1000` mod pack); `build.py` invents storage words
(align 8, flags 0) for a part past the template storages — the C# build takes every word from the template;
`PartSpec.FromPack` refuses compressed and child-pack parts that Python copies as raw bytes. Header field08 of a project
pack follows the predecessor's rule: `0x1000` with a mesh, else the source packs' shared value, else `0x1000`.

**DDS import (texture sources).** `read_dds` and `dds_to_levels` are ported rule for rule, but a DDS is only taken
verbatim when its format *is* the texture's build format and its type (2D/cube/volume) is the texture's; the predecessor
writes whatever format and type the DDS has (with a "geometry/format changed" warning). C# refuses the mismatch by name
and points at *build as*, so the sidecar stays the one statement of what gets written. The predecessor's sidecar
switches `import.srgb_to_linear` / `allow_unobserved` / `stats` / `stats_values` have no C# equivalent: tier C and sRGB
DXGI ids are refused for a verbatim DDS as by the predecessor's defaults (a float DDS that is only decoded may be tier C,
e.g. RGBA32F), and statistics are always the sidecar's — the predecessor recomputes them for decodable formats when the
texels changed. BC6H: the predecessor has no BC6H encoder and previews BC6H clipped to [0, 1]; C# encodes it with its own
encoder and tone-maps the preview.

**Viewer: plain albedo × `dif_0_val`.** The predecessor's viewer shows a plain `dif_0_tex` as stored and multiplies by
`dif_0_val` only inside its hair/opacity recipes; the C# viewer tints every plain albedo by `dif_0_val` ("Diffuse scale"
in the preset; the stock pixel shaders multiply the diffuse sample by a constant before the Fresnel and dye layers).
Near-white albedos with a tint were drawn white. The gradient-map recipe is new too.

**Viewer: hair alpha cutoff.** The viewer alpha-tests dithered hair at 0.08 instead of the predecessor's 0.25: the game
dithers coverage and TAA accumulates overlapping cards, and a 0.25 cutoff drops most of a brow (94 % of Crane's brow
card area is below it). Export keeps 0.25.

**Viewer: unskinned geometry at its owner entity.** The predecessor's viewer draws raw vertex positions;
`MeshScenes.Parts` places unskinned geometry by its owner entity's global, as the engine and `ModelCast` do (the export
already binds it rigidly to that bone). Measured: DLTB 642 of 21,408 meshes and DL2 1,015 of 33,728 have unskinned
geometry under a non-identity entity (props with pivots, billboards, vehicle doors, hood and tailgate); they drew
displaced before.

**Mod packs first.** With runtime modding on, the packs NightrunnerProxy's mods load are listed in load order before the
stock packs they load ahead of (`RuntimeContent.Rpacks`, `GameInstall.Rpacks(includeCustom: true)`), so a name lookup
(first hit) resolves to the mod's resource, following the engine's first-wins name registration. Between mods the order
is `nightrunner.json`'s. The predecessor has no runtime-content concept. See
[runtime-integration.md](runtime-integration.md).

**Audio: AESP header read.** The predecessor reads the member count as the u32 at 0x90 and the table offset as the u32 at
0xA0 (`audio/aesp.py`); the C# reads the count as a u64 at 0x90 and the table offset as the u64 at 0x88, and refuses an
archive where 0x88 and 0xA0 disagree (equal in all 14 shipped archives). The u64 at 0x98 is kept raw and shown as
`0x98` (0 everywhere). The header name is read up to 128 bytes (the predecessor: 16). Row ids are read as a u64 over the
predecessor's id + reserved u32s. The scan is recursive where the predecessor opens a fixed list (`init meta sfx
streams`), and it adds the language packs. Event names come from the C# code's own HIRC walk rather than the
predecessor's `resolve.py` / `pinhead.py`, and are unverified beyond three ids. The predecessor keeps its external
vgmstream optional; so does the C# (a user-built `libvgmstream.dll`, never distributed).

**Prefabs: earlier assumptions vs the data.** Not a Python divergence (the predecessor has no prefab support), kept here
as the record of what analysis before the port assumed and the data contradicted: see the corrections list in
[formats.md](formats.md#prefabs). Also: a vector count of 257,891 and duplicate object counts of 8 / 380 objects from
that analysis are not reproduced (the C# duplicate copies 6 records and 60–61 slots); the `RegisterPrefab` address it
gave belongs to a different overload (see the [engine appendix](formats.md#appendix-engine-references)).

**Animation: earlier assumptions vs the data.** Also not a Python divergence (the predecessor only dumps ANM2 header
words): stream-pack copies are *not* byte-identical (sequence banks differ in junk words, 373 custom-resource bodies
differ; graph banks are identical); the graph root has more fields than first listed; ValueRef types 6 and 8 and mode 0
exist; the scale-stream floor is 2e-4, not 2e-5; the "row-15 quirk" is a hard cut on a block boundary, not an encoder
bug. An earlier clip total of 56,774 disagreed with the per-pack table (94,560 DLTB copies). fps by clip name from
sequence banks alone gives 31,817 / 4,187 / 141 (30/60/120) and 484 multi-fps names, not 34,100 / 9,833 / 345 / 979 as
first counted (that count probably included the compiled banks inside custom resources). `DependencyWatcher` functions
are real, not `ret` stubs; the constant-stream count is header vtable slot 15, not 16; the ANM2 header code is in
`engine_x64_rwdi.dll`, not `engine_core_x64_rwdi.dll`. V0 was known only as "different" and V1 not at all; V1 is
DL2-only, and DL2 has 36 V0 names to DLTB's 9 (see [formats.md](formats.md#animation-anm2)).

## The earlier runtime (removed)

*Historical.* Before NightrunnerProxy, Nightrunner supported an earlier runtime loader: a `winmm.dll` proxy that read
`bin\x64\content.ini` (and older `custom_rpacks\content.ini` / `rpacks.ini` files) and loaded content from five
`custom_*` folders under the game's data folders (`custom_data`, `custom_audio`, `custom_rpacks`, `custom_sdb`,
`custom_maps`), with signed load orders (negative rpacks before the built-ins). Nightrunner detected it, showed its
folders, and could write a `content.ini` build output.

That support was removed completely in September 2026. Nightrunner no longer detects, reads, shows or builds for it:
the folder map, the ini parser, the `content.ini` output and the `custom_*` destinations are gone. Leftover `winmm.dll`
or `content.ini` files are ignored (not reported, not an error); only `bin\x64\dxgi.dll` is examined for a runtime. A
pack left in an old loader folder under the assets folder is now just one more pack of the recursive scan (the
predecessor's rule). Do not add support for it back.

Some in-game observations in these documents were made with that runtime installed; they are labelled *verified in game
with the earlier runtime (removed)* and remain **pending current-runtime validation**. Counts taken while its mod packs
were installed (for example 49 DLTB packs / 301,621 resources, 50,183 IMGC headers, 21,409 meshes) are superseded by the
stock counts in [formats.md](formats.md).
