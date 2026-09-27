# Release validation (0.1.0-rc.1)

What was measured for this release candidate, how, and what was not covered. Game data was only read; nothing here
was run through NightrunnerRuntime (see [runtime-validation-checklist.md](runtime-validation-checklist.md)).

Environment: Windows 11 x64, .NET SDK 10.0.401 (runtime 10.0.12), Dying Light: The Beast and Dying Light 2 installed
from Steam (builds current on 2026-09-27), optional audio decoder built by `tools/vgmstream/build-vgmstream.ps1` at
vgmstream `95cff213` with MSVC 19.51.

## Build and tests

From a fresh export of the release branch (no build output, no local files), Release configuration:

| Run | Build | Tests |
|---|---|---|
| installs present, no audio decoder | 0 warnings, 0 errors | 668 total, 661 passed, 0 failed, 7 skipped (all "audio decoder not available") |
| installs present, decoder built with the script | 0 warnings, 0 errors | 668 total, 668 passed, 0 failed, 0 skipped |
| `NIGHTRUNNER_TESTS_NO_INSTALL=1` | — | 668 total, 443 passed, 0 failed, 225 skipped (install-backed and decoder-backed) |

The historical parity tests against the archived Python predecessor are opt-in (not compiled by default); all of
them passed when last run with their fixtures ([tools/parity/README.md](../tools/parity/README.md)).

A self-contained single-file publish (`-p:PublishProfile=FolderProfile`) contains the executable, the WPF native
libraries, `LICENSE`, `THIRD_PARTY_NOTICES.md` and `licenses/`, no debug symbols, and no audio decoder even when one is
built locally. File version 0.1.0.0, product version 0.1.0-rc.1.

## Audio

Measured with a separate harness that drives Nightrunner's audio code and runs every risky decode in its own process, so
a native crash would show as an exit code. The harness is not part of the repository; the in-app part is the
`NIGHTRUNNER_AUDIOCHECK` self-check.

| Area | Result |
|---|---|
| Enumeration | DLTB: 7 archives, 51,894 WEMs (46,099 loose, 5,795 in banks). DL2: 7 archives, 87,295 (82,913 + 4,382). 0 archive errors, 0 bank warnings |
| Event names | DLTB 51,882 of 51,894 named (23,904 events, 128 banks); DL2 87,293 of 87,295 (51,590 events, 115 banks). Coverage, not a proof of correctness |
| Metadata | all 139,189 WEMs opened by the decoder; channels, sample rate and sample count equal to an independent parse of each WEM's header (0 mismatches) |
| Full decode | 3,339 WEMs (every multichannel, PCM, looped and largest file, 39 at archive offsets past 4 GiB, plus a random sample): decoded length = declared length for all; deterministic across buffer sizes; 3,323 of 3,323 seek positions sample-exact |
| Decoder build | the decoder built from pinned source produces PCM identical (SHA-256) to the previously contributed binary on all 3,339 |
| Signal sanity | 5 all-silent WEMs are genuinely silent encodes (about 1.1-1.6 kbps against a 345 kbps stereo median); spectral flatness of 162 exports at most 0.14 (noise is 0.5-1) |
| WAV export | 162 of 162 pass an independent RIFF parser and `ffprobe`: sizes, format, channel mask above 2 channels, data length, PCM hash equal to the decoder's, `smpl` loop with inclusive end, `LIST/INFO` |
| Raw export | `.wem` exports byte-identical to the archive bytes (162 of 162) |
| Metadata edge cases | Unicode and emoji, empty, embedded NULs, a 70,000-character title: valid files |
| Output paths | missing folder, invalid characters, a >260-character path, `.aesp` or the source archive as target, a folder as target, an existing file without overwrite, read-only and locked targets, relative path: each refused or written correctly; existing and locked files untouched; no temporary file left |
| Cancellation | mid-copy and mid-decode of a 55 MB WEM: cancelled, no destination file, no temporary file |
| Corrupt input | 393 corrupted WEMs, one process each: 0 crashes, 0 hangs; 16 archive-level corruptions refused by name; 600 corrupted soundbanks and 80 corrupted bank hierarchies: 0 unhandled exceptions; hostile registry XML (DTD expansion, external entity, 40 MB, invalid UTF-8, deep nesting): refused or ignored, no file read |
| Missing decoder | libvgmstream, libvorbis or both absent: browsing, names and `.wem` export work; decode and `.wav` export unavailable with a named reason; no crash. In the app: one log line in the whole session, "no decoder" in the player and status bar, "decoder none" in the inspector |
| Resource lifetime | 10,000 decoder open/dispose: handles flat. 500 undisposed decoders: all collected, handles back to where they were (before the fix: all 500 alive, +500 handles). Playback sessions: 13 threads at the start, 15 / 12 / 12 after 150 / 300 / 450 sessions and 30 s idle (before the fix: 15 → 41 → 58 → 85) |
| Read failures | an archive read failing mid-decode fails the export with an error and stops playback with an error (before: a full-length silent WAV, reported as success) |
| Concurrency | 64 WEMs decoded ×3 on 16 threads identical to serial; 32 parallel exports valid |
| Playback | play, pause, resume, seek while playing and while paused, stop, end of file and replay, 6-channel downmix, 200 rapid switches, process exit while playing: all pass |
| In the app | both games: list, inspector, search, play/pause/seek/stop, selection changes and play switches while playing, closing the window, switching game and closing the app while playing |

**Not covered:** listening by a person (a 17-file listening set was prepared), other audio devices (none, unplugged, exclusive
mode), a machine without the Visual C++ runtime, correctness of event names beyond spot checks. Damaged Wwise Vorbis data
decodes to noise without an error; a wrongly declared length or channel count in a WEM's header is followed as declared.
