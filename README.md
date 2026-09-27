# Nightrunner

An offline asset toolkit for Techland's Chrome Engine games **Dying Light: The Beast** and **Dying Light 2**: browse the
game's packs, view meshes, models and animations in 3D, export assets for Blender and other tools, and rebuild edited
assets into new game packs. Windows, .NET 10, WPF.

> **Status: first public release candidate.** Reading and exporting are the mature part. Rebuilding is implemented and
> tested inside the tool, but loading rebuilt content in game through the companion runtime has **not yet been validated
> with the current runtime**. Audio decoding needs an optional decoder you build yourself. Details: [docs/status.md](docs/status.md).

## What it does

- Reads every resource pack (`.rpack`) and data pak (`.pak`) of an installed game, in place; never modifies the game.
- Textures, materials, meshes (with skins and cloth), models, prefabs, animation clips, and Wwise audio: browse, search,
  inspect, and view in a 3D viewport with animation playback.
- Exports PNG, DDS, Cast, glTF (`.glb`), `.mat`, `.model`, `.wav`, `.wem` and raw resource parts.
- Rebuilds edited textures, meshes, model scenes, `.model` documents, prefab edits and animation clips into new packs and
  paks (Dying Light: The Beast; Dying Light 2: textures only), and writes a mod folder for NightrunnerRuntime.

## Supported games

| | Read, view, export | Rebuild | Audio (browse, export `.wem`; decode/play/`.wav` with the optional decoder) | NightrunnerRuntime module |
|---|---|---|---|---|
| Dying Light: The Beast | yes | yes (prefab edits and clip import experimental) | yes | yes (in-game validation pending) |
| Dying Light 2 | yes | textures only | yes | no |
| Dying Light 1 | no (placeholder only) | no | no | no |

Per-asset details and limits: [docs/asset-support.md](docs/asset-support.md).

## Requirements

- Windows 10 or 11, x64; a Direct3D 11 capable GPU.
- An installed copy of Dying Light: The Beast and/or Dying Light 2 (found through Steam, or set the folder in Settings).
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Optional, for audio decoding: `libvgmstream.dll` built with [tools/vgmstream](tools/vgmstream/build-vgmstream.ps1),
  and the Microsoft Visual C++ v14 x64 Redistributable ([docs/audio.md](docs/audio.md)).

## Build and run

```
dotnet build Nightrunner.slnx
dotnet run --project Nightrunner.UI
```

Self-contained single-file publish: `dotnet publish Nightrunner.UI -p:PublishProfile=FolderProfile`.
Tests: `dotnet test --project Nightrunner.Tests` (tests that need an installed game or the audio decoder skip without
them). See [docs/getting-started.md](docs/getting-started.md) and [docs/testing.md](docs/testing.md).

## Using it

Pick a game, then open windows from **Windows** (Raw, Textures, Materials, Meshes, Models, Prefabs, Animations, Audio,
Viewport, Projects, Build, Mods, Inspector, Log). Export from any list's context menu. To make a mod, create a project,
**Add to project** what you want to change, edit the exported files (for example in Blender), then **Check** and
**Build**. Walkthrough: [docs/getting-started.md](docs/getting-started.md).

## Audio

The Audio window lists the Wwise sounds of both games, names them from the game's event registry, and exports the
original `.wem` files. With the optional decoder it also plays them and exports `.wav`. There is no audio import or
replacement, and NightrunnerRuntime does not load audio. The decoder (vgmstream) is not shipped with Nightrunner because
its build includes code that may not be redistributed with the app; build it with the script and it is picked up
automatically. See [docs/audio.md](docs/audio.md).

## NightrunnerRuntime

Nightrunner works offline and writes files; getting them into the running game is the job of **NightrunnerRuntime**, a
separate project (a small native `dxgi.dll` bootstrap that hosts .NET 10 inside the game). Nightrunner checks whether the
runtime is installed, reads its mods and settings the way the runtime does, lets you switch mods on and off, and builds
mod folders for it. Only Dying Light: The Beast has a runtime module, and end-to-end loading with the current runtime is
still **pending validation**. See [docs/runtime-integration.md](docs/runtime-integration.md).

## Documentation

- [Getting started](docs/getting-started.md)
- [Asset support](docs/asset-support.md) and [file formats](docs/formats.md)
- [Audio](docs/audio.md)
- [Runtime integration](docs/runtime-integration.md) and the [in-game validation checklist](docs/runtime-validation-checklist.md)
- [Status](docs/status.md)
- [Testing](docs/testing.md), [release validation](docs/release-validation.md)
- [Architecture](docs/architecture.md)
- [Porting notes and divergences](docs/porting.md)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits

- Nightrunner by **ErickCHIN000**.
- Audio support (Wwise archive browsing, event names, decoding, playback and export) contributed by **metalheadbangg**.
- Third-party components and their licences: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

MIT; see [LICENSE](LICENSE). Nightrunner is not affiliated with or endorsed by Techland. Dying Light is a trademark of
its owner. Nightrunner contains no game data; it works on your own installed copy.

Nightrunner replaces an earlier Python prototype by the same author, which is archived and no longer
maintained.
