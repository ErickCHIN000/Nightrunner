# Testing

```
dotnet build Nightrunner.slnx
dotnet test --project Nightrunner.Tests
```

## Test tiers

| Tier | Needs | Without it |
|---|---|---|
| Synthetic | nothing | always runs |
| Install-backed | Dying Light: The Beast and/or Dying Light 2 installed (found like the app finds them: saved root, `NIGHTRUNNER_GAME_ROOT`, Steam) | skips with "no dltb/dl2 install detected"; `NIGHTRUNNER_TESTS_NO_INSTALL=1` forces that |
| Audio decoding | the optional decoder in `third_party/vgmstream/win-x64/` ([audio.md](audio.md)) | skips with "audio decoder not available: …" |
| Opt-in historical parity | Python, the archived Python predecessor, fixtures; compiled only with `-p:OptInTests=true` | not compiled, not listed ([tools/parity/README.md](../tools/parity/README.md)) |

Tests never write into a game folder; install-backed tests read the games and write only to temporary folders.
No game data is stored in the repository: where a test pins output made from game data, it pins a SHA-256 of that
output. Those hashes depend on the installed game files, so a game update that changes a sampled asset fails the test
with a message giving the new values to check and paste.

## Census tools

Run against the detected installs (read-only), `dotnet run --project tools/<Tool> -c Release`:

| Tool | Checks |
|---|---|
| `RpackCheck` | every pack and texture header; `--bc6h`: BC6H decode / re-encode census |
| `SdbCheck` | the shader database |
| `MeshCheck` | mesh decode and byte-exact re-encode; `--models`: `.model` census; `--cloth`: cloth parts |
| `PrefabCheck` | prefab container round trip, validation, decode coverage (DLTB and DL2), edits; `--text`: text prefabs; `--place`: placement |
| `AnimCheck` | ANM2 decode, bit-exact re-encode, skeleton check; `--rigs`: the viewer's rig pick |
| `AnimBankCheck` | sequence / graph / custom-resource bank round trip (DLTB) |
| `ExportCheck`, `BuildCheck` | compare mesh export and mesh building with the archived Python predecessor (needs Python and that checkout; see tools/parity) |

## UI self-checks

The app can drive itself through scripted scenarios and write captures and a `report.txt` into a folder. They never
write the user's settings (`%APPDATA%\Nightrunner\settings.json`).

| Variable | Scenario |
|---|---|
| `NIGHTRUNNER_DOCKCHECK=<folder>` | viewport and docking scenarios on both games (`_ANIM=1`: animation steps only; `_MODS`: runtime-mods steps) |
| `NIGHTRUNNER_AUDIOCHECK=<folder>` | the Audio window on each game at volume 0: inspector, search, play/pause/seek (also while paused)/stop, selection changes and play switches while playing, closing the window, switching game and closing the app while playing |
| `NIGHTRUNNER_RUNTIMECHECK=<folder>` | Settings and the Mods window per game (`_FAKE=<root>`: also a scratch DLTB-shaped root; `_SWITCH=<mod id>`: clicks that mod's switch twice, which writes `nightrunner.json` and then restores the same bytes) |
| `NIGHTRUNNER_BUILDCHECK=<folder>` | makes (or with `_PROJECT` reopens) a DLTB project with every item kind and runs Check and Build (`_EDIT=1`: a texture edit and rebuild; `_RUNTIME=1`/`0`: runtime modding for the run) |
| `NIGHTRUNNER_PROJECTSCHECK=<folder>` | the Projects window per tab (`_PROJECT`: an existing project, read only; `_FORGET`) |
| `NIGHTRUNNER_MEMCHECK=<folder>` | memory soak, writes `mem.csv` (`_GAME`, `_TEXTURES`, `_MODELS`, `_ANIMS`, `_PREFABS`, `_PREFAB_KIND`, `_PREFAB_NAMES`; see `MainWindow.MemCheck.cs`) |
| `NIGHTRUNNER_TEXCHECK=<folder>` | viewport captures of named models/meshes (`_ITEMS=dltb=a.model,mesh:b;dl2=c.model`) |

Other variables: `NIGHTRUNNER_GAME_ROOT` (a game root when detection fails), and the opt-in parity variables
(`NIGHTRUNNER_PROTOTYPE`, `NIGHTRUNNER_PYTHON`, `NIGHTRUNNER_GLTF_FIXTURES`, `NIGHTRUNNER_SPLIT_SMOKE`).
