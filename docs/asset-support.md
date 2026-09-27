# Asset support

What Nightrunner reads, exports and rebuilds, per game. **Tool status** is about Nightrunner itself (parsing, exporting,
writing files that pass its own verification). **In game** is about the result loading in the game through
NightrunnerRuntime, which is **pending current-runtime validation** for everything
([runtime-validation-checklist.md](runtime-validation-checklist.md)).

Games: **DLTB** = Dying Light: The Beast (data folder `ph_ft`), **DL2** = Dying Light 2 (`ph`). Dying Light 1 has a
placeholder in the code and is not supported.

## Summary

| Content | DLTB read / export | DLTB rebuild | DL2 read / export | DL2 rebuild |
|---|---|---|---|---|
| Resource packs (`.rpack`, RP6L) | yes | yes | yes | textures only |
| Textures (IMGC) | yes | yes | yes | yes |
| Materials (SDB) | yes (`.mat` export) | no | yes (`.mat` export) | no |
| Meshes, skins, cloth | yes | yes (cloth remap experimental) | yes | no |
| Models (`.model` in `.pak`) | yes | yes (edits, scenes) | yes | no |
| Prefabs, binary | yes (decoding partial) | experimental (edits) | yes (decoding partial) | no |
| Prefabs, text (JSON / MessagePack / YAML) | yes | no | yes | no |
| Animation clips (ANM2) | yes | experimental (import from Cast/glTF) | yes | no |
| Animation banks (sequences, graphs, custom resources) | read and re-serialise | no | not verified | no |
| Audio (Wwise AESP / WEM) | yes, [audio.md](audio.md) | no | yes | no |

"Rebuild" means writing new game-format files from edits, into a project build folder. Where DL2 says "no", Nightrunner
refuses by name rather than write something unverified. In game: not yet confirmed for any row.

## By content

**Resource packs.** Every `.rpack` under the game's assets folder, memory-mapped. Export a pack or its raw parts to a
folder. Rebuilt packs are written in the stock layout (byte-identical to the stock pack when nothing changed).
Refused: compressed storages and `.rpacz` child-pack parts.

**Textures.** Decode every stored format with a decoder (BC1-BC7, BC6H, uncompressed and float formats) for viewing and
export as PNG or DDS. Import PNG, DDS (including float) and Radiance `.hdr`, encoded to BC1/2/3/7 (+sRGB), BC4/BC5
(+SNORM), BC6H (UF16/SF16), R8, RG8, RGBA8, ARGB8 or RGBA16F. 2D textures only: cube and volume textures are refused, and
float formats from 8-bit sources are refused.

**Materials.** The game's shader database (`runtime_dx11.sdb` / `runtime_dx12.sdb`) is read for the material views and the
viewport's shading; materials export as `.mat`. There is no SDB writer, so new materials cannot be created.

**Meshes.** Decode, including skins and cloth parts; export per mesh as Cast (`.cast`), glTF binary (`.glb`), a
`.mesh.json` sidecar and `.skn` skin text. Rebuild on DLTB from an edited Cast or glTF scene (Blender round trip):
vertex formats 0, 3, 6 and 8; at most 65,535 vertices per draw window; bones cannot be added, removed or renamed; a new
cloth vertex is refused.

**Models.** `.model` documents from the data paks, with their merged skeleton and materials; export as `.model`, Cast,
glTF or a folder with every mesh. On DLTB: edit slots, materials and values into a new `.pak`, or "Edit as scene" (one
Cast/glTF for the whole model) and split it back into meshes on build.

**Prefabs.** The binary prefab container round-trips byte-exactly; its object model is decoded in part (about 85% of
slots named on DLTB, 83% on DL2, measured). Text prefabs from the paks are read. On DLTB, edits (rename, duplicate,
transform, values, activation, removal) rebuild the pack's `Prefabs` resource — experimental.

**Animation.** ANM2 clips decode and re-encode bit-exactly (header versions 0-3); the viewport plays clips on models,
with layers and facial poses. Export clips as Cast or glTF. On DLTB, import a clip from Cast/glTF into a rebuilt clip
pack — experimental. Animation banks (sequences, graphs, custom resources) are read and re-serialised on DLTB (the census and tests cover
DLTB only); editing them is not supported.

**Audio.** See [audio.md](audio.md): browse, name, decode (optional decoder), play and export; no import or replacement.

## Interchange formats

- In: PNG, DDS, `.hdr`, Cast (`.cast`), glTF (`.gltf`, `.glb`), project JSON.
- Out: PNG, DDS, Cast, glTF (`.glb`), `.mesh.json`, `.skn`, `.mat`, `.model`, `.wav`, `.wem`, raw resource parts, and the
  game formats written by a build (`.rpack`, `.pak`).
- Not supported: FBX, OBJ, TGA, EXR.

The per-format details (layouts, measured censuses, refusals) are in [formats.md](formats.md).
