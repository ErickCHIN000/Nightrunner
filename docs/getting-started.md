# Getting started

## Requirements

- Windows 10 or 11, x64. The app maps the game's packs into memory and is 64-bit only.
- A Direct3D 11 capable GPU (the 3D viewport).
- An installed copy of **Dying Light: The Beast** and/or **Dying Light 2**. Nightrunner reads the game's files in place;
  it never modifies them.
- To build from source: the **.NET 10 SDK** (developed with 10.0.401). A self-contained publish runs without a separate
  .NET install; a framework-dependent build needs the .NET 10 Desktop Runtime.
- Optional, for audio decoding only: see [audio.md](audio.md).

## Build and run

```
git clone <repository>
cd Nightrunner
dotnet build Nightrunner.slnx
dotnet run --project Nightrunner.UI
```

A self-contained, single-file build (what a release would contain):

```
dotnet publish Nightrunner.UI -p:PublishProfile=FolderProfile
```

The output lands in `Nightrunner.UI/bin/Release/net10.0-windows/publish/win-x64/`, with `LICENSE`,
`THIRD_PARTY_NOTICES.md` and `licenses/` next to the executable.

## Finding the games

On start, pick a game. Nightrunner finds installs in this order: a root you set before (Settings → the game's row),
the `NIGHTRUNNER_GAME_ROOT` environment variable, then your Steam libraries. The root is the folder that contains the
game's data folder (`ph_ft` for The Beast, `ph` for Dying Light 2). Settings are kept in
`%APPDATA%\Nightrunner\settings.json`; crash reports go to `%APPDATA%\Nightrunner\`.

## The windows

Everything is a dockable window (Windows menu); Views saves and restores arrangements.

| Window | For |
|---|---|
| Raw | every resource in every pack; export raw parts or a whole pack |
| Textures | texture list with preview; export PNG / DDS; add to a project |
| Materials | the shader database's materials; export `.mat` |
| Meshes, Models, Prefabs, Animations | browse; show in the Viewport; export; add to a project |
| Audio | Wwise sounds: search, play, export ([audio.md](audio.md)) |
| Viewport | meshes, models with skins, animation playback, facial poses, prefab placement |
| Projects | the items of the open project and their source files |
| Build | check and build the project; what each item produced |
| Mods | NightrunnerRuntime's installed mods, what loads, and the on/off switch (The Beast only) |
| Inspector | details of whatever is selected |
| Log | everything the app did, with timings |

## Exporting

Select something and use its context menu (Export…, Export .cast…, Export .glb…, Export as PNG…, Export .wav…). Meshes
and models export as Cast or glTF for Blender; Cast needs a Cast importer add-on in Blender, glTF works with Blender's
built-in importer.

## Making a mod (Dying Light: The Beast)

1. Create a project (Projects → New). A project is a folder with the source files of your edits.
2. Add what you want to change: a texture, a mesh, a model ("Edit as scene" for a whole character), an animation clip, or
   prefab edits (context menu "Add to project"). Nightrunner exports the current game version into the project for you
   to edit (PNG/DDS/HDR for textures, Cast/glTF for meshes, models and clips).
3. Edit the files (for example in Blender), then Build → Check to see what will be built, and Build.
4. The build folder gets new `.rpack`/`.pak` files. With runtime modding on (Settings), it also gets a mod folder for
   NightrunnerRuntime; copy it to the game's `bin\x64\Nightrunner\mods\`. See
   [runtime-integration.md](runtime-integration.md).

On Dying Light 2, projects build textures only. In-game loading of built content through the current NightrunnerRuntime
has not been validated yet ([runtime-validation-checklist.md](runtime-validation-checklist.md)).
