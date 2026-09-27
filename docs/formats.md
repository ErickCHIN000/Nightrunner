# Format reference

What Nightrunner knows about the file formats of Dying Light: The Beast (**DLTB**, data folder `ph_ft`) and Dying
Light 2 (**DL2**, data folder `ph`), as implemented in `Nightrunner.Core`. The C# code is the spec; this document
explains it and records the measurements behind it.

Conventions:

- **Which game.** Every layout, offset and count says which game it was measured on. "Both" means both games were
  checked; a statement about one game is not assumed for the other unless it says so.
- **Offsets** inside a file format (`+0x40` of a record, a part type such as `0x20`) are part of the format and stay
  in the section that describes it. Addresses inside game executables and DLLs are version-dependent and are collected
  in the [appendix](#appendix-engine-references).
- **Confidence**, where a section uses it: **A** = read in engine code, **B** = measured on every shipped instance,
  **C** = inferred.
- **Censuses** are dated. Unless a table says otherwise they cover the stock game files only. Re-run them with the tools
  under `tools/` (each skips cleanly when no install is found):
  `RpackCheck` (packs, textures, `--bc6h`), `MeshCheck` (meshes, `--models`, `--cloth`, `--materials`), `SdbCheck`,
  `PrefabCheck` (`--text`, `--edits`, `--fields`), `AnimCheck`, `AnimBankCheck`.
- **Refuse rather than approximate.** An unsupported case throws or refuses by name; unknown bytes are kept verbatim.
- **"In game."** Nothing in this document has been validated with the current runtime (NightrunnerProxy). Statements
  marked *verified in game with the earlier runtime (removed)* were observed with a previous runtime that is no longer
  supported (see [porting.md](porting.md#the-earlier-runtime-removed)); they are **pending current-runtime validation**.

Contents: [Container](#container-rp6l-v4) · [Textures](#textures-imgc) · [Meshes](#meshes) · [Cloth](#cloth-part-0xf3) ·
[Paks and `.model`](#paks-and-model) · [SDB](#sdb-shader--material-database) · [Prefabs](#prefabs) ·
[Animation](#animation-anm2) · [Facial rig](#facial-and-lip-sync) · [Audio](#audio-experimental) ·
[Engine references](#appendix-engine-references)

## Container (RP6L v4)

`Core/Rpack` (`RpackFile`, `RpackFormat`, `RpackCatalog`, `RpackWriter`). Both games.

- On-disk layout: header 36 B, storage records 20 B, physical (part) records 16 B, logical (resource) records 12 B,
  u32 name offsets, a NUL-terminated name blob, then payload. Part offset = `(storage.base_units + physical.offset_units) << 4`.
- Records are kept as their raw packed words; every derived field is computed, so unknown bits survive a
  read → write round trip bit for bit.
- `physical.packed`: low byte = storage index, bit 8 preload-skip, bits 9..11 priority, bit 12 special, bit 13 payload
  lives in the `.rpacz` child pack, bits 16..31 owning logical index.
- `storage.flags`: bits 0..1 method (0/1 = plain bytes, 2/3 = compressed → unsupported), bit 3 stream group;
  `metadata` high nibble = codec; the version is split across `flags >> 4` and `metadata & 0xF`.
- Names fold as the engine's `FindLogicalResourceUsingName` does: ASCII `A`–`Z` only.
- Compressed storages (method 2/3) and child-pack parts have no readable payload here; they are marked, never guessed.
- A name offset outside the blob, or a name without its terminating NUL, is refused when the pack is opened.
- Header field08 bit 12 (`0x1000`, `RpackFormat.Field08OnDemand`) marks an on-demand pack; the writer's `auto` layout
  lays such a pack out contiguously, any other grouped. The field08 a project pack gets is in
  [architecture.md](architecture.md#build).

**Writer** (`RpackWriter`): layouts contiguous, grouped, preserve and auto, byte-identical to the Python predecessor's
writer. Grouped layout = one contiguous region per storage group; storage table in the stock order (stream storages
first, then by type id); sizes rounded to the storage alignment; part count truncated to 16 bits; file padded to 16
bytes. Part types must be in the type catalogue. Files are written to `<name>.partial` and moved into place only when
complete.

Measured (`RpackCheck`, stock packs, Release; DLTB re-measured 2026-09-27, DL2 date not recorded):

| | DLTB | DL2 |
|---|---|---|
| packs / resources / parts | 47 · 301,613 · 492,062 | 83 · 252,022 · 512,991 |
| bytes mapped | 50.7 GB | 46.6 GB |
| index every pack (tables only) | ~27 ms, ~70–80 MB | ~30 ms, 64 MB |
| unfiltered name search / `player` / `head` | ~4 ms / ~2 ms / ~2 ms | 4 ms / ~1.5 ms / ~1.5 ms |
| 2,000 random 4 KB part reads (largest pack) | 14 ms | 15 ms |

## Textures (IMGC)

`Core/Texture`. A texture resource is type `0x20`: the IMGC header on its `0x20` part, the bitmap on its `0x21` part.
Both games.

- **Header** (`Imgc.cs`): an 80-byte fixed part, stored 16-byte padded. Statistics are kept as raw bytes, never
  interpreted, so a round trip stays bit-exact. Header-only records (flag `0x02`) carry a reference name instead of a
  bitmap. `ImgcHeader.Parse` refuses a part longer than the padded header (`strictLength: false` gives the lenient
  mode).
- **Level layout**: mip-major, each level padded to a 16-byte stride. A third-party tight variant (levels back to back)
  is accepted only when the part size equals the tight sum. The padding is detected, never guessed: a size that matches
  neither is an error naming both computed sizes. Paddings other than 16 and 0 are refused.
- **Formats** (`ImgcFormats.cs`): all 70 rows of the native `IL::Format` enum, because the layout maths needs
  `unit`/`block` for every id even though only 18 occur in the shipped DLTB corpus.
- **Decoding** (`TextureDecoder.cs`): BC1–BC5 are decoded here with explicit SNORM endpoint handling — `BC5_SNORM`
  (7,762 DLTB textures) and `BC4_SNORM` (1,191) are most of the normal maps. BC6H and BC7 decode through
  [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) (MIT). BC6H is tone-mapped (Reinhard + gamma) for display.
  A format with no decoder is refused by name.
- **Encoding** (`ImgcEncoder.cs`, `BlockEncoders.cs`): BC1/BC2/BC3/BC7 through BCnEncoder.Net (BC1 in its punch-through
  mode when any source alpha is below 255, so cutout holes survive). BC4 and BC5, signed and unsigned, are encoded here
  (the library writes only the unsigned ones, and BC5_SNORM is what the normal maps are); the encoder always emits the
  eight-value block mode with `a0 > a1`, which serves both signednesses. BC6H is written from floats by the project's own
  encoder (below). Measured decode → encode → decode drift (mean absolute error per channel, 0–255): BC1 0.01, BC4 0.00,
  BC4_SNORM 0.67, BC5 0.01, BC5_SNORM 0.00, BC7 0.18, RGBA8 0.00.

**DLTB census** (`RpackCheck`, stock packs, 2026-09-27): **50,176 IMGC headers parsed, 0 unreadable**; every format
present decodes a sample — BC1 14,756, BC4 12,096, BC5_SNORM 7,762, BC7 7,464, R8 4,057, RGBA8 1,367, BC6H_UF16 1,255,
BC4_SNORM 1,191, RGBA16_SNORM 96, RGBA16F 61, RG8_SNORM 53, BC5 4, RGBA8_SNORM 4, ARGB8 3, RGBA8_UINT 3, R16_SNORM 2,
RG8 1, RGBA16 1. (An earlier count of 50,183 included textures from installed mod packs.)

### HDR (BC6H)

**Corpus.** Every BC6H texture in both games is `BC6H_UF16` and 2D — no cube maps, no `BC6H_SF16`:

| | DLTB 1,255 | DL2 619 |
|---|---|---|
| reflection probes `<hash>_<time>_<weather>_hqrfl.dds`, 512×512, 10 mips | 1,243 (`dlc_frontier_envprobes_pc` 621, `dlc_frontier_pc` 621, engine 1) | 270 (`city_pc` 138, `city_envprobes_pc` 135) |
| skyboxes `<time>_<weather>_{32,64,2048,4096}skybox.dds`, 1 mip | — | 336 (`engine_pc`) |
| engine lookups (`*_hcb.hdr` 4096×2048 calibration, `*_end/_enr/_mli/_sky.hdr`, `*_hli.dds`, `default_hli.png` …) | 12 | 13 |

Header flags `0x64` on the probes, `0x44` on the rest. The probes' mips are **prefiltered**, not box-filtered: mip 1
differs from a 2×2 box of mip 0 by 13–64 % (relative L1) on sampled probes, so a source must carry every mip —
regenerating them would destroy the roughness chain. The statistics hold real HDR ranges (probe maxima 0.01–98,
skybox maxima up to 65,504). The stored statistics are close to, not equal to, those of the decoded mip 0 (probe max
17.4/19.3/23.7 vs stored 18.5/20.4/21.7; DLTB probes store a mean of 0; 2048² skyboxes match): they describe the source
image, so after an edit they are stale. Whether the engine reads them is not known.

**Encoder** (`Core/Texture/Bc6h.cs`). BCnEncoder.Net 2.3.0's BC6H encoder wraps endpoint deltas: re-encoding decoded
shipped probes returns 65,504 for pixels near zero at every quality (log2 error up to 26: a whole block turned white),
and on fresh data its log2 RMSE is 5–90× this encoder's. `Bc6h.cs` writes only the four **one-region modes** (11:
10-bit endpoints, 12: 11+9, 13: 12+8, 14: 16+4), the only modes in the corpus (mip 0 of all 1,874 textures: DLTB
`0x03` 1.31 M, `0x07` 10.6 M, `0x0B` 9.97 M, `0x0F` 0.12 M blocks; DL2 `0x03` 1.12 M, `0x07` 4.70 M, `0x0B` 107.4 M,
`0x0F` 1.60 M; no two-region block), so shipped data is exactly representable. Per block and mode: nine starts on the
principal axis, least-squares refinement, a ±1 local search; error is measured on the decoded half bit patterns with
the decoder's integer maths, and deltas stay inside their range. UF16 clamps negatives/NaN to 0 and anything above
65,504 to 65,504 (the build warns with a count); SF16 is ±65,504. The one-region decoder in the same file agrees bit for
bit with the library on every shipped block. Decoding for display and export stays with the library (it returns exact
halves: 0 of 96 M sampled values off).

Measured by `RpackCheck --bc6h` (decode every level of every shipped texture → encode → decode; log2 error on
value + 2⁻¹⁰; the source is exactly representable, so the floor is 0):

| | DLTB | DL2 |
|---|---|---|
| textures / blocks (all levels) | 1,255 / 28.8 M | 619 / 116.3 M |
| modes over all levels (`0x03`/`0x07`/`0x0B`/`0x0F`), two-region | 1.31 / 11.17 / 16.18 / 0.13 M, 0 | 1.72 / 5.53 / 107.42 / 1.61 M, 0 |
| own one-region decoder vs the library | 0 blocks differ | 0 blocks differ |
| blocks re-encoded bit-identical | 99.5 % | 95.1 % |
| log2 error RMSE / max | 3.0e-6 / 0.0059 | ~1e-5 / 0.0080 |
| linear error RMSE / max | 8.6e-6 / 0.0625 | 2.9e-6 / 0.0156 |
| encode time, whole corpus (parallel, one run) | 62 s (~50 ms per texture) | 221 s (the 84 4096² skyboxes take 3–4 s each) |

On fresh data (shipped top levels plus 2 % per-channel noise, 16 per game) the log2 RMSE against the noisy input is
0.0066 (DLTB) / 0.0068 (DL2); the shipped blocks themselves score 0.0069 / 0.0081 on the same input; the library
scores 0.57 / 0.068 (Balanced) and 0.16 / 0.031 (BestQuality), with 65,504 outliers.

### Texture sources (DDS, PNG, Radiance HDR)

A PNG cannot carry HDR, so an HDR texture (BC6H, RGBA16F) is exported to a project as an **RGBA16F DDS of every mip**
(`DdsWriter.ToFloatDds`; BC6H decodes to halves, so this is lossless). A build reads (`Core/Project/TextureSource.cs`):

| source | builds |
|---|---|
| `.png` | every 8-bit format; a float target (BC6H, RGBA16F) is refused by name — a PNG of a BC6H texture is a tone-mapped preview, not data |
| `.dds` in the build format | verbatim, every level and face as the file has them (`DdsReader.cs`: DX10, legacy FourCC, D3DFMT codes, masks; tier C and sRGB ids refused). The byte-identical BC6H export builds back bit for bit |
| `.dds` RGBA16F / RGBA32F / BC6H of the other signedness, float target | decoded and re-encoded; its mips kept, or regenerated (box filter, with a warning naming prefiltered probes) when it has one level; a cube DDS gives a cube texture |
| `.hdr` (Radiance RGBE), float target, 2D | one surface; mips regenerated with the same warning. `RadianceHdr.cs` reads flat and run-length scanlines in `-Y h +X w`; XYZE, `EXPOSURE`/`COLORCORR` ≠ 1, other orientations and old-style RLE are refused by name |

Also refused by name: a DDS in any other format ("DDS is BC1, the texture builds as BC6H_UF16: save it as …"), a DDS
whose type (2D/cube/volume) differs from the texture, a volume float target, and two source files for one resource.
Statistics, flags, extension and tail always come from the texture's sidecar, never recomputed.

**DDS export** (`DdsWriter`) is byte-identical to the predecessor's.

## Meshes

`Core/Mesh` decodes a type-`0x10` resource into `MeshModel`; `Core/Mesh/ClassReader` is the object graph under it.
Parts: `0x10` image, `0x11` fixups, `0xF0` vertices, `0xF1` indices, `0x12` skins, `0xF3` cloth (DLTB only).

- **Fixups / image** (`Fixups.cs`, `Image.cs`): every record and slot word is kept raw; `ToBytes()` re-serialises byte
  for byte. Pointers resolve only through the slot table (kind 0 and tagged kind 2); kinds 1/4/8 and secondary streams
  are refused by name — none occur in either game.
- **Layouts as data** (`MeshGraph.cs`): `MeshLayout.Dltb` (root 0x70, entity 0xE0, class-6 entry 0x40) and
  `MeshLayout.Dl2` (root 0x68, entity 0xD0, class-6 entry 0x30 → class-8 stream 0x20) differ only in sizes and in where
  each geometry field lives (`GeoField(InStream, Offset, Size)`). The decoder reads through the layout; no decode path
  branches on a game id. `MeshLayout.Detect` reads the layout from the fixups (root span, entity span per element,
  class-8 records), hint rules included.
- **Vertices** (`Vertex.cs`): formats 0/3/6/8, qtangent frame + handedness rule, weights/joints. Half floats go through a
  NaN-payload-preserving conversion (`System.Half` sets the quiet bit; 17 DLTB entries hold non-finite values and must
  re-encode bit-exactly).
- **Re-encode** (`MeshEncoder.cs`): fixups, vertex and index parts regenerated from the model in the original layout.
  This decode-side encoder refuses a vertex whose tangent frame or weights no longer match its raw record; requantising
  is the mesh writer's job (`MeshBuild`, see [architecture.md](architecture.md#build)).
- **Material table** (image class 11): entries `0..count-1` are the mesh's own materials; entries `count..capacity-1` are
  **skin-only materials**, each a `Replace` target (184,192/184,192 DLTB in the 2026-09-23 census, 113,804/113,804 DL2), not free spares
  (`MeshModel.FullMaterialTable()`). Table entries carry relocation slots only at +0x08 and +0x10, nothing else points
  into the table, and the record count always equals capacity (census, DLTB).
- **Backend**: `IMeshBackend.Decode(pack, index)`. DLTB meshes encode; DL2 is decode-only with a refusal text.

### Skins (part `0x12`)

`MeshSkins.cs`: part `0x12` `_SKIN_`, the compiled `.skn` skins, named after the source language: record name =
`Skin("name")`; `{slot, material}` pairs = `Replace`; `{key, value, hi}` = `ReplaceSurface(old surface id, new surface
id, surface flags)`; the 8-byte object = `ColorI` (`e_hi = 1` when a colour is set); refs = `UseSkin`; flag `0x40000000` =
`FilterInEditor()`. A `Replace` material indexes the **full** class-11 table. Decode never throws (garbage gives
`Error`); `Encode()` keeps every offset, shared array and dead span.

**`.skn` text** (`SknWriter.cs`): a read-only emitter; surface names and flags come from `surface.def` inside the
install's `dataN.pak` (`SurfaceDefs.Load`), numbers when it is absent. Lossy by design: comments, `!include`s,
`NodePos`/`Texture` blocks and `Replace` lines for materials the mesh does not have are not in the compiled part. Checked
against the DL2 DevTools `.skn` sources (`SknSourceTests`): every source skin of every DevTools mesh that ships comes
back, given the compile rules read off those pairs — a `ReplaceSurface` whose new surface is `""` compiles to the old id
(and is written back as `""`); material names match case-insensitively; a `Default` skin gains identity `Replace(x, x)`
for mesh materials it does not list.

**Resolution order (C).** A skin draws: the mesh's own slots, then every `UseSkin` target's pairs in listed order
(recursively — 31 DLTB / 500 DL2 targets have `UseSkin` of their own; a cycle stops), then the skin's own `Replace`
pairs; each resulting name resolves through the SDB. The order is inferred from DL2 DevTools `dummy_box.skn`:
`Skin("Red") { UseSkin("common_default",1) ColorI(0,128,0,0) … }`, where `common_default` carries only
`Replace("dummy.mat","dummy.mat")`. Checked by `SkinResolveTests`: every skin of `veh_sedan_a` (26) and
`int_ce_a_elevator_2x3_body_a` (230) resolves through dx11 to textures a pack provides, except three materials the
shipped skins name but **neither DLTB database contains** (`veh_sedan_body_a_c.mat`, `veh_sedan_glass_a_c.mat` — skin
`CLEAN` — and `sur_sens$npp.mat`). `Police` and `Taxi_broken_glass` point slot 0 at skin-only entries that carry the same
name as the mesh's own `veh_sedan_body_a.mat`, so by material name they draw like `Default`; whatever makes them differ
in game is not in the material name (C).

**`ColorI` (hypothesis, C):** `ColorI(index, r, g, b)` — the first argument a colour-slot index (it matches the
deprecated `Color(i, v3)` signature; every source writes `0`, and `ColorI(255,255,255,255)` does not compile), compiled
to `r g b ff | 00 00 00 ff`. Of 18,970 DLTB (19,147 DL2) skins with a colour, 14,058 (14,063) store `00 00 n ff` with
n < 32 — the sedan's `color_a`…`color_g` are n = 1…7 — so the "blue" byte often looks like a palette index rather than a
colour. The viewer does not tint by it. `ReplaceSurface` is physics-only.

### Census

`MeshCheck` (every type-`0x10` resource of every pack; Release). DL2 2026-09-27 (no mod packs). DLTB: the stock mesh
count is from 2026-09-27; the other DLTB rows are the 2026-09-23 census, which covered **21,409 meshes: the 21,408 stock
ones plus one mesh from a mod pack installed at the time**, so they are not stock-only to the last unit:

| | DLTB | DL2 |
|---|---|---|
| stock meshes | 21,408 | 33,728 |
| decode + byte-exact re-encode (`0x11`/`0xF0`/`0xF1`) + skins round trip (`0x12`) | **all** (21,409/21,409 on 2026-09-23; 21,423/21,423 on 2026-09-27 with 15 mod-pack meshes) | **33,728/33,728** |
| entities / geometry entries / submeshes | 207,555 / 28,052 / 142,535 | 380,816 / 55,220 / 139,264 |
| vertices | 39,976,309 | 56,936,568 |
| vertex formats (entries) | 0: 9,147 · 3: 17,035 · 6: 1,828 · 8: 42 | 0: 25,652 · 3: 24,504 · 6: 5,064 |
| skins / skin-only materials (all used by a `Replace`) | 120,944 / 184,192 | 174,118 / 113,804 |
| decode warnings | 0 | 0 |
| whole census (parallel) | ~2 s | ~2.5 s |

`MeshCheck` always includes meshes in installed NightrunnerProxy mod packs, marked `custom`; subtract those for stock
numbers. The predecessor's own census over the same DLTB packs (2026-09-23) reports the same mesh, entity, submesh,
vertex and format counts.

```
dotnet run --project tools/MeshCheck -c Release -- [--game dltb|dl2] [--xcheck N] [--seed S] [--name MESH] [--models] [--cloth]
```

## Cloth (part `0xF3`)

DLTB only (DL2 ships none). Part `0xF3` `_CLOTH_DATA_` of a mesh, plus the ClassReader objects in the mesh image that
describe it. `Core/Mesh/Cloth.cs` (`ClothData`: decode, layout, encode, image patch, invariants) and
`Core/Mesh/ClothRebuild.cs` (carrying it through a mesh rebuild). The predecessor keeps the part opaque. Sources: the
DLTB loaders (see the [appendix](#appendix-engine-references)) and the data of every shipped part. The simulation runs
in a compute shader, so array *meanings* come from the data. Confidence A/B/C as defined above.

**Where it hangs (A).** The cloth entity (the geometry-owning entity, type 2; exactly one per cloth mesh) holds at entity
+0xD0 a pointer to **ClothInFile** (class 23, 0x18 B): +0x00/+0x08 `{u16*, u64}` proxy-bone palettes (one per mesh),
+0x10 → **ClothEntityInFile** (class 32, 0x198 B). Every offset below is a u32 **in dwords** into part `0xF3`,
`0xFFFFFFFF` = absent.

| ClothEntityInFile | content |
|---|---|
| +0x00 | name pointer (the `.msh` name, 58/58) |
| +0x08..+0x37 | simulation: gravity xyz, max displacement, skin width, viscosity, elasticity, max distance scale, max distance gameplay scale, backstop offset scale, spherical backstop radius scale, velocity damping (A: the `.model` override of each maps onto these offsets) |
| +0x38 | surface used when there is no mapping (`0xFFFFFFFF` in 58/58) |
| +0x3C / +0x40 / +0x44 / +0x48 | simulation index count (u16 triangles), particle count, E60 count, E64 count (= +0x3C, 58/58) |
| +0x4C..+0x7C | 13 array offsets: Indices u16 · Positions f32x3 · Normals f32x3 · E58 · E5C · E60 u32 · E64 u32 · E68 u32 (particles + 1, a prefix table) · E6C · E70 (absent 58/58; refused if set) · Joints u32x4 · Weights f32x4 · E7C u32 (element sizes B; the names Positions / Normals / Joints / Weights C) |
| +0x80 | six floats, box-like (not a tight box, C) |
| +0x98 / +0xB8 / +0xD8 | constraint sets S0–S2: count, batch count, three floats, offsets of Pairs (u32 = two u16 particle indices, B), B0 (not derivable), Batches (u32 cumulative ends, last = count, B) |
| +0xF8 | set S3: count, batch count, offsets +0x100 Pairs, +0x104/+0x108/+0x10C/+0x110 (not derivable); +0x114 = 0 |
| +0x118 / +0x120 | visual mappings (class 27, 0x54 B each), count |
| +0x128 / +0x130 | colliders (class 24, 0x70 B each: +0x00 type, +0x04 seven shape floats, +0x20 3×4 matrix, +0x58 bone name, +0x60 bone-name hash), count |
| +0x138..+0x187 | 5 × 16 B, one per cloth LOD level: runtime list pointer/count, and at +0x0C the **mapping drawn at that level** (file data) |
| +0x188 / +0x190 | runtime `ClothFile` / `ClothFileConstants` buffer handles (zero in files) |

| Visual mapping (class 27) | content |
|---|---|
| +0x00 | LOD: index into the cloth entity's geometry entries |
| +0x04 | render vertex count (= that entry's, 63/63) |
| +0x08 | render vertices **not** bound to the simulation mesh (= count − popcount(+0x38), 53/53) |
| +0x0C | popcount of +0x2C (5/5) |
| +0x10 | per render vertex: position f32x3 — **bit-exact copy of the vertex buffer** (63/63) |
| +0x14 | per bound vertex: simulation triangle u16 (< triangle count, 63/63) |
| +0x18 / +0x1C / +0x20 / +0x24 | per bound vertex, quantised with a (min, step) f32 header: u16 (two u8 barycentric-like values, C), u8 (signed distance along the normal, C), u16, u16 (two u8 angles each, header (−π, 2π/255), C) |
| +0x28 | per render vertex: mask bit (meaning unknown) |
| +0x2C / +0x30 / +0x34 | per bound vertex mask bit; for each marked one its render vertex index (derived, 5/5) and a f32x3 |
| +0x38 | per render vertex: bound mask |
| +0x3C | per render vertex: rank among bound vertices, `0xFFFFFFFF` if unbound (derived, 53/53) |
| +0x40 / +0x44 / +0x48 | per render vertex: normal, tangent, weights at full precision (≤ 5.4e-5 / ≤ 0.011 from the vertex buffer; tangents within 0.01 on 378,538 of 378,606 vertices) |
| +0x4C | per render vertex: joints u32x4 — equal to the vertex buffer's (53/53) |
| +0x50 | per render vertex: the submesh that uses it (53/53) |

"Per bound vertex" arrays are in vertex order (the rank). +0x2C..+0x34 are present in 5 mappings, +0x38..+0x50 in 53.

**Layout (B, 58/58).** Arrays in field order (entity +0x4C..+0x110, then each mapping +0x10..+0x50), absent ones
skipped, contiguous from dword 0, each exactly its derived size — u16 arrays `ceil(n/2)` dwords, quantised ones
2 + `ceil(n/2)` or 2 + `ceil(n/4)`, bit arrays `(n >> 5) + 1` (a whole extra dword when n is a multiple of 32:
`npc_ft_obasi_torso_a`, 7,616 vertices) — with every pad byte and bit zero, then zero padding to 16 bytes. The eleven
arrays with no derivable length (E58, E5C, E6C, S*.B0, S3.104..110) keep their stored length; they all belong to the
simulation mesh, which precedes the mappings, so no remap moves them. Decode refuses anything else by name.

**Census** (`MeshCheck --cloth`, Release, 2026-09-25):

| | DLTB | DL2 |
|---|---|---|
| resources with a part `0xF3` | 58 meshes, all in `common_meshes_pc.rpack` | 0 |
| decode + byte-exact re-encode + image offsets re-derived + invariants | **58/58** | – |
| part sizes | 8,000 .. 3,742,192 B (32.9 MB total) | – |
| particles / simulation triangles | 8..568 (9,033) / 12,946 | – |
| mappings | 63 (53 meshes 1, 5 hair meshes 2: LOD 0 and 1, the last LOD level drawing mapping 1) | – |
| render vertices / bound | 451,632 / 214,187 | – |
| colliders | 102 in 38 meshes (types 1: 33, 2: 16, 3: 53) | – |
| constraint sets in use | S0 58, S1 51, S2 58, S3 47 | – |
| unedited rebuild (every part) | 58/58 byte-identical | – |
| delete ~10 % of an entry's faces + their vertices + reverse the vertex order, per mapping | **63/63** pass the self-check (451,632 → 419,106 vertices) | – |

Cloth meshes are hair (`npc_ft_{kehinde,mara,olivia,shelby,starchild}_hair*`, `dlc_ft_*banshee*_hairs*`),
coats/hoods/dresses/shirts/pants/scarves and small props (`*_necklace_*`, `*_earring_a`, `*_flashlight_a`, `*_keys_a`,
`*_vials_a`); `MeshCheck --cloth` lists them.

**`.model` `clothResources` (B).** 286 DLTB slots in 79 models, always `{"name": "<slot mesh>.cloth", "initialized":
true}` — no other key, no collider or parameter override (the engine reads `gravity`, `skin width`, `viscosity`,
`delta time`, the scales, `max displacement`, `velocity damping`, `colliders`: A). The name is the slot's own mesh in
286/286; only 93 slots (55 of the 58 cloth meshes) name a mesh that has a part `0xF3`; 33 slots draw a cloth mesh without
`clothResources`. No pak holds a `*.cloth` member (the `.mpcloth` files are older mesh-part-cloth scripts). DL2: none. So
`clothResources` points at no other resource: it is a per-slot switch named after the mesh, with optional overrides
nobody ships.

**What a geometry edit may do** (`ClothRebuild`, `MeshRebuild.Rebuild`; a self-check re-runs every invariant):
- anything that leaves the cloth entry's vertices untouched (other entries, faces only, materials): cloth kept verbatim;
  the simulation mesh references no render vertex, and nothing references render triangles;
- **moved / re-framed / re-weighted vertices**: the copies are re-synced — +0x10 from the new positions (exact), +0x4C
  joints, +0x50 submesh; +0x40/+0x44/+0x48 from the rebuilt vertex only where that vertex's own frame / weight bytes
  changed. The **binding** (+0x14..+0x24) is not recomputed: a moved *bound* vertex is reported (`cloth: … moved vertices
  are bound to the simulation mesh; their binding is kept`); while the cloth simulates it follows its old place on the
  simulation mesh (C);
- **reordered, deleted, duplicated vertices** (every output vertex traced to one source: `bp_vertex_id` or an exact
  position match): every per-vertex and per-bound-vertex datum moves with its vertex, ranks / marked lists / counts
  re-derive, the part is laid out again and the mapping's offsets and counts are patched into the rebuilt image;
- **refused**: a cloth vertex with no source (`cloth: entry E (cloth mapping K): vertex N is new …` — its binding would
  have to be computed), a vertex used by two submeshes (+0x50 holds one), a cloth part outside the layout rule.

**Open:** the meaning of E58/E5C/E6C/E60/E64/E7C, S*.B0 and S3's extra arrays (and which set is structural / shearing /
bending — the kernel list runs S2, S1, S0 in that order); the exact binding formula (+0x18..+0x24: a geometric projection
onto the simulation triangle gets within a few quantisation steps, not bit-exact), which recomputing the binding of a
moved or new vertex needs; mask +0x28; the float triple of each constraint set; collider type numbers → shapes; whether
the engine reads the +0x10/+0x40/+0x44/+0x48 copies at all (the renderer copies base positions from the vertex buffer).
**Untested in game:** a remapped part (only the self-check and the census invariants back it).

## Paks and `.model`

`Core/Model`. Both games.

**Paks.** `GameInstall.Paks`: the stock `dataN.pak` files in numeric order; with runtime modding on, the paks
NightrunnerProxy's mods mount follow in mount order (`RuntimeContent.Paks`). `PakIndex` reads the zip with
`System.IO.Compression` (members over 16 MB refused, reads serialised per pak). `ModelCatalog` lists every
`.model`/`.models` member; members sharing a basename (ASCII case-insensitive) override one another and the later pak
wins (`ModelEntry.OverriddenBy`, `Wins`). Later-pak-wins was verified in game with the earlier runtime (removed) for
root-level members only; pending current-runtime validation.

**Document** (`ModelDocument`): keeps the parsed `JsonObject` (unknown fields survive) and gives typed views: skeleton
(`preset.skeletonName`), slots, entries (`meshResources.resources`), `materialsData`, `materialsResources`, `rttiValues`
(type 7 texture `val_str`, 2 float, 4 vec3), `clothResources` (see Cloth). Only version 6 is accepted.

**Drawn entry.** The engine draws the **first** entry of a slot and ignores `selected` (verified in game with the earlier
runtime (removed), 2026-09-16; pending current-runtime validation). `ModelSlot.Drawn` is the first; `Chosen` keeps the
predecessor's pick (selected, else first). No stock model has more than one entry in a slot (both games), so this only
matters for built models; the build moves the chosen entry first.

**Material resolution**, per submesh: embedded name → (the mesh's current skin) → `materialsData.number` →
`materialsResources[number]` selected/first → base `.mat` + `rttiValues` → SDB bindings (first texture per parameter
over every variant) → type-7 overrides replace a bound parameter or add an unbound one → catalog lookup.

**Census** (`MeshCheck --models`, stock paks; the predecessor's `PakIndex`/`mesh_refs` gives the same numbers):

| | models | parsed | slots | mesh refs | slots with >1 entry | slots with cloth | refs found in rpacks |
|---|---|---|---|---|---|---|---|
| DLTB (data0, data1) | 815 | 815 | 7,220 | 6,626 | 0 | 286 | 6,528 (86 names not shipped) |
| DL2 (data0, data1) | 2,330 | 2,330 | 32,887 | 23,275 | 0 | 0 | 23,239 (28 names not shipped) |

**Merged skeleton** (`ModelSkeleton.Merge`): the preset skeleton's bones, then every drawn part's missing bones by name.
Rest poses follow the rule in [porting.md](porting.md#divergences) (*Rest pose of an assembled model*).
`player_kc_basic_tpp.model` (DLTB): 497 bones, 258 rest poses from parts; skinned-vertex shift under the merged rest —
head 0.005 mm, beard 1.343, eyebrows 0.001, torso 0.101, shoes 0.234, pants 0.219 (`ModelSkeleton.MaxShift`).

## SDB (shader / material database)

`Core/SDB` reads `runtime_dx11.sdb` / `runtime_dx12.sdb`: an `MDBR` container wrapping an `MDB` stream of 39 tables
(DLTB) or 38 (DL2 — `0x48` absent, `0x62` 43 B, `0x28` 25 B). **The inner MDB version word selects the layout, never the
selected game**; an unknown one is refused by name. A DL2 stream labelled with the DLTB version word throws, and vice
versa. Twelve tables are interpreted; the other 27 are exposed as bytes and never reinterpreted.

- **Block A (≤ 25 MB) is copied into a managed array** at open; block B (the DXBC program blobs, up to 335 MB) stays
  memory-mapped and is touched only when a blob is asked for.
- A flat table needs no per-record bookkeeping (offsets are arithmetic); the variable shapes get two `int[]`.
- `SdbIndex` is the immutable snapshot the panels read — lowered name blob plus start/length arrays, per-material preset,
  preset → materials. Built on a worker and published by reference assignment, so readers need no lock.
- **There is no writer.** Table growth and new strings are undecoded, the `0xD2` value blob is shared between materials,
  and where a replacement database would have to live is unknown. `SdbMaterials.CanCreate` is `false`.

Measured against both installs (2026-09-22), matching the predecessor's reader counter for counter:

| | DLTB dx11 | DLTB dx12 | DL2 dx11 | DL2 dx12 |
|---|---|---|---|---|
| open + full table walk | 30 ms | 36 ms | 24 ms | 36 ms |
| materials | 26,670 | 26,665 | 25,760 | 25,741 |
| presets | 791 | 791 | 523 | 523 |
| resolve every material | 896 ms | 1,359 ms | 823 ms | 1,119 ms |
| parameters (all declared, all typed) | 348,774 | 348,774 | 359,453 | 359,453 |
| texture bindings | 1,858,898 | 1,858,900 | 1,539,248 | 1,539,248 |
| distinct texture names | 59,173 | 59,173 | 50,565 | 50,565 |
| runtime-resolved names used | 0 | 0 | 0 | 0 |

Every material has exactly one route in all four databases. `SdbCheck` re-runs the whole thing.

**`.mat` text** (`Core/SDB/MatWriter.cs`): DevTools material source rebuilt from the SDB — `preset("<route tokens>")`, then
one line per parameter the route sets: strings `name("x.png")`, floats as the stored single widened to double with 10
decimals (`0.8899999857`), vectors `[a, b, c]`. Checked against the 630 `.mat` files under DL2 `DevTools` (21 distinct
materials also in the shipped DL2 database, each present three times): 16 match line for line as sets and 11 byte for
byte; 4 differ because the shipped material was changed after the DevTools snapshot (different token string), 1 because
DevTools sets a value equal to the preset default, which the compiler drops. Line order is the author's and is not
stored; preset declaration order reproduces it for 11 of 16. Written: DLTB 26,549/26,670, DL2 25,651/25,760 materials;
the rest set an `int` parameter, and no source shows how one is written, so they are refused by name.

**Shader facts used by the viewer** (read from the stock pixel shaders): the diffuse sample is multiplied by a constant
(`dif_0_val`, "Diffuse scale") before the Fresnel and dye layers. Gradient maps: `sample(idx_0_tex).r` → "Index
modifiers" (two saturated affine steps: `idx_0_range_in_*`, then `idx_0_range_out_*`) → `sample_l(grd_0_tex, u =
index·scale + offset, v = an interpolated row)`, blended over the base by the gradient's alpha; the row also takes a
per-instance offset (`grd_0_usr_on`, NPC colour variants) that only exists at runtime. The dye gradient (`idx_tex` →
`grd_tex`) multiplies the colour and reads its index on UV1 by default (`grd_uv_1_on`). How the viewer uses these is in
[architecture.md](architecture.md#viewport).

## Prefabs

`Core/Prefab`. Binary `Prefabs` resources (type `0x61`: part `0x61` primary image, part `0x62` = 8-byte prefix +
ClassReader metadata + secondary image), and DL2's text prefabs in paks. The predecessor has no prefab support; the
format was worked out from the DLTB engine (see the [appendix](#appendix-engine-references)) and checked against the data.

**Container** (`PrefabContainer.cs`): parse, `Validate()` (the loader rules: roots first, record partition, direct slots
before indirect, allowed slot kinds, object count, alignments — refused by name) and a byte-exact encoder. It has its own
reader because part `0x62`'s alignments count from the part start (the mesh `Fixups` class does not fit). Round trip
(`PrefabCheck`): **23/23 DLTB** and **50/50 DL2** resources byte-exact, both parts. `Validate()` and the edits keep the
DLTB rules and refuse the DL2 layout by name; the decoder reads both.

Corrections to earlier assumptions, measured on DLTB: the primary image is only 8-aligned (not 16) in 6 of 23 packs;
`CRTTIFieldVirtual<T>` is not 0x68 bytes for 12 types; bindings are 0x20 apart, not 0x18; entity → prefab pointers
outside `common_prefabs_pc` go by class name (kind 9 → `0xC0000042`), so only `common_prefabs_pc` is a closed graph; the
"primary direct slots, then secondary" grouping and byte-sorted names hold only in some packs (only "direct before
indirect" always holds); root `+0x258` also takes 3, and `+0x259` = `0x08` marks `*_lod0` roots.

**Decoder** (`PrefabDecoder`, `PrefabModel`): prefab roots (name, base class, flags), fields, components (class, pcid,
component class, property value blobs), transforms, child entities (instanced prefab by pointer or, outside
`common_prefabs_pc`, by class name), interfaces, pipes, bindings, virtual fields, preset sets. DLTB coverage — slots read
as named fields: 85.79 % in `common_prefabs_pc`, 73.2 % over all packs; every other slot still resolves to text, an
object or an inline block. Engine-field-keyed values stay raw (their widths are the engine's and are not known).

### DL2 binary layout

`PrefabLayout` (measured on the data; no DL2 binary was read). `PrefabLayout.Detect` picks DLTB when `LayoutProblem()`
is null, DL2 when every root is a `cbs::Prefab` at a 0x228 stride and the secondary image holds only B1/42/43/0x70
elements, and refuses anything else by name. The field census (slot kind and target class per offset, `--fields`):

| | DLTB | DL2 |
|---|---|---|
| `cbs::Prefab` size / stride | 0x260 | 0x228 |
| root fields from `m_pBaseClass` on (base class, interfaces, components, properties, container size, pipes in/out, bindings, virtual fields, DOM format + flags) | +0x120 … +0x258 | 0x38 earlier: +0xE8 … +0x220 (`m_Fields` stays +0x28, name +0x10) |
| `cbs::PrefabEntityComponent` | 0x128, forced GUID +0x120 | 0x120, no GUID (`ForcedGuid` 0); +0x90 → `0xB0000002` block of 0x70 pair handles, +0xC8 → a CoWorldSpawner, +0x108 (kept raw) |
| value blob terminator | {−1, −1} | {0, 0} (184,607/184,607 blobs of `city_persistent_pc` end so) |
| element text | tagged 0x2100, kind 14 | untagged kind 12 (the same `{len, cap}` text); the empty string is a zero word (DLTB: byte 7 = 0x07) |
| secondary classes | B1, 42, 43 | + `0xC0000070` `{out, string_base, string_base}` (0x18): pairs such as `physics`/`Disabled` (read as `a:b`) |

Components, xform (+0x40/+0x4C/+0x58), hierarchy parent (+0x68), entity name/prefab/presets/configurations
(+0x68…+0x80), `m_EntityPrefab` (+0x98), parent pcid (+0xB8), fields, virtual fields and presets have the DLTB offsets.
Each was checked against the text prefab of the same name: 318,990/318,990 transforms, 318,335/318,335 entity names,
prefabs (6 differ only by a `.prefab` suffix the text keeps), presets, configurations and parent pcids agree,
3,961/3,961 hierarchy parents; the text `m_EntityComponentsExtents` equals +0xD0 on 204,683 and +0xE8 on 8,460 of
213,143 entities.

**`m_XformComponent` (both games):** u32 +0x40 of mesh render (`0x17`), mesh logic (`0x16`), mesh editor helper (`0x15`),
area (`0x03`), box (`0x05`) and point (`0x1D`) components, and +0x1A8 of `0xC0000028` (CoWorldSpawner), is the pcid of the
CoHierarchy* / CoWorldXform* / CoSkeleton component it uses (DL2: equal to the text's on 20,524 of 20,566 components; in
both games every non-zero value names such a component of the same prefab).

DL2 decode: **50/50** resources, no warnings, 44,393 prefabs, 7,889,035/12,361,082 slots typed (63.82 %;
`common_prefabs_pc` 83.16 %) — the untyped rest is mostly the DL2-only entity fields above, `0xC0000028` and the
`0xB0000002` blocks; 4.6 s for all packs (Release).

**`PrefabHierarchyComponent`** (0x78 bytes, observed, DLTB) is the xform component's 0x68 bytes plus a u32 parent pcid
at +0x68: over DLTB 6,329 records, 4,894 parent 0 (root), 1,435 a pcid of the same prefab, none self; only 7 have a
non-identity transform, and 5,002 of 169,402 entities hang from one.

**Transforms**: `mtx34::rotation_xyz(x, y, z)` = `Rx·Ry·Rz` in degrees, columns scaled, translation added (the engine's
`PrefabXformComponent::CreateXform`), chained through the entity parent pcid or the hierarchy component's parent (this
chaining is the port's reading).

### Text prefabs

`PrefabTextReader`, `PrefabTextNode`, `PrefabYaml`. DL2's paks hold 116,510 `*.prefab` members (data0 89,016, data1
27,494; 1.26 GB inflated); DLTB's hold one, `prefabs/game.prefab` (YAML, no components). The format is picked as the
engine's `dom::CreateReaderInSitu` does: `{` → JSON (99,063), `MsgP` → MessagePack (17,144; each ends with one NUL after
the root value), else YAML (303; the block subset the files use — anchors, tags, block scalars and flow maps refuse by
name). One DOM keeps key order and **duplicate keys** (`EmbeddedObject`, `Bundle`, `Mesh` repeat inside components).
Schema (`Version` 4: 59,096, 3: 21,037, 2: 5,315, 1: 2,028, absent: 29,033):

```
{"__filename__": {Version, RuntimeData: {Components: [{Class,
    PrefabFieldsNative: {name: [ERTTIType, value]} | PrefabFields: {name: "text"} (older),
    Fields: {name: "text"}, Presets | EmbeddedObject | … }],
  Interface: {VirtualFields: [{Name, Init, DestinationFields: [{Uuid, Name}]}], Properties, …},
  Bindings: {Properties, Pipes: [{src_uuid, src, dest_uuid, dest}]},
  PrefabInterfaces, Extents, SpawnType, …}, PrefabEditorData},
 "<sub-prefab>": {…}}
```

The oldest YAML files put the `RuntimeData` members in document 1 and the editor data in document 2. Read into the same
`PrefabDocument`: `m_PcId` (or `m_Uuid`) → pcid, `m_Translate/m_Rotate/m_Scale` → xform, `CEntity` → an entity (`m_Name`,
`m_PrefabName`, `m_PresetNames`, `m_Configurations`, `m_ParentComponent`, extents), `m_ParentComponent` → hierarchy
parent, `m_XformComponent`, `Fields` → text values (keys without `class::`), every native field kept with its ERTTIType,
virtual fields, bindings, interfaces; what has no model member (`Presets`, `EmbeddedObject`, `PrefabEditorData`,
`Extents`, …) is kept as its JSON text.

Census (`PrefabCheck --game dl2 --text`): **116,509/116,510 read**; the one refused is
`prefabs/encounters/exterior_defend_handcraft_encounter.prefab` (a string that is not UTF-8); 98.43 % of DOM members read
into model members; 7,793 files carry 18,964 sub-prefabs (11,398 of their names are also binary prefabs).

**Relation to the binary prefabs** (DL2): 32,575 names are both a text member and a binary prefab, 83,935 text only; of
44,393 binary names 425 are neither a member nor a sub-prefab (city-builder `_flt` / `_lod0` / `genpin` outputs). The
binary is the compiled text: it drops editor components (45,562 text components have no binary pcid; 4,319 of the
32,575 differ in component count) and prefixes value keys with their class (958,386/968,954 text values found by key;
196,559/196,559 text values equal).

**Load order.** In the DLTB engine a lookup asks the registry (binary prefabs, first registration wins) and only on a
miss opens `<name>.prefab` (`LoadPrefabFromFile`); `PrefabManager` enables rpack prefabs when `common_prefabs_pc` exists,
which DL2 ships. The port assumes the same for DL2 (not read in a DL2 binary): the binary prefab shadows a text member
of its name. `PrefabCatalog.Build(rpacks, paks)`: binary entries first (pack order, first wins), then one entry per text
member (name = file name, lower case, no extension; between paks the later wins), each with its source and, when
shadowed, `ShadowedBy`. Text members are read on first use. DL2: 160,903 entries (44,393 binary, 116,510 text; 32,575 text
shadowed by binary). Class presets are looked up across every binary resource by class name (`.pre` text preset files,
8,437 in DL2's data0, are not read).

### Placement

`PrefabPlacement`, both games, one path. Three data-driven mechanisms, each measured:

1. **`m_XformComponent`** (+0x40 of `cbs::PrefabComponentWithXformPtr`, named so by the engine's class-writer compare): a
   component without its own transform is placed by the xform / hierarchy / skeleton component it names (the parent
   chain is entity parent pcid → hierarchy parent → `m_XformComponent`). DLTB prefab roots: 300 of the 333 mesh
   components that name a `.msh` attach so (MeshRender 182, CoMeshLogic 83, CoSimpleMeshLogic 26, CoSimpleMeshRender 9;
   424 editor helpers aside); the 33 that do not are CoSkinnedMesh 29 (class `0xC0000023` has no such field), CoSensable
   3, CoCustomMove 1 — they stay at their prefab's origin. No mesh component names a bone: the only bone/element-valued
   fields in DLTB prefabs are `CoActorAttachInfo::m_ElementName` (3, an actor's hand holder) and
   `dead_ragdoll::m_JointBone` (2), neither a mesh placement.
2. **Virtual fields.** An instancing entity's own values, the presets it names (`m_PresetNames`, `group;preset` pairs,
   looked up in every binary resource's class presets) and its parent's overrides aimed at it reach the child's
   components through the child's `m_VirtualFields` (destination field on a pcid; an entity destination forwards again;
   entity values win over presets, outer over inner; a qualified key that names no virtual field falls back to its field
   name). Overrides feed `m_MeshName`/`MeshName` (on any component — DL2 containers set a model proxy's `MeshName`),
   skins, `m_SelfActive` and `LodVisibility` (text values: on unless "0"; binary: low byte).
3. **Vehicle rig** (`PrefabVehicleRig`, needs meshes + pak scripts). A vehicle proxy takes hierarchies by property
   binding (`H.this → P.m_WheelFLHierarchy` …, `m_VisHierarchy`); at runtime the vehicle code resets its model to the
   reference frame and hands each hierarchy the matrix of a model element (read in the game DLL, see the appendix: the
   suspension defaults `axis_fr/fl/rr/rl`, `Bone_door_*`, … `Bone_root`; `mesh_wheel_*_helper`, `mesh_door_*_helper`,
   `mesh_chassis_helper` map onto them; the hierarchies registered as `m_VisHierarchy`, `m_Wheel{FL,FR,RL,RR}Hierarchy`
   get the chassis/FL/FR/RL/RR elements). The element name comes from the proxy's script: `m_ScriptName` →
   `LoadParams("…")` → `ParamString("mesh_wheel_fl_helper", "Bone_wheel_fl")` (pickup vis params), else the engine
   default; the mesh is the proxy's `CModelObject::MeshName` (with overrides); the element lookup ignores case (params say
   `Bone_wheel_fl`, the mesh has `bone_wheel_fl`). The hierarchy is placed at the element's rest global; the wheel meshes
   on it follow (`m_XformComponent`). Right wheels come out mirrored (the pickup's right bones carry diag(−1, 1, −1)).
   **Doors** hang on their hinge elements the same way (`mesh_door_*_helper`, default `Bone_door_*`). Door geometry sits
   under a child entity (`…_door_fl.001`) whose rotation turns the door's +X to −Z; the closed pose is in the mesh. The
   hood and tailgate likewise sit under hinge entities. **Skin preset**: a vehicle whose rig resolves also loads its skin
   preset's `PrefabsToLoadStrings` from `scripts/vehicles/vehicle_skin_presets.scr` (`VehicleSkinPreset("…") { … }`;
   entries `prefab`, `prefab;group;preset` or `prefab;group;preset;hierarchy`, the hierarchy a proxy field without `m_`,
   attached there, else at the vehicle root). The game takes the skin from the player's inventory at runtime; the viewer
   uses `Default`. Anything unresolved (no model mesh, no script, missing element, skin prefab not registered, hierarchy
   not bound) is a note, never a guess.

Example (DLTB): `dlc_ft_vehicle_truck` alone names no script (its own MeshName `veh_truck_a_drive.msh` is in no pack):
24 meshes, note "no vehicle script". With its class preset `Preset;Vehicle_Baron` (MeshName
`dlc_ft_veh_drivable_pickup_b_base_ch.msh`, script `vehicle_pickup_script.scr`, simset `pickup_drivable`): 40 meshes,
98,682 triangles, the four wheels at (±0.717, 0.423, 1.708) and (±0.717, 0.423, −1.452) — the `bone_wheel_*` elements —
the doors closed on their hinges and the Default skin's panels and lights.

Over all 26,195 DLTB prefabs: 326,065 placements, 63 rig/skin notes. Most placements go through `m_XformComponent`; a
few hundred sit under a rig hierarchy; the rest are at their prefab origin (mostly model proxies drawn through
`MeshName`, and CoSkinnedMesh). DL2 (44,393 binary prefabs): 1,798,939 placements. Left out when drawing: components
and entities whose `m_SelfActive` is 0 (start inactive — damage variants), meshes with a `LodVisibility` value (distance
stand-ins, kept when they are all a prefab has), and a mesh a second component draws at the same place (mesh-render +
mesh-logic pairs).

### Edits

`PrefabEdits`, DLTB binary only; DL2 and text prefabs are refused by name. Structural: rename and duplicate (the
duplicate copies 6 records and 60–61 slots; whole record spans would also take neighbouring prefabs' data). Values
(`PrefabEdits.Values.cs`, `PrefabEditOp`; `Apply` validates once at the end, `Check` confirms a re-parsed value):

- **transform** of an entity / xform / hierarchy component: the nine floats at +0x40, in place (extents untouched).
- **text** (`m_MeshName`, `m_SkinName`, any text value): a pstring value's kind-9 slot word is retargeted at an existing
  pstring element with that text (shared) or at one appended to the secondary image; a `string_base` value (tagged
  0x2100 slot into primary text) gets its text appended to the primary image. No length limit; only the 8-byte word
  changes in place. Empty text (a null has no slot) and class/field references are refused.
- **scalar**: float (ERTTIType `0x09`, low 4 bytes) and bool (`0x0B`, low byte) field values; engine-keyed values (width
  unknown) and every other type are refused.
- **active** (`cbs::CComponent::m_SelfActive`): an existing value's low byte (payload must be 0/1); a missing one is
  inserted before the blob terminator — the primary image grows by 16 at that point and every later record, slot and
  pointer target moves by 16 (alignment kept); the key's field element is shared, or appended (mode byte 0, as 4/4 shipped
  `m_SelfActive` elements). Refused when the blob holds out-of-line values after the terminator (blob-relative offsets,
  engine-keyed ones indistinguishable) or the component has no blob.
- **remove** a child entity: the component vector's later pointers move down with their slots, size/capacity (and a
  pointer-array record's count) drop; the object stays in the image, unreferenced. Refused when anything names its pcid
  (parents, `m_XformComponent`, bindings, virtual/field destinations), when the prefab has interfaces or properties (their
  pcid lists are not decoded), or for the last component.
- **Refused**: adding entities or components (new pcids, extents and a grown component vector), adding values other than
  `m_SelfActive`, engine-keyed scalars, DL2 / text prefabs.

Measured: `PrefabCheck --edits` applies each kind alone on the first candidates of every DLTB pack and checks validate,
byte-exact re-encode, the re-parsed value and that every other prefab decodes as before — 53/53 on 23/23 packs
(transform 16, active 17 of which 14 inserted and 10 appended the field element, remove 15, text 3, scalar 2). A
transform changes 5 or 12 bytes, a text retarget 3 bytes (+1 record, +1 slot); an insertion shifts the rest (e.g.
`common_prefabs_pc`: 6.2 M bytes after the insertion point move, every other prefab identical). Tests pin the byte ranges
on a synthetic resource. **Nothing edited has been loaded in the game.**

### Prefab loading options

How an edited `Prefabs` resource could reach the game. None is chosen or verified in game. The engine facts (DLTB): each
pack's `Prefabs` registers at pack registration (`RegisterPrefabsData` → `RegisterPrefabResource`, only when
`common_prefabs_pc.rpack` exists); the registry keeps the **first** registration of a name and silently drops later ones
(one observation recorded 17,140 entries, one image per pack; method and build not recorded); `ReleasePrefab`
unregisters **by name** when a pack unloads; a lookup that misses the registry falls back to `<name>.prefab` text
(`LoadPrefabFromFile`), except `_cb_region_` + `_lod0` names.

1. **A new rpack with the whole edited resource** (what the project build writes). Its prefabs register when the pack
   registers. Edited names that already exist win **only if the mod pack registers before the shipped one**; the
   registration order of prefab resources is not known. If it registers later, every name is a duplicate and nothing
   changes. Risks: class presets register again (duplicate `CRttiClassPresets` handling not read); a streamed `reg_*`
   pack unloading unregisters the name for everyone.
2. **New names only** (duplicate + edit, then rename): no collision, so order does not matter; but nothing places the new
   name until something references it — an edited referencing prefab (back to option 1 or 3) or a pak script
   (city-builder / configurator scripts name `.prefab`s; paks override by basename, later pak wins).
3. **Rebuild the shipped pack** (`common_prefabs_pc` or the `reg_*` holding the prefab) with only its `Prefabs` replaced
   (the rpack writer has a preserve mode; building such a pack is not wired up). No registration-order question, but it
   replaces a game file (Nightrunner never writes into the game folder: the user would install it), must be redone after
   every game update, and is 37 MB for `common_prefabs_pc`.
4. **Text `.prefab` in a mod pak**: the miss fallback compiles text at runtime (JSON/MsgP/YAML; DLTB ships
   `prefabs/game.prefab`). It only serves names **not** in the registry, so it cannot override a shipped binary prefab.
   Needs a text writer (not written; the reader exists), and it is not known whether the engine's file lookup finds a mod
   pak or loose file, or what `Fields` value syntax DLTB accepts (DL2's text prefabs show one).
5. **Runtime** re-registration through a runtime mod framework (out of scope for Nightrunner's offline tooling).

Unverified for all: that the engine accepts a relocated resource (it validates nothing — a mistake crashes), that an
inserted `m_SelfActive` entry is read as the engine field, and that a removed entity's orphaned object is harmless.

## Animation (ANM2)

`Core/Anim`. The predecessor only dumps ANM2 header words; the format was worked out from the engine (see the appendix)
and checked against the data.

**Clips** (`Anm2*`): type `0x40`, and the `0x44` + `0x45` stream pair (the same bytes, 40,083/40,083 DLTB). `Anm2Payload`
is the stored form (segments, static blocks, bias/scale, 16-row key blocks) and re-serialises byte for byte; `Anm2Clip`
holds the values (stereographic rotation → xyzw, parent-local metres), keeping the second stored copy of seam and
hard-cut keys; `Anm2Encoder` implements the shipped recipe and, by default, reuses the source's constant/animated split,
segment layout and in-grid bias/scale, which makes **every** shipped clip re-encode bit-exactly (DLTB 94,560/94,560
copies: 94,536 V3, 6 V2, 18 V0; DL2 108,896/108,896: 107,390 V3, 1,408 V2, 26 V1, 72 V0). Edited data falls back to the
recipe (alone it matches 99.887 % of stream bias/scale pairs and 99.72 % of layouts); a fresh encode of 3,500 clips stays
within 0.0042° and 5e-4 m. Bones bind by `h41` name hash. Against `sh2_player_tpp_phx_skeleton`, frame-0 translations of
88 idle clips match the bind locals to a median of 5.1e-7 m (84 % within 1e-5 m; rig-driven bones — hand holders,
pelvis, clavicles, cameras — move). Decode: ~0.4 ms median, 3 ms for a large player clip. `AnimCheck` reruns the census.

Codec details that differ from a naive reading: the scale streams use a 2e-4 floor (not 2e-5); unused lanes hold
−32768; a partial last key block stores k+2 rows; a "row-15" key is a hard cut on a block boundary, not an encoder bug;
134 clips segment one block earlier than the greedy rule (the engine's size test is not the final size — unresolved).
The constant-stream count is header vtable slot 15.

**Old header versions** (`Anm2Header`; the engine's `CTypeErasedAnm2Header<Header_Version0/1>` vtables and
validators, see the appendix):

- **V1** (13 DL2 clips, 26 copies; none in DLTB) is the V3 payload behind a shorter header: `+8 u16 numFrames
  (frameBound + 1) · +A numTracks · +C numBlocks · +E headerSize (bytes) · +10 u32 fileSize · +14 u16 numVfr · +16..+1F
  read by no header accessor, validator or byte-swap (zero, kept) · +20 trackHash[T] · segFrames[numBlocks] · vfrDen ·
  {len, rate}[numVfr] · pad to 16` (the pad is not always zero — kept); no numStatic, no pose list, no timeBound (taken as
  Σ len). The engine checks non-zero counts, numBlocks = ceil((fileSize − headerSize) / 64 KiB), Σ segFrames ≥
  numFrames − 1 and Σ len·rate / den = numFrames − 1; the reader also requires Σ segFrames = numFrames − 1 and
  headerSize = the laid-out size.
- **V0** (the `*_demo` clips: 9 in DLTB, 36 in DL2; 18/72 copies) keeps the static block in the header and has no VFR:
  `+8 u16 rate word · +A numFrames · +C numFrames again · +E numTracks · +10 static block size · +12 trailer size · +14
  numBlocks · +16 header data end · +18 u32 fileSize · +1C u32 unread (zero, kept) · +20 static block (the V3 one; track
  flags without bit 7, which no sampler reads) · trackHash[T] · u16 segEnd[numBlocks] (cumulative) · trailer (a 41-byte
  NUL-terminated string in every clip) · zero fill to 2 KiB`; the payload is `u16 off16[nblk + 1]` (key blocks and end,
  no static entry) and the V3 key blocks, except that a segment whose frame count is a multiple of 15 stores one more
  block (the last key again plus a pad row), `frames / 15 + 1` blocks in all. The engine only checks nConst + nAnim = 9T;
  header size = data end rounded up to 2 KiB. The rate word (30; one clip `0xBC3C`) scales time to frames by
  `(bit 15 ? bits 8..14 : low byte) / low byte` — 1 for every shipped clip.
- Refused by name (no shipped clip has them): V0 with more than one payload block, differing frame counts, or a last
  segment end ≠ numFrames − 1. Writing: V1 from any clip; V0 only from a V0 source (rate word and trailer), one payload
  block ≤ 64 KiB.
- **Checks**: DLTB ships V3 re-encodes of most of these DL2 names — 8 of 13 V1 clips decode to the same values as their
  DLTB V3 copies (7 exactly, one within 6e-8), 25 of 36 V0 clips lie within 9.2e-4 m and 0.062° of theirs (9 V0 clips are
  byte-identical in both games; 7 names were re-authored: translations 6.8 cm to 5.6 m apart, or rotations 20° to 180°);
  the V1 clip `mzi_sprint_sudden_turn_180r_mirror` and its V0 twin `…_demo` agree within 5.7e-4 m and 0.0046°. Frame 0 sits
  on skeleton bind translations for 3 V0 clips (the generic `skeleton` mesh: 53–55 bones within 1e-5 m) and 2 V1 clips
  (47/67 and 41/64 bones); the other demo clips match no shipped skeleton mesh, and 8 V1 clips drive 2–6 prop tracks.
  `AnimCheck --game dl2 --against dltb` reruns all of it.

**Sequence banks** (AnimationScr `0x42` + `0x43`, `AnimBank`): SeqTrack records, events, action lists and clip names,
written back byte for byte, junk words included (217/217 DLTB; 237,691 records, 699,881 events). Lookup is the engine's
case-insensitive binary search. The 169 tracks whose first event reads as a negative time (no event of theirs fires) are
flagged. `SeqRef` parses `<bank>.scr@<Seq>` and `<Seq>@<bank>.scr[:blend]`.

**Graph banks** (`0x47` + `0x48`, `AnimGraphBank`): fixups re-serialise byte-exactly (140/140), with clip nodes (sequence
strings through the root value table, or variable names), state machines and blend durations, interface lists. The graph
root has counts at +0x58/+0xC0/+0xD0/+0xE8/+0x110; +0x120 is the node count and +0x128 the pointer; names at +0x38/+0x40;
a second value table at +0x60. ValueRef types 6 and 8 and mode 0 exist.

**Custom resources** (`0x49`/`0x4A` ×2): header only (bank, baker DLL/function, version; 4,740/4,740); bodies kept as
bytes. `AnimBankCheck` covers banks and custom resources.

**Stream-pack copies are not byte-identical**: sequence banks differ in junk words and 373 custom-resource bodies differ
(uninitialised bytes, one layout); graph banks are identical. An edit must be applied to each copy, not copied across.

**Catalog** (`AnimCatalog`): registers each bank once, the first pack winning as in the engine (DLTB: 110 banks, 131,305
tracks; DL2: 87 banks registered, 84 stream copies shadowed, 105,756 tracks), and resolves a clip preferring the stream
copy (many player clips ship only the static copy). The engine keeps the first registration of a clip name and searches
the stream layer first, so whether a mod pack's copy wins over the shipped one depends on load order and is untested in game; see
[architecture.md](architecture.md#build).

**Clip census by frame rate** (DLTB, from sequence banks alone): 31,817 / 4,187 / 141 clips at 30/60/120 fps, 484 names
used at more than one rate.

**Additive clips**: a clip where over 90 % of the tracks (OffsetHelper aside) have zero translation at frame 0 is treated
as additive — DLTB: 9 of 23,547 bone clips, all named `*additive*`/`*_add`. Partial clips are common: 779 bone clips
drive no pelvis (592 `door_anims`, 153 `anims_player` hand/arm clips). About half of all DL2 tracks are lip-sync
pose-weight clips, which bind to no skeleton.

### Facial and lip sync

Read in DLTB's `engine_x64_rwdi.dll` (see the appendix) and checked against the data. `SFaceBone`'s layout is from the
data. No DL2 binary was read: DL2 uses the same structures and the same rule is assumed.

**Where the poses live**: in the head mesh (`sh_*` / `sh2_*` "solid head" meshes, and the engine's
`solid_head_template_v1/v2`), as `SFaceData` (image class 16, 0x28 bytes) → poses (class 17: name, case-kept h41, kind,
five per-detail bone bitsets; 0xD8 bytes in DLTB, 0x60 in DL2) and face bones (class 18, 0x20: element pointer, five
per-detail element counts, lower-cased h41 of the bone name, sorted) → elements (class 20, 0x20: quaternion xyzw,
translation, pose index). A second bone list (+0x10) holds bones with a base element only; the mix does not read it.
Pose 0 is `neutral_pose`; poses 1.. are sorted by hash. Element 0 of a bone is its bind local (every face bone of every
rig matches a bone of its mesh, locals within 8.6e-7); the others are per-pose rotation / translation deltas. Pose names:
v1 FACS-like (`jaw_opening__`, `cheek_raise_L`, 104 + neutral), v2 rig controls (`C_jaw_pY`, `L_Lips_mX`, …, 191 +
neutral) including 81 corrective `cs*` poses. Kinds: 1 neutral, 2..5 eyeball roll/pitch, 6 blink, 7 plain, 8 corrective,
9 `sw_*`, 10 `*_eye_blink_mY`.

**Pose-weight clips** (lip sync and facial expressions) carry two lists. List A is the tracks `"0".."13"` (v1) or
`"0".."22"` (v2); weight `i` is stream `i` in track-major order (track `i / 9`, component `i % 9`; the sampler's
component table is `0,1,2,4,5,6,8,9,10` into its 12-float track block). List B is the head's poses 1.. (`h41` without
lowering — `cheek_raise_L` and `_R` differ) then `wrinkles0..15`; weight `i` drives mesh pose `i + 1`. Every clip of one
template has the same list B.

**Mix rule** (one clip at full weight, highest detail; `FacialPose`): weights are sampled linearly between keys, clamped
to [0, 1], scaled by the pose's group (face; eyelids = first two kind-6 and kind-10 poses; eyeballs = kinds 2..5;
wrinkles) and dropped at ≤ 0.01. v2 correctives then set their pose to the product of their inputs
(`characters/solidhead/solid_head_corrective_shapes.json` in data0.pak, 81 shapes: 55 of 2 inputs, 22 of 3, 4 of 4; the v1
template names no config; the engine turns them off below detail 4). Per face bone over elements 1..:
`q ← q ⊗ (w·e.xyz, w·e.w + 1 − w)`, `t ← t + w·e.t`; `q` normalised; local = `(base.q ⊗ q, base.t + base.q · t)`, no
scale. The engine then writes that local over the bone's current local (face bones are not layered on body animation).
The 16 wrinkle weights feed the wrinkle normal maps, not bones. In game the eyelid and eyeball groups come from the clip
only for facial expressions whose eye source is the animation (never for lip sync); the procedural blink and look-at,
and the blend of up to two facial and two lip-sync slots, are not modelled.

**API** (`Core/Anim/FaceRig.cs`, `FacialPose.cs`): `FaceRig.FromMesh(MeshModel)` (null without face data);
`new FacialPose(clip, rig, skeleton, correctives)` refuses by name (`Anm2UnsupportedException`) a clip whose list B is not
the rig's poses + wrinkles or whose track count is not ⌈weights / 9⌉ (the engine's own check), and a corrective naming a
missing pose; `Sample(frame)` returns 4×4 row-major globals per skeleton bone like `AnimPose.Sample`; `Weights(frame)`,
`FaceLocals(frame)`, `GroupWeights`. `CorrectiveShape.Load(ModelCatalog)` + `ForRig(rig, …)` (v2 only).

**Measured**:

| | DLTB | DL2 |
|---|---|---|
| face meshes | 149: 106 v1 (105 poses), 43 v2 (192 poses) | 218, all v1 |
| face bones per rig | 126 ×46, 128 ×60, 258 ×41, 260 ×2 | 97 ×2, 126 ×60, 128 ×156 |
| pose elements | 1,003,613 | 885,596 |
| pose-weight clips (distinct names) | 11,580: 6,693 v1 + 4,887 v2 (lang_speech_en 11,084, common_anims 494, engine 2) | 49,556, all v1 |
| clips matching a head | **11,580 / 11,580** | **49,555 / 49,556** (one test clip lists its poses in another order: refused) |
| corrective config | 81 shapes | none in the paks |

(The facial census counted 21,407 DLTB and 31,766 DL2 meshes as its denominators; the per-resource mesh census above
counts 21,408 and 33,728. The scopes were not reconciled; the face-mesh counts are what the facial census found.)

Every face bone binds to a bone of its own mesh and of the model's merged skeleton (`man_basic_skeleton` + `sh_man_a`:
128/128; `sh2_woman_basic_skeleton` + `sh2_npc_ft_olivia`: 258/258 — 3 of those bones take their merged rest from the
skeleton mesh, which differs from the head's bind; the mix uses the head's base, as the engine does).
`solid_head_template_v1` holds one pose per frame (frames 17..120 at weight 1): at frame 120 (`jaw_opening__`)
`c_centerchin_bone` drops 21 mm and moves back 17 mm on `sh_man_a`. Timings (Release, warm): face-rig read 8 ms
(`sh_man_a`) / 14 ms (`sh2_npc_ft_olivia`); `FacialPose` set-up ≤ 1.3 ms; `Sample` 0.1–0.2 ms per frame.

## Audio (experimental)

Contributed by metalheadbangg. **Scope**: browse and list archives and sounds, event names, decode (through the optional
native decoder), playback, raw WEM export and WAV export. **Not in scope**: import, replacement, bank rebuild, or loading
audio at runtime — nothing writes an AESP or a bank, and NightrunnerProxy does not load `audio` mod items, so there is no
audio build output.

- `Core/Audio/AespCatalog`: every `*.aesp` under `{data}/work/data/audio` (recursive) and
  `{data}/work/data_lang/<lang>/data/audio`. A numbered row that holds a complete RIFF/WAVE is a loose WEM;
  `wwisepinhead` and `BKHD` rows are name assets; a bank's in-memory DIDX media are listed through `BankMediaReader`
  (bank version 150 only; prefetch and streamed media are not listed). Rows of other kinds (PLUG, MIDI, empty) are not
  listed.
- **AESP header** (both games, all 14 shipped archives): the table offset is the u64 at 0x88, the member count the u64 at
  0x90; the u64 at 0x98 is kept raw (0 everywhere, meaning unknown); the u32 at 0xA0 must equal the table offset (both
  hold 0xB8 in every archive), otherwise the archive is refused. The header name is read up to 128 bytes. Rows are 0x98
  bytes (offset u64 at row +0x88, size u64 at +0x90).
- `AudioNameIndex`: event names from the `wwisepinhead` XML, FNV-1 hashed, walked through HIRC v150 (Event → Play action
  → Sound/MusicTrack parents). Unverified beyond three ids.
- **Decoding** (`AudioDecoding`, `VgmstreamDecoder`) is optional: it needs `libvgmstream.dll` (libvgmstream API
  `0x01010000`) and `libvorbis.dll` beside the app, plus the Microsoft Visual C++ x64 runtime. The decoder is **never
  distributed** with Nightrunner: each user builds it with `tools/vgmstream/build-vgmstream.ps1` (see
  `third_party/vgmstream/README.md` for why and what it needs). Without it, listing, event names, search and raw WEM
  export still work; play and WAV export are unavailable ("no decoder"). The check runs once at startup and is logged.
- `AudioExporter`: `.wem` as stored; `.wav` PCM16 (WAVE_FORMAT_EXTENSIBLE above two channels, `smpl` loop, `LIST/INFO`),
  written through a temp file.
- UI: the Audio window (search, type filter, play through NAudio WASAPI, export) and its inspector.

Measured 2026-09-27: DLTB 7 archives, 51,894 WEMs (46,099 loose, 5,795 in banks); DL2 7 archives, 87,295 (82,913 +
4,382); 0 archive errors; with the decoder present, every WEM opens (Wwise Vorbis 1/2/4/6 channels, PCM16).

## Appendix: engine references

Names and addresses read in the game binaries (Binary Ninja, analysis only — Nightrunner never patches or injects).
**They depend on the game build.** Where the build was not recorded it says so; re-locate a function by its name or
strings before relying on an address. Addresses in the `0x18…` range are virtual addresses at the x64 DLL default image
base `0x180000000` (RVA = address − `0x180000000`), assuming the module loaded at its preferred base when read. DL2
binaries were not read for any of this.

| Game | Module | Build | Reference | Used for |
|---|---|---|---|---|
| DLTB | `engine_x64_rwdi.dll` | not recorded | `CTypeErasedAnm2Header<Header_Version0/1>` vtables and validators: V1 validator `0x180296700`, V0 validator `0x1801cb250`, V1 time→block `0x180223cb0`, V0 time→block `0x1802976e0` | ANM2 V0/V1 headers. All four `Header_Version*` validators, vtables and time→block functions are in this DLL; `engine_core_x64_rwdi.dll` has no ANM2 function or `Header_Version` string. Game recorded only by context (the facial analysis read DLTB's copy of this DLL) |
| DLTB | `engine_x64_rwdi.dll` | not recorded | ANM2 header vtable slot 15 | the constant-stream count (not slot 16) |
| DLTB | `engine_x64_rwdi.dll` | not recorded | `CSolidHeadController::UpdateMix`, `CAnimPoseWeightSampler::SampleWeights`, `anim_Anm2_SamplePoseStreams`, `CAnimWeightedSum`, `FacialExpressionManager::ProcessTemplateFile` / `LoadCorrectiveShapeConfig` / `GetPoseIndex` / `GetStreamIndex`, `CoSkinnedMesh::UpdateFaceWrinkles`, `UpdateBlink`, `UpdateLookAt` | facial mix rule, list A/B, correctives, wrinkles |
| DLTB | `ResourceCore_x64_rwdi.dll` | — | `SFaceBone` | not read; its layout comes from the data |
| game not recorded | engine (module not recorded) | not recorded | `DependencyWatcher` constructors, destructor, `GetCurrent`, `SetIgnorePostfix` | real functions, not `ret` stubs |
| DLTB | ResourceManagement (module file not recorded) | not recorded | `CCompactMeshEntity::CreateClothBuffers` | uploads the whole part `0xF3` as the `ClothFile` GPU buffer; builds `ClothFileConstants` from ClothEntityInFile +0x3C..+0x7C, the constraint sets and each mapping's +0x04..+0x50 |
| DLTB | ResourceManagement (module file not recorded) | not recorded | `sub_18000d120` | the per-LOD cloth kernel list (runs S2, S1, S0) |
| DLTB | renderer (module file not recorded) | not recorded | `ClothGPURuntimeData` constructor, `InitializeBuffers`, `UpdateClothConstantsBuffer` | cloth GPU buffers |
| DLTB | engine (module file not recorded) | not recorded | `CoSkinnedMesh::GetClothData`, the collider copy, `MED::CClothResource` getters | the `.model` cloth simulation-property override (maps onto ClothEntityInFile +0x08..+0x37) |
| DLTB | `engine_x64` (file name as recorded) | not recorded | `ClassWriterCompare` of `cbs::PrefabComponentWithXformPtr` compares +0x40 under the name `m_XformComponent` | prefab placement |
| game not recorded | `engine_core` (file name as recorded) | not recorded | `PrefabXformComponent::CreateXform`, `mtx34::rotation_xyz` | prefab transforms: `Rx·Ry·Rz` in degrees |
| DLTB | engine (module file not recorded) | engine build id `0x6AA8D2A8` (as recorded) | `RegisterPrefab(string, Prefab*)` at `0x180647E80`; the other overload is `RegisterPrefab(Prefab*)` | prefab registry |
| DLTB | engine (module file not recorded) | not recorded | `RegisterPrefabsData` → `RegisterPrefabResource`, `ReleasePrefab`, `LoadPrefabFromFile`, `PrefabManager`, `dom::CreateReaderInSitu`; `CRttiClassPresets` duplicate handling not read | prefab registration, text fallback and text format detection |
| game not recorded (the vehicle analysed is DLTB's `dlc_ft_vehicle_truck`) | game DLL ("gamedll", file name not recorded) | not recorded | `SuspensionParams` constructor `sub_1812e0470` (defaults `axis_fr/fl/rr/rl`, `Bone_door_*`, … `Bone_root`); `sub_181363b00` (maps `mesh_wheel_*_helper`, `mesh_door_*_helper`, `mesh_chassis_helper` onto them); `sub_181353ce0` (resolves chassis/FL/FR/RL/RR element indices for the hierarchies registered as `m_VisHierarchy`, `m_Wheel{FL,FR,RL,RR}Hierarchy`); truck proxy `RegisterSTTI` `sub_1813d2370`; `sub_181406700` (calls `ResetBonesToReferenceFrame`, then sets each hierarchy from its element) | vehicle rig placement |
| game not recorded | engine | not recorded | `FindLogicalResourceUsingName` | RP6L name folding (ASCII `A`–`Z`) |
| game not recorded | engine | not recorded | `IL::Format` enum (70 rows) | IMGC format table |
