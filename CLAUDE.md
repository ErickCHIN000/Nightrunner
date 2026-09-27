# Nightrunner — notes for coding agents

Follow [CONTRIBUTING.md](CONTRIBUTING.md): its rules apply to every change, whoever or whatever makes it. The short
version:

- Offline file work only: no process injection, memory patching or runtime hooks.
- Game installs are read-only. The single exception is the Mods on/off switch editing NightrunnerRuntime's
  `bin\x64\Nightrunner\nightrunner.json` (never while the game runs).
- Unknown bytes are preserved verbatim, never reinterpreted.
- Refuse rather than approximate: an unsupported case throws or refuses by name.
- No absolute paths in source, docs or tests; installs are found by `GameInstall`.
- No proprietary game data in the repository (tests read the installed games at run time).
- UI text is terse: single words, no legends or info banners.
- Keep docs/ current when behaviour changes, with measured numbers (label estimates as estimates).

Build and test: `dotnet build Nightrunner.slnx`, `dotnet test --project Nightrunner.Tests`. Layout, test tiers and
tools: CONTRIBUTING.md and docs/testing.md.
