# Runtime integration (NightrunnerRuntime)

Nightrunner is an offline toolkit: it reads the game's files and writes new packs into a project folder. Loading those
packs into the running game is the job of a separate project, **NightrunnerRuntime** (also called NightrunnerProxy in
the code): a small native `dxgi.dll` bootstrap (C++20) that starts a .NET 10 runtime inside the game and mounts mod
content at the engine's load points.

> **Status: PENDING CURRENT-RUNTIME VALIDATION.** Nightrunner's side of the contract below matches NightrunnerRuntime's
> release candidate (rc1). No asset built by the current Nightrunner has yet been confirmed to load in game through the
> current runtime; see [runtime-validation-checklist.md](runtime-validation-checklist.md). Earlier in-game results were
> obtained with an older runtime that has since been removed and do not count as evidence.

Only **Dying Light: The Beast** has a runtime module. For Dying Light 2 (and the Dying Light 1 placeholder) Nightrunner
reports "no runtime module" and keeps runtime modding off.

## What Nightrunner does and does not do

- **Does:** detect whether the runtime is installed and complete; read `nightrunner.json` and every `mods\*\mod.json` the
  way the runtime does, and show what would load (Mods window, Settings, Inspector); merge the loadable mod packs into its
  own views when *runtime modding* is on; write a **mod folder** as a build output; switch a mod on or off in
  `nightrunner.json` (the only file Nightrunner ever writes inside a game folder).
- **Does not:** install the runtime, copy mods into the game, delete anything in a game folder, or write audio/map/SDB
  mod content. Copying a built mod folder into the game is a manual step.

## Install layout

In the game's executable folder (`<game>\ph_ft\work\bin\x64\` for The Beast):

```
dxgi.dll                              the runtime's proxy — the only proxy name; recognised by the UTF-16 string
                                      "Nightrunner\Core.dll" inside it
Nightrunner\Core.dll                  required
Nightrunner\Core.runtimeconfig.json   required
Nightrunner\Nightrunner.Api.dll       required
Nightrunner\DLTB.dll                  required (the game module)
Nightrunner\Core.deps.json            optional
Nightrunner\nightrunner.json          settings (optional; see below)
Nightrunner\mods\<folder>\mod.json    one folder per mod
Nightrunner\logs\boot.log             native bootstrap log (rolls to boot.prev.log past 1 MiB)
Nightrunner\logs\nightrunner.log      Core's log (previous run: nightrunner.prev.log)
```

Settings → the game's row shows each of these as ok / missing / absent. A `dxgi.dll` without the marker is reported as not
Nightrunner's. Files left by the removed first runtime (`winmm.dll`, `content.ini`, `custom_*` folders) are ignored.
The runtime itself also needs an installed x64 .NET 10 runtime.

When the last game start failed before Core started, Settings and the Mods window show `boot failed: <reason>` with the
path of `logs\boot.log`; when Core started but reported a failure they show `core failed: …` and point at
`nightrunner.log`.

## nightrunner.json

```json
{ "schema": "nightrunner/settings@1", "console": false, "logLevel": "info",
  "mods": [ { "id": "my-mod", "enabled": true, "order": 0 } ] }
```

- **Missing file:** defaults — every mod enabled, console off, log level `info`.
- **Broken file** (empty, invalid JSON, a wrong type, a schema other than `nightrunner/settings@1`, an unknown `logLevel`,
  a `null` entry, a blank id): **the runtime loads no mods.** Nightrunner shows "runtime loads no mods (<reason>)", merges
  no mod content, and refuses to edit the file.
- A mod listed twice: the **first entry wins**, with a warning.
- `console` is off unless the file says `true`. Files Nightrunner creates are written with `"console": false`.
- The Mods window's on/off switch edits only a mod's `enabled` (or appends `{id, enabled, order}` for an unlisted mod),
  writes through `nightrunner.json.tmp`, keeps `nightrunner.json.bak`, and refuses while the game is running, for a
  file with comments (the runtime accepts them but an edit would drop them), and for any file the runtime would treat as
  broken.

## mod.json

```json
{ "id": "my-mod", "name": "My mod", "description": "…",
  "items": [ { "kind": "rpack", "file": "packs/assets_0_pc.rpack", "at": "after-builtins", "order": 0 } ] }
```

| Field | Rule |
|---|---|
| `id` | required, trimmed, compared case-insensitively; a later folder with an id already taken is skipped (folders are read in name order) |
| `name`, `version`, `author`, `description` | display only; Nightrunner's builder writes `id`, `name`, `description` |
| `items` | a list (or absent/`null` = nothing to load); anything else makes the mod invalid |
| `items[].kind` | `rpack`, `pak`, `sdb`, `audio`, `map` |
| `items[].file` | relative to the mod folder; no rooted path, no `:`, no escaping the folder; must exist |
| `items[].at` | `after-builtins` (default) or `before:<pack>`; the pack name is normalised like the runtime (folder and `.rpack` dropped, lower case, trimmed; empty is invalid) |
| `items[].order` | integer, default 0 |

Any bad item makes the whole mod invalid; other mods are unaffected.

**Kinds.** `rpack` and `pak` load. `sdb` loads once, after the renderer's first `MmCreate`, whatever `at` says (a
`before:` on an sdb item is reported as a warning). `audio` and `map` are **accepted but skipped** by the runtime, and
Nightrunner shows them as not loaded. Nightrunner's material viewer always shows the stock SDB, never mod SDBs.

## Load order

- Enabled mods: listed mods first, by (`order`, `id`); then unlisted mods, by `id`.
- Items within a mod: by (`order`, `file`).
- `after-builtins` items load at the engine's first `LoadResources` (paks, then rpacks); `before:<pack>` items load just
  before that stock pack.
- Precedence: an rpack loaded earlier wins a resource-name collision; a pak mounted later wins. Keep pack file names
  unique across stock packs and all enabled mods: what the engine does with a duplicate pack name is untested.

## What a Nightrunner build writes

A project build writes into the project's build folder, never into the game:

- `assets_N_pc.rpack` — textures, rebuilt meshes, split scenes and the edited `Prefabs` resource;
- `<name>_anims_pc.rpack` — rebuilt animation clips;
- `dataN.pak` — `.model` documents and the outfit script;
- `<project>.build.json` — what was built, with hashes and warnings.

With runtime modding on (The Beast only), the build also writes a mod folder `<build>\<modId>\` with `mod.json`,
`packs\` and `paks\`: the clip pack at `before:common_anims_pc`, the main pack and pak at `after-builtins`. To use it,
copy that folder to `…\bin\x64\Nightrunner\mods\`.

Open points that need in-game measurement are listed in the validation checklist (clip-pack timing, SDB merge semantics,
pack-name collisions).
