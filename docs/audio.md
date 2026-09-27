# Audio

The **Audio** window browses the Wwise audio of Dying Light: The Beast and Dying Light 2. Contributed by
**metalheadbangg**; integrated and extended in this repository.

## What it does

| | Needs the optional decoder |
|---|---|
| List every AESP archive and the WEM sounds inside it (loose WEMs, and media embedded in soundbanks) | no |
| Resolve Wwise event names for sounds (from the game's `wwisepinhead` registry and bank hierarchy) and search by name, ID, bank or archive | no |
| Export the original `.wem` bytes, unchanged | no |
| Show codec, channels, sample rate and duration | yes |
| Play (with pause, seek, stop; multichannel sounds are downmixed to stereo for the player) | yes |
| Export `.wav` (16-bit PCM, source channel count, loop points in a `smpl` chunk, name/event/bank/archive in `LIST/INFO`) | yes |

## What it does not do

- No audio import, replacement or rebuilding: nothing writes an AESP archive or a soundbank.
- No runtime audio: NightrunnerRuntime accepts `audio` mod items but skips them, so there is no audio mod output.
- Media that the game streams from elsewhere (prefetch fragments, streamed bank media) are not listed; neither are
  archive rows of other kinds (plug-in and MIDI rows). Soundbanks of a version other than 150 are refused with a warning.
- Codecs: the games' WEMs are Wwise Vorbis (1, 2, 4 and 6 channels) and a few PCM files; the decoder built by the
  script supports those. Other Wwise codecs are refused by the decoder.

## The optional decoder

Decoding uses [vgmstream](https://github.com/vgmstream/vgmstream) (`libvgmstream.dll` with `libvorbis.dll`), which is
**not in this repository and not part of any Nightrunner release** — its build includes code whose terms do not allow
redistributing it with the app. To enable decoding, build it once:

```powershell
powershell -ExecutionPolicy Bypass -File tools\vgmstream\build-vgmstream.ps1
```

This needs git and Visual Studio 2022 or later with the C++ workload, and puts the DLLs, their licence texts and a
`PROVENANCE.txt` into `third_party\vgmstream\win-x64\`. A build from source copies them next to the app. For a published
copy of Nightrunner, copy `libvgmstream.dll` and `libvorbis.dll` next to `Nightrunner.UI.exe`. Running them needs the
**Microsoft Visual C++ v14 x64 Redistributable** (at least as new as the compiler that built the DLL).

Nightrunner checks for the decoder once at startup and logs the result. Without it the Audio window says "no decoder"
(the reason is on hover), Export .wav is disabled, and browsing and `.wem` export work as usual. Details:
[third_party/vgmstream/README.md](../third_party/vgmstream/README.md).

## How far it has been tested

Measured on the installed games (DLTB and DL2); details in [release-validation.md](release-validation.md):

- all 139,189 WEMs listed (51,894 DLTB, 87,295 DL2) and opened by the decoder, with channels, sample rate and sample
  count matching each WEM's own header;
- 3,339 WEMs decoded in full (every multichannel, PCM, looped and largest file, plus a random sample): decoded length
  equal to the declared length, deterministic, and 3,323 seek positions sample-exact;
- 162 WAV exports checked by an independent parser and by `ffprobe`; raw `.wem` exports byte-identical to the archive;
- corrupt, truncated and hostile input (393 corrupted WEMs, 600 corrupted banks, hostile XML) without a crash or hang;
- playback, pause, seek, stop, rapid switching and shutdown in the running app.

Limits of that evidence: a decoder that does not fail is not proof that every waveform is right. Wwise Vorbis has no
checksums, so damaged audio data decodes to noise without an error. Event names are matched by hashing and walking the
bank hierarchy; beyond a few spot checks their correctness has not been confirmed by ear or in game. A human listening
pass is part of the release checklist.
