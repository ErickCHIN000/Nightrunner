# Contributing to Nightrunner

Issues and pull requests are welcome. Contributions are accepted under the project's [MIT License](LICENSE).

## Rules

These hold for every change:

- **Offline file work only.** No process injection, memory patching or runtime hooks; in-game loading belongs to
  NightrunnerRuntime.
- **Game installs are read-only.** Nothing writes into a game folder, with one exception: the Mods window's on/off switch
  edits NightrunnerRuntime's `bin\x64\Nightrunner\nightrunner.json` (a mod's `enabled`, or an entry for a mod not listed
  yet; through `nightrunner.json.tmp`, keeping `nightrunner.json.bak`; never while the game runs). No mod files are copied
  into the game, nothing there is deleted, and no other file there is written.
- **No proprietary game data in the repository.** Tests and tools read the installed games at run time; where a test pins
  output made from game data, it stores a SHA-256, not the data.
- **Unknown bytes are preserved verbatim**, never reinterpreted or relabelled.
- **Refuse rather than approximate.** An unsupported case throws or refuses by name; nothing writes a file it could not
  verify.
- **No absolute paths** in source, docs or tests. Installs are found by `GameInstall` (saved root →
  `NIGHTRUNNER_GAME_ROOT` → Steam).
- **UI text is terse:** single words, no legends or info banners.
- **Docs follow behaviour.** When a format decision or behaviour changes, update [docs/](docs/) with measured numbers
  (label estimates as estimates). Formats and the record of where Nightrunner deliberately differs from its archived
  Python predecessor are in [docs/formats.md](docs/formats.md) and [docs/porting.md](docs/porting.md).
- **Runtime integration** follows NightrunnerRuntime's contract ([docs/runtime-integration.md](docs/runtime-integration.md)):
  Nightrunner reads mod files the way the runtime does and never claims more than the runtime loads.

## Layout

```
Nightrunner.Core/     formats and services: Rpack, Texture, SDB, Mesh, Model, Cast, Prefab, Anim, Audio, Games,
                      Backends, Project, Export, Logging
Nightrunner.UI/       WPF (.NET 10) with AvalonDock; Panels.cs is the window registry; Helix only in Viewport/
Nightrunner.Tests/    xUnit v3; OptIn/ holds historical parity tests, compiled only with -p:OptInTests=true
tools/                census tools (RpackCheck, SdbCheck, MeshCheck, PrefabCheck, AnimCheck, AnimBankCheck),
                      cross-checks against the Python predecessor (ExportCheck, BuildCheck, parity/),
                      and the optional audio decoder build (vgmstream/)
third_party/vgmstream/ what the optional audio decoder is and how to build it (the DLLs are never committed)
docs/                 user and contributor documentation
licenses/             third-party licence texts (see THIRD_PARTY_NOTICES.md)
```

## Build and test

```
dotnet build Nightrunner.slnx
dotnet test --project Nightrunner.Tests
dotnet run --project Nightrunner.UI
```

A clean build has no warnings; keep it that way (fix analyzer warnings rather than suppressing them). Tests that need an
installed game or the optional audio decoder skip, with the reason, when they are missing. See
[docs/testing.md](docs/testing.md) for the test tiers, the census tools and the UI self-checks.

## Commits

Describe what changed and why in plain language. Keep commit messages and files free of personal information, local
paths and links to private tools or sessions.
