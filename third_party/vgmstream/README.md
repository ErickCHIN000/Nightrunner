# Optional audio decoder (vgmstream)

Nightrunner decodes Wwise audio (play, WAV export, codec details) through [vgmstream](https://github.com/vgmstream/vgmstream)'s
public library API. The decoder is **optional** and **not in this repository**. Without it, the Audio window still lists
archives and sounds, resolves event names, searches, and exports the original `.wem` bytes; play and WAV export are
unavailable, and the window says "no decoder".

## What it needs

Beside `Nightrunner.UI.exe` (or in `third_party/vgmstream/win-x64/` for a build from source, which copies them to the
output):

| File | What it is |
|---|---|
| `libvgmstream.dll` | vgmstream built as a shared library, x64, libvgmstream API 1.1.0 (`0x01010000`), Vorbis enabled |
| `libvorbis.dll` | upstream vgmstream's own prebuilt `ext_libs/dll-x64/libvorbis.dll` (MinGW-w64 build of libVorbis 1.3.7 with libogg linked in), which `libvgmstream.dll` imports |

and, on the machine running Nightrunner, the **Microsoft Visual C++ v14 x64 Redistributable** (the one Microsoft ships for
Visual Studio 2015 and later): `libvgmstream.dll` built with MSVC imports `VCRUNTIME140.dll` and the Universal CRT, and the
redistributable must be at least as new as the MSVC toolset that built the DLL (`PROVENANCE.txt` names it). `libvorbis.dll` needs only the system `msvcrt.dll`.

Nightrunner checks for these once at startup and logs what it found (`Audio decoder: …` or
`Audio decoding unavailable: …`).

## Building it

`tools/vgmstream/build-vgmstream.ps1` clones vgmstream at the pinned commit
`95cff213b1b1fbd292c313270cc679f02a1e624d`, builds `libvgmstream.dll` with upstream's documented shared-library CMake
options (Vorbis on, every other external codec, the CLI and the plugins off), copies upstream's `libvorbis.dll` and the
licence texts (`COPYING`, `ext_libs/licenses/libvorbis*`, `libogg*`) into `third_party/vgmstream/win-x64/`, and writes a
`PROVENANCE.txt` with the commit, compiler, flags and SHA-256 of both DLLs. It needs git and Visual Studio 2022 or later
with the C++ workload (its bundled CMake is used if none is on PATH). MSVC output embeds a timestamp, so rebuilt DLLs differ byte-for-byte; compare them by exports,
imports and the API version.

`third_party/vgmstream/win-x64/` is git-ignored: decoder binaries are never committed.

## Licences

vgmstream is under its ISC-style licence (`COPYING`); libvorbis and libogg are BSD-3-Clause (Xiph.Org Foundation).
Anyone distributing these DLLs must ship those texts with them (the build script copies them beside the DLLs). See
`THIRD_PARTY_NOTICES.md` at the repository root.

**Nightrunner does not distribute the decoder.** vgmstream's build compiles every file under `src/coding/libs`, and at
the pinned commit that includes code its `COPYING` does not cover: `relic_mixfft.c` (an FFT marked "For non-commercial use
only") and the `mio_erisa*` files (Leshade Entis, "All rights reserved"); its Wwise Vorbis codebooks say they were
extracted from the Wwise SDK. So a Nightrunner publish never contains `libvgmstream.dll` (the UI project copies it into
local build output only), and each user builds it for themselves with the script above.

## History

The audio contribution originally committed a `libvgmstream.dll` and a `libvorbis.dll` here. The `libvorbis.dll` was
byte-identical to upstream's `ext_libs/dll-x64/libvorbis.dll` at the commit above (not built by that contribution's CMake
configuration, as its README had said). The `libvgmstream.dll` had no recorded build command and no official
counterpart (upstream releases do not ship it), so it was removed rather than published; the script above replaces it.
