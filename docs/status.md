# Status

Nightrunner is at its **first public release candidate**. "Supported" below means implemented, covered by automated
tests (synthetic and against the installed games), and checked by the census tools; it does not mean anything built has
been confirmed in game through the current NightrunnerRuntime — that is **pending** for every build output.

## Supported

- Reading every resource pack, texture, material, mesh, model, prefab (binary and text) and animation clip of
  Dying Light: The Beast and Dying Light 2; the 3D viewport with skins, animation and facial poses.
- Export: PNG, DDS, Cast, glTF (`.glb`), `.mesh.json`, `.skn`, `.mat`, `.model`, raw parts.
- Rebuilding on The Beast: textures, meshes, model scenes and `.model` edits into new packs/paks, checked by round-trip
  tests and, where the archived Python predecessor had the same feature, against its output (byte-for-byte for pack
  writing and mesh builds; documented differences in [porting.md](porting.md)). Dying Light 2: textures.
- Audio browsing, event names and raw `.wem` export on both games.
- NightrunnerRuntime (rc1) contract on the Nightrunner side: install check, `nightrunner.json` / `mod.json` reading, the
  mod switch, and mod-folder build output (The Beast).

## Experimental

- Audio decoding, playback and WAV export — needs the optional decoder that each user builds
  ([audio.md](audio.md)); event names not yet verified by ear or in game.
- Prefab edits and animation-clip import (The Beast).
- Cloth vertex remapping in mesh rebuilds.
- Everything that depends on in-game behaviour: see [runtime-validation-checklist.md](runtime-validation-checklist.md).

## Planned

Nothing is scheduled. Work that would come next is listed as "not currently supported" below; none of it is promised.

## Not currently supported

- Audio import, replacement or rebuilding; loading audio through NightrunnerRuntime (the runtime skips `audio` items).
- Rebuilding anything but textures on Dying Light 2; a NightrunnerRuntime module for Dying Light 2.
- Dying Light 1 (a placeholder exists in the code; nothing works for it).
- Writing the shader database (new materials); editing animation banks; editing text prefabs.
- Compressed pack storages and `.rpacz` child packs; cube and volume textures; FBX, OBJ, TGA, EXR.
