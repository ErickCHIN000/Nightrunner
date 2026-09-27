# Architecture and behaviour

How the application is put together and what its pipelines do. File formats are in [formats.md](formats.md); the
history of the port from the Python predecessor, and every place the C# deliberately behaves differently from it, is
in [porting.md](porting.md). Runtime modding (NightrunnerProxy) is covered in
[runtime-integration.md](runtime-integration.md).

## Solution layout

| Project | What it holds |
|---|---|
| `Nightrunner.Core` | formats and services: `Rpack`, `Texture`, `SDB`, `Mesh` (+ `ClassReader`), `Model`, `Cast`, `Prefab`, `Anim`, `Audio`, `Games`, `Backends`, `Project`, `Export`, `Logging` |
| `Nightrunner.UI` | WPF (.NET 10, x64) with Dirkster.AvalonDock 5; `Panels.cs` is the window registry; Helix Toolkit only under `Viewport/` |
| `Nightrunner.Tests` | xUnit v3; synthetic tests always run, install-backed tests skip without an install |
| `tools/*` | command-line census and cross-check tools (listed in [formats.md](formats.md)) |

Third-party packages and their licences are listed in `THIRD_PARTY_NOTICES.md`.

Ground rules the code follows: offline file work only; game installs and `DevTools` are read-only (the single exception
is the runtime settings switch, see below); unknown bytes are preserved verbatim; an unsupported case is refused by
name rather than approximated; no absolute paths in source, docs or tests.

## Games and installs

`Core/Games`. Three profiles live in `GameProfile.cs`; each carries its Steam folder, data-folder candidates,
executable templates, marker paths and a map of the folders it owns.

| id | game | data | executable | folders |
|---|---|---|---|---|
| `dltb` | Dying Light: The Beast | `ph_ft` | `{data}/work/bin/x64/DyingLightGame_TheBeast_x64_rwdi.exe` | Chrome Engine 6 set |
| `dl2` | Dying Light 2 | `ph` (then `ph_ft`) | `{data}/work/bin/x64/DyingLightGame_x64_rwdi.exe` | Chrome Engine 6 set |
| `dl1` | Dying Light | `DW` | `DyingLightGame.exe`, `DevTools/DyingLightPlayer.exe` | not defined (`Supported = false`): `DW/Data*.pak`, no `work/data_platform` tree |

The Chrome Engine 6 set (`install.Paths.Folder(GameFolder.X)`; `{data}` is the game's data folder):

| `GameFolder` | path |
|---|---|
| `Assets` | `{data}/work/data_platform/pc/assets` |
| `Audio` | `{data}/work/data/audio` |
| `Data` | `{data}/source` |

All three are required. `GameInstall.Find` locates an install from a saved root, then `NIGHTRUNNER_GAME_ROOT`, then the
Steam libraries. `install.Validate()` locates the executable first, then the content folders, and returns a
`PathReport` (`ok` / `missing` / `absent` / `n/a` per entry). Roots the user picks are kept in
`%APPDATA%\Nightrunner\settings.json` (`GameSettings.cs`) and win over detection on the next start.

`GameInstall.Rpacks()` is every `.rpack` under the assets folder, recursively, sorted by relative path (the
predecessor's rule); NightrunnerProxy's folder is not part of it. `GameInstall.Paks()` is the `dataN.pak` files in
numeric order.

### Runtime modding (pointer)

The runtime that loads what Nightrunner builds is a separate project, **NightrunnerProxy**. Nightrunner only reads its
installation (`RuntimeModding.cs`, `RuntimeContent.cs`, `RuntimeSettingsFile.cs`) and, with runtime modding on for a
game, shows what the runtime would load and lays out build output as a mod folder. The only write into a game folder
anywhere in Nightrunner is the Mods window's on/off switch, which edits NightrunnerProxy's `nightrunner.json`
(`RuntimeSettingsWriter.cs`), never while the game runs. Only DLTB has a runtime module. The detection rules, the
`nightrunner.json` and `mod.json` contract, load order and the switch are documented in
[runtime-integration.md](runtime-integration.md).

With runtime modding off for a game, nothing from its mods folder is shown anywhere: without the runtime the game
cannot load it.

## Backends

`Core/Backends/IGameBackend.cs` is the per-game swap point: one backend per game, resolved by `GameBackends.For(profile)`,
carrying `IPackSource`, `ITextureBackend`, `IMeshBackend`, `ISdbBackend`, `IMaterialBackend`. A capability a game does not
have is `null`.

- `ChromeBackend` serves DLTB and DL2 (RP6L packs, IMGC textures, runtime SDB, SDB materials) and carries their
  differences as data: DLTB meshes encode, DL2 meshes are decode-only with a refusal text.
- `DyingLight1Backend` is a slot: `Implemented = false`, every capability `null`. Its root validates down to the
  executable, and the game selector shows the card disabled.

Format decoders branch on data (layout detection, version words), not on the game id. The project build is the
exception: meshes, model scenes, `.model` overrides, clips and prefab edits are built for DLTB only, and the build and
the Build overview refuse them for any other game by checking the id `dltb` (`ProjectBuild.cs`, `BuildOverview.cs`).
Textures build for both games.

## Pack access

- **x64 only**: the UI pins `PlatformTarget` x64 / `win-x64` and refuses to start in a 32-bit process — packs are
  memory-mapped, and an x86 build runs out of address space on a full install.
- Packs are opened with `MemoryMappedFile` and never read whole: only the table region is copied into managed arrays
  (a few MB per pack); payload bytes are faulted in on demand.
- Names stay as bytes in the pack's name blob plus a pre-lowered copy for search; strings are built only for rows
  actually shown. Search is a vectorised `Span<byte>.IndexOf` over that blob across every pack in parallel (all words
  must match; `#123` matches a logical index).
- Resources are addressed by a global id, `gid = pack.Base + logicalIndex`, as in the predecessor's catalog.
- `RpackCatalog.Lookup` uses a folded-name index built per pack on first use.

## Workspace, panels and the Inspector

`Workspace` holds the open game, its backend, its `RpackCatalog`, the model/prefab/animation catalogs and the decode
caches; swapping games raises `Opened` and views rebind. Every area is a dockable window declared in `Panels.cs` (id,
title, factory, placement): Raw, Textures, Materials, Meshes, Models, Prefabs, Animations, Audio, Viewport, Projects,
Build, Mods, Inspector and Log. A new window is a `UserControl` plus one row there.

**Selection** (`Selection.cs`) is the only coupling between lists and the Inspector: a panel calls `Set(source, what)`
with a `Selected.*` value (pack, resource, texture, material, mesh, model, sequence, prefab, audio archive or entry,
project, project model or scene, build item, mod) and the
Inspector picks the matching view. Nothing registers and nothing subscribes both ways. For jumps between windows,
`PanelContext` carries an `IPanelHost` (`MainWindow`) with `Open(panelId)` and `Reveal(panelId, payload)`: `Reveal`
opens and fronts the window and hands over the payload through a `dynamic` call; a window that does not understand it
ignores it.

**Workspace views** are named arrangements (which windows are open, which side each is on, pane sizes, the front
document). The built-in ones are `LayoutStore.BuiltIn`; saved ones live in `%APPDATA%\Nightrunner\layouts.json`. The
document area is stored as a tree (a leaf is a pane with its tabs and the front one; a branch is a split with its
orientation and weights); a flat `documents` list is kept beside it so older files still read. AvalonDock's own layout
XML is deliberately not used: version 5 has no public serializer, and this description survives panels being added or
renamed.

## Logging, crashes and jobs

- **Log** (`Core/Logging/Log.cs`): a ring buffer of the last 5,000 entries plus a `Written` event. Long operations use
  `Log.Start(source, what)`, which writes a `start` line and, when disposed, a closing line with the result and the
  elapsed time.
- **Crashes** (`Core/Logging/CrashReport.cs`, hooked in `App.xaml.cs`): an exception left unhandled on the UI thread, on
  any thread, or in an unobserved task is logged (`crash`) and written to `crash-<time>.txt` beside `settings.json` with
  the process line (version, bitness, runtime, working set), the front window, the running job, the game, the stack and
  the last 100 log lines. No minidump: a native fault never reaches these hooks; setting `DOTNET_DbgEnableMiniDump=1`
  before launch makes the runtime write one.
- **Job queue** (`Nightrunner.UI/Jobs.cs`): exports and builds run one at a time off the UI thread, report progress in
  the status bar and are logged. Cancel is checked between files; a cancelled export keeps what it already wrote.

## Export

**Raw export** (`Core/Export/RawExporter.cs`) writes part bytes straight from the memory map, in the predecessor's layout:

```
<out>/<pack>/<family>/<index>_<name>/<part file>      e.g. common_meshes_pc/mesh/009232_sh2_player_tpp_phx_skeleton/image.bin
```

Part file names follow the predecessor (`image.bin`, `fixups.bin`, `skin.bin`, `vertex.bin`, `index.bin`, `header.imgc`,
`bitmap.bin`, …). Existing files are skipped, so an interrupted export can be re-run; compressed storages and `.rpacz`
child parts are counted as unreadable, never invented.

**Mesh and model export** (`Core/Export/AssetExport.cs`), one folder per export (`name`, `name_2`, … never overwritten):

```
<mesh>/                                  <model>/
  <mesh>.cast  <mesh>.mesh.json            <model>.model                     the pak member as stored
  <mesh>.glb                               <model>.cast  .cast.json  .glb    one scene: merged skeleton, every drawn mesh
  <mesh>.skn                               meshes/<mesh>/…                   each drawn mesh as on the left, no textures
  textures/<tex>.dds  <tex>.png            textures/…   materials/…
  materials/<mat>.mat
```

The per-mesh Cast and sidecar port the predecessor's exporter (`Core/Cast/CastExport.cs`, `Core/Mesh/MeshSidecar.cs`);
the model scene (`Core/Cast/ModelCast.cs`) uses the rest pose of `ModelSkeleton.Merge` and the report format
**`nightrunner.model_cast/2`**. `.cast.json` lists every overridden bone (`rest_overrides`: bone, supplying part, mm,
degrees) and each part's `max_shift_mm`. Albedos follow the material-aware preview (`Core/SDB/MaterialPreview.cs`: eye
layers, hair alpha hardened at its 0.25 cutoff, opacity-blended surfaces; recipe, alpha mode and cutoff recorded per
material). Textures are every texture the mesh's materials bind (full table, skin-only materials included) as DDS
(byte-identical to the predecessor) and PNG (`PngWriter`, plain RGBA). `.mat` files come from `MatWriter`. The
predecessor's Blender alpha helper script is not written.

**Animation export** writes a Cast animation (curves `rq`, `t*`, `s*` per track, absolute, the track's fps, keys re-based
to the range start; tracks named from the shown skeleton, else the player skeleton, else their hash), or a `.glb` with
the shown skeleton (else the player's) as the model-scene armature plus a glTF animation (per bound bone a rotation,
translation and scale channel, LINEAR, times = key / fps). glTF files without an animation stay byte-identical to the
predecessor's. Pose-weight (facial / lip-sync) clips are refused on export.

## Viewport

**Renderer: HelixToolkit.Wpf.SharpDX** (Direct3D 11, presented through D3DImage). It is confined to
`Nightrunner.UI/Viewport/` (`MeshViewport`, `Gpu`); everything else hands it a `SceneModel` of plain arrays, so the
renderer can be replaced without touching the panels. Helix's maintained line still builds on SharpDX 4.2, which is
unmaintained; a replacement renderer is a likely future change. One Direct3D device is shared by every viewport.

**Textures.** Albedo goes to the GPU as a DDS of the stored blocks (no CPU decode). Normal maps are decoded on the CPU
with Z rebuilt, because Helix's Phong shader samples a normal map as unsigned XYZ while the stored BC5_SNORM / BC4 maps
are signed two-channel data. Formats a shader cannot filter (integer, depth, volumes, cubes) fall back to a CPU decode of
the top level. The viewport decodes the largest level ≤ 2048; single-level textures wider than that (UI, skyboxes,
maps: 19 DLTB, 95 DL2) use the top level.

**Caches** (`Workspace`): decoded meshes (64, by global id), surfaces (512 MB) and textures (768 MB) in a
`ByteBudgetCache` (LRU by bytes); one GPU material per `SceneMaterial` and one GPU texture per `SceneTexture`, shared
by every placement.

**What is drawn.**
- A mesh: LOD 0, the selected skin (`MeshSkins.MaterialsFor`; resolution order in [formats.md](formats.md#skins-part-0x12));
  `FilterInEditor` skins listed dimmed. Materials that resolve to nothing draw untextured with a warning. `ColorI` is not
  applied as a tint.
- A model: every slot's drawn entry (the first) with the merged skeleton, in bind pose; per-slot visibility and skin.
- Unskinned geometry is placed by its owner entity's global, as the engine and the model export do (see
  [porting.md](porting.md#divergences)).
- A prefab: every mesh-render / mesh-logic component's `m_MeshName` with its `m_SkinName`, child entities expanded
  recursively (depth ≤ 12, ≤ 600 meshes), placed as described in [formats.md](formats.md#placement). Presets can be
  applied from the Inspector.
- **Mods** toggle (per viewport, default off): off resolves every name the viewport draws against the stock packs and
  paks only (`Workspace.Content(false)`); on draws what the runtime would load, mod packs first. A resource only a mod
  has is still drawn with Mods off, marked `mod`. The Inspector and a prefab's rig source always read the loaded catalog.

**Surfaces** (`Core/SDB/MaterialPreview.cs`, `ViewerSurface.cs`). The predecessor's preview recipes decide each
material's look: eyes composed from iris/sclera/veins, dithered hair and beards alpha-tested, eye shadow, wet eye and
forearm hair blended, non-rendering materials (`null.mat`) hidden; everything else is the plain `dif_0_tex` multiplied
by `dif_0_val`. The viewer alpha-tests dithered hair at 0.08 (the export keeps 0.25; see Divergences). **Gradient maps**
(`gradient_map`, not in the predecessor; C): an NPC outfit or hair material with no `dif_0_tex` takes its colour from
`grd_0_tex` looked up by `idx_0_tex`, composed in UV0 space at row `grd_0_base` (with dither coverage for hair,
`gradient_map+dither_cutout`); the per-instance row offset only exists at runtime, so the base row is shown and the
slot's note says so. Index sources other than the map or `idx_0_const` refuse by name. **Not composed, named per slot**
(`[SLOT] material: reason` in the log): the dye gradient (read on UV1), a gradient map over `dif_0_tex`, the Fresnel
gradient `grf_dif_tex` (view dependent), and materials with no colour texture at all.

**Albedo census** (`MeshCheck --materials [--game G] [--filter REGEX] [--examples]`; `--material NAME [--preset]` dumps
one SDB material, `--rows TEX` a gradient, `--levels` the level the viewport decodes). Every surface of every stock
`.model` as the Model view resolves it (2026-09-27):

| | DLTB model surfaces | DL2 model surfaces |
|---|---|---|
| total | 12,969 | 44,586 |
| drawn grey (no colour texture resolved) | 727 | 1,711 |
| gradient map composed (`gradient_map` / `+dither_cutout`) | 772 (364 / 408) | 5,675 |
| no colour texture (glass, trims, threads) | 693 | 1,496 |
| colour only in the Fresnel / second layer | 12 | 51 |
| not in either SDB / texture in no stock pack | 5 / 17 | 159 / 5 |
| plain, tinted by `dif_0_val` | 1,137 | 3,770 |
| plain, dye layer not applied | 2,650 | 7,873 |
| plain, gradient over `dif_0_tex` not applied | 113 | 1,411 |

### Animations

The **Animations** window lists every sequence-bank track with its clip. Selecting one plays it in the Viewport with CPU
skinning (`Σ w · Pose[bone] · InvBind` per vertex, normals and tangent frame following) over the track's frame range at
its fps. A pose samples a clip at any frame onto a skeleton (nlerp/lerp between keys; undriven bones keep their rest
local).

**Which model** (`AnimRigs`, a viewer heuristic, not an engine rule): a clip names no rig, so the skeletons models name
are candidates when they bind ≥ 80 % of the best rig's tracks; the one sharing most name words with the sequence and bank
wins (`fpp`, `tpp`, `banshee`, `biter`, …), then the shown rig, then the rig more models use. Bound counts alone cannot
decide: the DLTB FPP player rig is the TPP rig plus 12 `*_normal_mask` hand bones, so every TPP clip binds as well or
better on it. **Follow** (default on) switches the shown model to that pick; off keeps the shown model and reports
`n/m tracks bound` when the fit is poor. Face, prop and vehicle clips bind to no model skeleton.

**Layers**: each sequence picked with Layer on plays over the running one on its own clock. The bones a layer drives take
its pose; an additive clip composes its delta after the base local, `L = L_base · L_delta`. The engine's order is not
proven: `hold_chin_additive` over three standing idles on `man_basic` puts the right hand 0.10–0.19 m from the head with
`base · delta` and 0.18–0.31 m with `delta · base`. The graph's own layer and mask setup is not read.

**Eye**: with a skeleton that has `eyecamera`, the viewport can look through it — along the bone's −Y column, up −Z
(measured in FPP idles; the bone's scale ≈ 1.07 in some clips is normalised away).

**Facial / lip sync**: a pose-weight sequence plays on the first shown mesh whose face rig fits the clip's list B; with
nothing fitting and no body clip running, the engine's `solid_head_template_v1/v2` head is loaded. Over a running body
clip it becomes a layer that replaces the face bones' locals, as the engine's mix does. v2 heads get the corrective
shapes; lip-sync banks play with the eyelid and eyeball groups at 0 (the engine never takes them from a lip-sync clip).

## Projects

A project **is a folder** (`Core/Project/ModProject.cs`); the manifest beside it is `project.nrproj`:

```json
{ "schema": "nightrunner/project@1", "name": "...", "game": "dltb",
  "created": "...", "modified": "...", "notes": "" }
```

Optional fields: `buildFolder` (relative when inside the project; absent = `<project>\build`), `rpackName`, `pakName`
(the names the last build used). Unknown manifest fields are kept on save. `Create` makes the folder plus
`textures mesh model anim sdb audio other`; `Scan` reports what is on disk; contents are never tracked in the manifest,
so files dropped in with Explorer show up on the next scan. Remove sends files to the Recycle Bin and only touches
paths inside the project folder.

Item folders (`ProjectAssets`):

| folder | item | written by |
|---|---|---|
| `textures/` | `<resource name>.png` (top mip) or `.dds` (HDR: RGBA16F, every mip) + `.nrtex.json` sidecar | Textures → Add to project |
| `mesh/<output>/` | scene + `.mesh.json`; a folder name other than the source's clones the mesh under that name | Meshes → Add to project |
| `scene/<name>/` | edited model scene, its report, `split.json` (`assign`, `hide`, `lods`, `double_sided`) | Models → Edit as scene |
| `model/<stem>/` | `.model` + `model.json` (member, pak, paths, noGear, stash) | Models → Add to project |
| `anim/<clip>/` | `.cast` + `.glb` on the shown skeleton, `anim.json` (template, fps, sequence) | Animations → Add to project |
| `prefab/<pack>/` | `prefab.json`: source (pack label, logical index, `Prefabs`) + the edit list | Prefab editing → Save |

**Texture sidecar** (`TextureAsset.cs`): IL format, texture type, mip count, the raw statistics / flags / extension /
tail bytes of the IMGC header, the level padding, the logical flags, and the storage words plus `flagBits`/`fc` of both
parts; unknown bits ride along verbatim. Optional `buildFormat` is the IL format a build should write when it differs
from the original (a PNG can build BC1, BC4, BC7, BC5, BC2, BC3, their sRGB variants, R8, RG8, RGBA8, ARGB8; a DDS or
`.hdr` BC6H_UF16, BC6H_SF16, RGBA16F).

A prefab item's edit list is replayed on the template on every save, so a refused edit never reaches the project.

## Build

`ProjectBuild.Build` (`Core/Project/ProjectBuild.cs`, `ProjectBuilder.cs`) runs on the job queue and builds everything a
project holds — textures, meshes, model scenes, `.model` overrides, clips and prefab edits — in one pass, then verifies
the result. The Build window only ever runs this full build (a texture-only project goes through it too).

**Outputs.**
- **Main rpack**: textures, meshes, scene parts and edited `Prefabs`. Its name defaults to the first free
  `assets_N_pc.rpack` (N ≥ 2). Header field08 follows the predecessor's rule (`ProjectBuilder.PackField08`,
  `RpackWriter.ProjectField08`): `0x1000` when the pack holds a mesh, else the source packs' field08 when they all
  agree, else `0x1000`.
- **Clip rpack**: imported clips, in a pack of their own, field08 by the same rule without a mesh (stock anim packs are
  0, so a stream pair is laid out grouped).
- **Pak**: `.model` overrides, one entry per slot, plus for player models the empty `player_outfit_slots.scr` (no gear).
  Its name defaults to the first free `dataN.pak` (N 0..8, never a stock one).
- A stock pack name, `data0`/`data1`, or anything that is not a bare file name is refused. The names used are stored in
  the manifest and prefilled next time.
- **With runtime modding on**, the verified outputs are also laid out as a NightrunnerProxy mod folder in the build
  folder (`<build>\<id>\packs\`, `paks\`, `mod.json`) for the user to copy into the runtime's mods folder; the clip pack
  is placed `before:common_anims_pc`, everything else `after-builtins`. A clip override through that load point is
  recorded as unverified in game (`mod.animsAt.verified: false`). Details: [runtime-integration.md](runtime-integration.md).
  With it off, the report names the stock assets and source folders as destinations. Nightrunner never copies anything
  into the game folder itself.

**Per item kind.**
- **Textures**: read the source (`TextureSource.cs`; which source builds which format is in
  [formats.md](formats.md#texture-sources-dds-png-radiance-hdr)); a PNG gets its mip chain regenerated by a box filter.
  Re-encode every level to the effective format (the sidecar's `buildFormat`, else the original); rebuild the IMGC
  header from the sidecar with only the geometry and mip count changed.
- **Meshes** (`Core/Mesh/MeshBuild.cs`, DLTB only): original parts + scene (`.cast`/`.glb`/`.gltf`) + sidecar → new
  `0x10`/`0x11`/`0xF0`/`0xF1` (and `0x12` when a new material shifts skin indices, `0xF3` when a cloth entry changed);
  everything else is carried verbatim. In place when every mesh keeps `bp_vertex_id`, rebuilt otherwise; glTF edits are
  first snapped onto the original. Invariants, each tested: an unedited build is byte-identical (all 21,408 stock DLTB
  meshes); the embedded `.msh` name follows the output name; new geometry may only use bones the mesh already has; a
  cloth mesh carries its cloth through kept, reordered, deleted and duplicated vertices and refuses a cloth vertex with
  no source; a new material grows the class-11 table and every skin resolves as before.
- **Model scenes** (`Core/Cast/ModelSplit.cs`): an edited single-model scene and its `.cast.json` → one folder per source
  mesh (`model.cast`, `mesh.json`, raw parts) plus `split_report.json`, undone with the rest rule the file was baked with
  (`/1` first supplier, `/2` part rigs; recorded as `rest_rule`), then built as meshes.
- **`.model` overrides** (`ModelEdit`): slot on/off, the mesh each slot draws, per-material base and `rttiValues`; the
  chosen entry is moved first, because the game draws the first entry.
- **Clips** (`AnimImport`, DLTB only): `.cast` (the export's curves) or `.glb`/`.gltf` (rotation / translation / scale
  channels on named nodes; LINEAR and STEP read, CUBICSPLINE refused) back to a clip. Keys are resampled to whole frames
  at the item's fps (nlerp with sign fix, lerp, held past the last key); tracks bind by `h41` of the node name, or a
  `0x%08X` name; a track without translation or scale takes that channel from the template clip's frame 0; template
  tracks the scene lacks are dropped and listed. Encoded with the shipped recipe into the **template's form** (the stream
  copy `0x44` + `0x45` when the clip ships one, else plain `0x40`) under the same name, then self-checked. Round trip of
  40 random `anims_player` clips through `.cast` and `.glb`: worst 0.056° rotation, 0.0008 mm translation.
- **Prefab edits** (DLTB binary only): the edit list is applied to the open game's resource, validated, re-parsed
  byte-exactly, each edit's value checked, and the **whole** edited `Prefabs` resource (the template's storage words and
  flags) written into the main rpack under the same name. One pack's `Prefabs` per build (the resources share the name).
  The build report says it has never been loaded in game.

**Verify.** Every output is reopened with the project's own readers: every mesh decodes, every texture opens, every
model parses with one entry per slot, clips re-decode, prefabs validate and decode. Every output is written as
`<name>.unverified` and replaces `<name>` only once the verify passes; a failed verify keeps it as `<name>.invalid` and
leaves an earlier `<name>` untouched (the predecessor's rule). **"Verified" means "parses and decodes
here"**, not "works in the game": no build output has been validated with the current runtime.

**Report** (`<project>.build.json`): items, outputs, destinations, warnings, verdict, and `hashes` — per output kind
(`rpack`, `anims`, `pak`) and resource name (or PAK member, lower case with `/`), the first 8 bytes of SHA-256 over the
resource's type, flags and each part's type, length and bytes — plus a `diff` (`added` / `changed` / `unchanged` /
`removed`, or `unknown` when the previous report predates hashes) against the previous report in the same folder.

**Check** (`ProjectBuild.Check`) is a dry run: the same `Build` on a second `ModProject` instance whose in-memory
manifest points the build folder at a new `%TEMP%\nightrunner-check-<guid>`, deleted afterwards; the project's manifest
and build folder are not touched. A refusal is the check's verdict, attributed to the item it names; the diff is against
the last real build.

**Build window** (`BuildView`, `BuildOverview.cs`): one row per item of every kind, read-only, from
`ProjectBuild.Overview`: output name and form, target output, template, source file and whether it changed since the
export, pending edits, refusals visible up front (template not in the open game, DL2 geometry, a duplicate texture
source, a texture source that cannot build), and the last build's and check's results attributed per item.
