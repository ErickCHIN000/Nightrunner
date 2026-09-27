# End-to-end validation checklist (Nightrunner → NightrunnerRuntime → game)

Status: **not started.** Nothing below has been run with the current Nightrunner and the current runtime. Results from
the earlier, removed runtime do not count. Fill each row from an actual run; leave it blank rather than guess.

**Setup to record once:** Nightrunner commit; NightrunnerRuntime commit; game (Dying Light: The Beast) build / Steam
build ID; Windows version; .NET runtime version; whether the audio decoder was installed (not needed here).

**Per run:** keep the project folder, the built mod folder, `nightrunner.json`, `logs\boot.log` and
`logs\nightrunner.log`.

## Runtime recognition (no game start needed)

| # | Scenario | Nightrunner operation | Expected in Nightrunner | Observed | PASS/FAIL |
|---|---|---|---|---|---|
| R1 | Runtime installed from its rc build | Settings → DLTB → Validate | every required file `ok` (dxgi, folder, core, config, api, module) | | |
| R2 | `Nightrunner.Api.dll` removed (on a copy) | Validate | `api missing`, runtime not installed | | |
| R3 | No `nightrunner.json` | Mods window | all mods on, "defaults" | | |
| R4 | `nightrunner.json` with `"mods": [null]` | Mods window | "runtime loads no mods (…)", switch refuses | | |
| R5 | A mod listed twice with different `enabled` | Mods window | first entry wins, warning shown | | |
| R6 | Switch a mod off and on | Mods window "on" box twice | file edited, `.bak` kept, same bytes after two clicks | | |
| R7 | Game running | Mods window switch | refused | | |

## Assets (one mod each, The Beast)

For each: build the project with runtime modding on, copy `<build>\<modId>\` to `…\bin\x64\Nightrunner\mods\`, start the
game, and check.

| # | Asset | Nightrunner operation | Resulting mod layout | Runtime recognition (Mods window: valid / loads) | Runtime load (`nightrunner.log` lines) | In-game result | Logs / errors | PASS/FAIL |
|---|---|---|---|---|---|---|---|---|
| A1 | Texture replacement (a visible albedo) | Textures → Add texture to project; replace the PNG; Build | `mod.json`, `packs\assets_N_pc.rpack` at `after-builtins` | | | | | |
| A2 | HDR / BC6H texture | as A1 with a `.hdr` source | as A1 | | | | | |
| A3 | Mesh edit (moved vertices on a prop) | Meshes → Add to project; edit the Cast/glTF in Blender; Build | `packs\assets_N_pc.rpack` | | | | | |
| A4 | Model scene edit (a character) | Models → Edit as scene; edit; Build | `packs\assets_N_pc.rpack` (split meshes) | | | | | |
| A5 | `.model` edit (swap a slot's mesh or material) | Models → Add to project; edit in the inspector; Build | `paks\dataN.pak` at `after-builtins` | | | | | |
| A6 | Prefab edit (move or remove an entity) | Prefabs → edit; Add to project; Build | `packs\assets_N_pc.rpack` with the edited `Prefabs` resource | | | | | |
| A7 | Animation clip import (replace a clip) | Animations → Add to project; replace the Cast/glTF; Build | `packs\<name>_anims_pc.rpack` at `before:common_anims_pc` | | | | | |
| A8 | A7 built as a plain (non-stream) clip | Build → plain clips | as A7 | | | | | |
| A9 | Two mods overriding the same texture, different `order` | two projects, both built and installed | two mod folders | | | expected: lower `order` wins (rpack first-wins) | | |

## Runtime behaviour Nightrunner relies on (to measure, not assume)

| # | Question | How to observe | Result |
|---|---|---|---|
| M1 | Does `before:common_anims_pc` put a clip pack ahead of the stock animation pack? | A7 in game; runtime log of the load point | |
| M2 | Which stock packs load before the first `LoadResources`? (Nightrunner's comments disagree: only `engine_pc` and `lang_speech_*`, or also the animation packs) | runtime debug log of LoadPack order | |
| M3 | Does a mod pack whose file name equals a stock pack's (or another mod's) load, get refused, or replace? | a deliberately colliding pack | |
| M4 | SDB mod items: merge or replace, and which duplicate material wins? (Nightrunner writes no SDB items) | a hand-made sdb item | |
| M5 | `audio` and `map` items are skipped with a warning and do not block the mod's other items | a hand-made `mod.json` with an audio item and an rpack item | |
