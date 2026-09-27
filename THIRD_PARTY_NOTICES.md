# Third-party notices

Nightrunner is licensed under the MIT License; see [LICENSE](LICENSE).

This file lists the third-party components that are distributed with Nightrunner binaries or included in its
source, with the licence each is used under. The full texts are in [`licenses/`](licenses/), copied verbatim from
the package in question or from the upstream repository at the matching tag or commit (see *Sources* below). A
self-contained publish of `Nightrunner.UI` carries `LICENSE`, this file and `licenses/` next to the executable.

"Ships" means:

- **app binary**: inside the `Nightrunner.UI` self-contained win-x64 publish.
- **app binary, with audio support**: inside the same publish; used by the Audio window's player.
- **source (ported)**: third-party code ported into Nightrunner's own source files.
- **optional audio decoder**: native decoder DLLs that are not part of this repository and not part of any
  Nightrunner release; users build them with `tools/vgmstream/build-vgmstream.ps1` (see notes 5 and 6).
- **test-only (not distributed)**: used to build and run the tests; never part of a release.

The copyright column quotes the line in the licence text itself. Where the licence text has no copyright line, the
column says so, and the notices the upstream source carries are listed in the component's `*.notices.txt` file.

## App binary

| Component | Version | Ships | SPDX | Copyright (as in the licence text) | Full text |
|---|---|---|---|---|---|
| AvalonDock (`Dirkster.AvalonDock`, `.Core`, `.Themes.VS`, `.Themes.VS2013`) | 5.0.0 | app binary | MS-PL | None in the licence text. Notices in the source: "Copyright (C) 2007-2013 Xceed Software Inc."; "Copyright Microsoft Corporation. All Rights Reserved." | [AvalonDock.Ms-PL.txt](licenses/AvalonDock.Ms-PL.txt), [AvalonDock.notices.txt](licenses/AvalonDock.notices.txt) |
| Helix Toolkit (`HelixToolkit`, `.Geometry`, `.Maths`, `.SharpDX`, `.Wpf.SharpDX`) | 3.1.2 | app binary | MIT | Copyright (c) 2023 Helix Toolkit contributors | [HelixToolkit.MIT.txt](licenses/HelixToolkit.MIT.txt), [HelixToolkit.AUTHORS.txt](licenses/HelixToolkit.AUTHORS.txt), [HelixToolkit.notices.txt](licenses/HelixToolkit.notices.txt) |
| SharpDX (`SharpDX`, `.D3DCompiler`, `.DXGI`, `.Direct2D1`, `.Direct3D11`, `.Direct3D9`) | 4.2.0 | app binary | MIT | Copyright (c) 2010-2014 SharpDX - Alexandre Mutel | [SharpDX.MIT.txt](licenses/SharpDX.MIT.txt), [SharpDX.notices.txt](licenses/SharpDX.notices.txt) |
| BCnEncoder.NET (`BCnEncoder.Net`) | 2.3.0 | app binary | MIT (elected from `MIT OR Unlicense`) | Copyright 2020 Nomi Lakkala | [BCnEncoder.NET.MIT.txt](licenses/BCnEncoder.NET.MIT.txt), [BCnEncoder.NET.notices.txt](licenses/BCnEncoder.NET.notices.txt) |
| .NET Community Toolkit (`CommunityToolkit.Common` 8.3.2, `.Diagnostics` 8.3.2, `.Mvvm` 8.3.2, `.HighPerformance` 8.4.0) | 8.3.2 / 8.4.0 | app binary | MIT | Copyright © .NET Foundation and Contributors | [CommunityToolkit.MIT.md](licenses/CommunityToolkit.MIT.md), [CommunityToolkit.ThirdPartyNotices.txt](licenses/CommunityToolkit.ThirdPartyNotices.txt) |
| Cyotek.Drawing.BitmapFont | 2.0.4 | app binary | MIT | Copyright © 2012-2022 Cyotek Ltd. | [Cyotek.Drawing.BitmapFont.MIT.txt](licenses/Cyotek.Drawing.BitmapFont.MIT.txt) |
| .NET Compiler Platform "Roslyn" (`Microsoft.CodeAnalysis.Common`, `.CSharp`) | 4.11.0 | app binary | MIT | Copyright (c) .NET Foundation and Contributors | [Roslyn.MIT.txt](licenses/Roslyn.MIT.txt), [Roslyn.ThirdPartyNotices.rtf](licenses/Roslyn.ThirdPartyNotices.rtf) |
| `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` | 8.0.2 | app binary | MIT | Copyright (c) .NET Foundation and Contributors | [Microsoft.Extensions.MIT.txt](licenses/Microsoft.Extensions.MIT.txt), [Microsoft.Extensions.THIRD-PARTY-NOTICES.txt](licenses/Microsoft.Extensions.THIRD-PARTY-NOTICES.txt) |
| .NET runtime (`Microsoft.NETCore.App`, runtime pack `Microsoft.NETCore.App.Runtime.win-x64`) | 10.0.12 | app binary | MIT | Copyright (c) .NET Foundation and Contributors | [DotNet.Runtime.MIT.txt](licenses/DotNet.Runtime.MIT.txt), [DotNet.Runtime.THIRD-PARTY-NOTICES.txt](licenses/DotNet.Runtime.THIRD-PARTY-NOTICES.txt) |
| Windows Desktop runtime, WPF (`Microsoft.WindowsDesktop.App`, runtime pack `Microsoft.WindowsDesktop.App.Runtime.win-x64`) | 10.0.12 | app binary | MIT | Copyright (c) .NET Foundation and Contributors | [WindowsDesktop.Runtime.MIT.txt](licenses/WindowsDesktop.Runtime.MIT.txt), [WPF.THIRD-PARTY-NOTICES.txt](licenses/WPF.THIRD-PARTY-NOTICES.txt) |
| NAudio (`NAudio.Core`, `NAudio.Wasapi`) | 3.1.0 | app binary, with audio support | MIT | Copyright 2008-2026 Mark Heath | [NAudio.MIT.txt](licenses/NAudio.MIT.txt), [NAudio.notices.txt](licenses/NAudio.notices.txt) |
| System.Numerics.Tensors (dependency of NAudio.Core) | 9.0.0 | app binary, with audio support | MIT | Copyright (c) .NET Foundation and Contributors | [System.Numerics.Tensors.MIT.txt](licenses/System.Numerics.Tensors.MIT.txt), [System.Numerics.Tensors.THIRD-PARTY-NOTICES.txt](licenses/System.Numerics.Tensors.THIRD-PARTY-NOTICES.txt) |

The closure above is every package with runtime assets for `net10.0-windows` / `win-x64` in
`Nightrunner.UI/obj/project.assets.json`. `Microsoft.CodeAnalysis.Analyzers` 3.3.4, `NETStandard.Library` 1.6.1 and
`Microsoft.NETCore.Platforms` 1.1.0 are in the restore graph but contribute no runtime files. Roslyn and the
`Microsoft.Extensions` abstractions come in through `HelixToolkit.SharpDX` and `HelixToolkit`; they are not used by
Nightrunner directly.

## Source (ported)

| Component | Version | Ships | SPDX | Copyright (as in the licence text) | Full text |
|---|---|---|---|---|---|
| Cast (dtzxporter/cast), ported to C# in `Nightrunner.Core/Cast/CastFile.cs`, `CastNode.cs`, `CastNodes.cs` | commit `a8ca18a0acf3b97b19332c53b54b47fcc3217755` | source (ported), compiled into the app binary | MIT | Copyright (c) 2020 Nick | [Cast.MIT.txt](licenses/Cast.MIT.txt) |

The rest of Nightrunner is a port of its own Python prototype (archived; MIT, same owner), so it
is not listed as third-party. The prototype's only third-party licence file is its copy of the Cast licence.

## Optional audio decoder

These native DLLs are not part of this repository, and a Nightrunner publish does not contain them. Users who want
audio decoding build them locally (`tools/vgmstream/build-vgmstream.ps1`, which copies the vgmstream, libvorbis and
libogg texts next to them). They are listed so that their notices are known; see notes 5 and 6 for why Nightrunner
does not redistribute `libvgmstream.dll`.

| Component | Version | Ships | SPDX | Copyright (as in the licence text) | Full text |
|---|---|---|---|---|---|
| vgmstream (`libvgmstream.dll`) | commit `95cff213b1b1fbd292c313270cc679f02a1e624d` | optional audio decoder (not distributed) | ISC-style (see note 1) | Copyright (c) 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm, Christopher Snowhill, NicknineTheEagle, bxaimc, Thealexbarney, CyberBotX, EdnessP, et al (plus the "Portions" lines in the file) | [vgmstream.COPYING.txt](licenses/vgmstream.COPYING.txt), [vgmstream.notices.txt](licenses/vgmstream.notices.txt) |
| ww2ogg (vgmstream's Wwise Vorbis decoder is converted from it) | COPYING as of commit `14ed9b0dd62e815a38702b5f03c57006cbe2501b` (same text as tag 0.24) | optional audio decoder (not distributed), inside `libvgmstream.dll` | BSD-3-Clause | Copyright (c) 2002, Xiph.org Foundation; Copyright (c) 2009-2016, Adam Gashlin | [ww2ogg.BSD-3-Clause.txt](licenses/ww2ogg.BSD-3-Clause.txt) |
| libvorbis (`libvorbis.dll`) | 1.3.7 | optional audio decoder (not distributed) | BSD-3-Clause | Copyright (c) 2002-2020 Xiph.org Foundation | [libvorbis.BSD-3-Clause.txt](licenses/libvorbis.BSD-3-Clause.txt) |
| libogg (statically inside `libvorbis.dll`) | 1.3.5 (see note 2) | optional audio decoder (not distributed) | BSD-3-Clause | Copyright (c) 2002, Xiph.org Foundation | [libogg.BSD-3-Clause.txt](licenses/libogg.BSD-3-Clause.txt) |
| MinGW-w64 runtime (statically inside `libvorbis.dll`) | runtime version not recorded in the binary (see note 3) | optional audio decoder (not distributed) | LicenseRef-MinGW-w64-runtime (several permissive notices) | Copyright (c) 2009, 2010, 2011, 2012, 2013 by the mingw-w64 project (overall notice; per-part notices in the file) | [MinGW-w64-runtime.COPYING.txt](licenses/MinGW-w64-runtime.COPYING.txt) |

## Test-only (not distributed)

`Nightrunner.Tests` uses `xunit.v3` 4.0.1 and `xunit.runner.visualstudio` 4.0.0 (Apache-2.0, "Copyright (C) .NET
Foundation") with their `xunit.v3.*` dependencies, and `Microsoft.NET.Test.Sdk` 18.10.1 (MIT). They are restored from
NuGet to build and run the tests and are never copied into a release, so no licence texts are carried for them. The
`tools/*` check programs reference no packages.

## Notes

1. **vgmstream licence.** The COPYING text is the ISC permission and disclaimer with "copy, modify, and distribute"
   where the current ISC text reads "copy, modify, and/or distribute"; it is recorded as ISC-style rather than
   asserted as an SPDX `ISC` match.
2. **libogg version.** `libvorbis.dll` is byte-identical (git blob `b5fb43226f9473f0cc1decfb532ad3d63caf94c2`) to
   vgmstream's prebuilt `ext_libs/dll-x64/libvorbis.dll` at the pinned commit. It reports "Xiph.Org libVorbis 1.3.7",
   imports only `KERNEL32.dll` and `msvcrt.dll`, and exports the `ogg_*` functions, so libogg is linked in statically.
   The binary carries no libogg version string; 1.3.5 is the version vgmstream's `ext_libs/licenses/libogg-1.3.5.COPYING`
   names, and that file matches Xiph's `COPYING` at tag v1.3.5.
3. **MinGW-w64 runtime and libgcc.** `libvorbis.dll` contains the string "GCC: (i686-win32-sjlj-rev0, Built by
   MinGW-W64 project) 12.2.0" and the MinGW-w64 startup code ("Mingw-w64 runtime failure:"), so the MinGW-w64
   runtime notice applies. The exact runtime release is not recorded; `COPYING.MinGW-w64-runtime.txt` is the same
   file (blob `ca6a077e24b81d4f0ae9b952b6fbcaf370a78d6a`) at mingw-w64 tags v8.0.0, v9.0.0, v10.0.0, v11.0.0,
   v12.0.0 and v13.0.0.
   Any libgcc code linked into the DLL is covered by the GCC Runtime Library Exception 3.1, which, for code produced by an
   eligible compilation process such as a normal GCC build, lets the resulting binary be distributed under any terms
   and adds no notice requirement, so no libgcc text is carried. Whether
   libgcc objects are present at all cannot be established from the stripped binary.
4. **Wwise codebook data.** `libvgmstream.dll` embeds the Wwise Vorbis codebooks
   (`src/coding/libs/vorbis_codebooks_wwise.h`), which the vgmstream source describes as "Extracted from Wwise SDK
   by ww2ogg". Licence status unresolved (data originates from Audiokinetic's SDK per vgmstream source comments).
   This is a legal note, not a licence.
5. **UNRESOLVED: relic_mixfft in `libvgmstream.dll`.** vgmstream's CMake build compiles every file in
   `src/coding/libs`, and the pack's `libvgmstream.dll` contains the Relic codec ("Relic Codec v1.6"). Its FFT,
   `relic_mixfft.c`, is a decompilation of Jens Joergen Nielsen's mixfft.c, whose notice reads "For non-commercial
   use only. A $100 fee must be paid if used commercially." (quoted in `vgmstream.notices.txt`). vgmstream's COPYING
   does not cover it. That restriction is not compatible with redistributing the pack alongside MIT software without
   conditions. Not resolved here.
6. **UNRESOLVED: ERISA-Library code in `libvgmstream.dll`.** The Entis MIO decoder (present in the DLL: "Entis
   MIO") is adapted from Leshade Entis's ERISA-Library, "Copyright (C) 2002-2003 Leshade Entis, Entis-soft. All
   rights reserved.", which vgmstream's source describes as "licensed under a custom license somewhat equivalent to
   GPL". The original licence text was not located. Not resolved here.
7. **Other code inside `libvgmstream.dll`.** miniz (MIT; notice in `vgmstream.notices.txt`) and maac (0BSD, no
   notice requirement; text in `vgmstream.notices.txt`) are compiled in. libacm, the NWA decoder and the Sun G.721
   code are credited in vgmstream's COPYING.
8. **`libvgmstream.dll` runtime.** It is an MSVC build that imports `VCRUNTIME140.dll` and the Universal CRT; neither
   is shipped by Nightrunner.
9. **AvalonDock notices.** The Ms-PL text in the packages has no copyright line, and section 3(C) requires keeping
   the notices present in the software. The AvalonDock v5.0.0 source carries "Copyright (C) 2007-2013 Xceed Software
   Inc." (AvalonDock is a fork of Xceed's AvalonDock) and, in `Controls/Shell`, "Copyright Microsoft Corporation. All
   Rights Reserved."; its build properties set `<Copyright>2017-2026</Copyright>` with no holder named. These are
   quoted in `AvalonDock.notices.txt`. No named holder for the 2017-2026 work was found in the repository `LICENSE`,
   `README.md` or source headers at that tag.
10. **Helix Toolkit notices.** The shipped Helix assemblies include code under third-party notices (SharpDX, SlimDX,
    Nick Gravelyn and Markus Ewald's sprite packer, Xenko's FastList, AssimpNet, DirectXTK via Justin Stenning's
    CMO reader) and the compiled NVIDIA FXAA 3.11 shader, whose BSD-style notice asks binary redistributions to
    reproduce it. All are quoted in `HelixToolkit.notices.txt`. `HelixToolkit.SharpDX.dll` also embeds a bitmap
    font atlas generated from Arial (`arial.dds`, `arial.fnt`); upstream states no separate licence for it.
11. **Other embedded notices.** SharpDX, BCnEncoder.NET and NAudio carry notices from code they incorporate
    (SlimDX; Sun's fdlibm; SpanDSP / CMU G.722, Stephan M. Bernsee's pitch shifter, Cockos WDL, Ray Molenkamp's
    Core Audio wrapper). They are quoted in the matching `*.notices.txt` files.

## Sources

Every text in `licenses/` is a byte-for-byte copy of the file named here, except the `*.notices.txt` files, which
quote verbatim line ranges (named in each block) from the upstream source under a short heading.

| File | Taken from |
|---|---|
| AvalonDock.Ms-PL.txt | NuGet package `dirkster.avalondock` 5.0.0, `LICENSE` (identical in all four AvalonDock packages; same text as the repository `LICENSE` at tag v5.0.0) |
| AvalonDock.notices.txt | github.com/Dirkster99/AvalonDock, tag v5.0.0 = commit `408dc2896e2f41f3bb79a15207f160edee8a6792` (the commit the packages name) |
| HelixToolkit.MIT.txt, HelixToolkit.AUTHORS.txt | NuGet package `helixtoolkit` 3.1.2, `LICENSE` and `AUTHORS` (identical in all five Helix packages) |
| HelixToolkit.notices.txt | github.com/helix-toolkit/helix-toolkit, tag v3.1.2 = commit `0d10a9fc27bbe06d4ce74c580612217af14d3fc6` |
| SharpDX.MIT.txt | github.com/sharpdx/SharpDX `License.txt`, tag v4.2.0 = commit `8e5df9f17b1d328c595a9df5851dbb2537b55621` (the packages carry only a `licenseUrl`, http://sharpdx.org/License.txt, and no licence file) |
| SharpDX.notices.txt | same tag, `Source/SharpDX/DataStream.cs` |
| BCnEncoder.NET.MIT.txt | github.com/Nominom/BCnEncoder.NET `LICENSE-MIT`, commit `74eda8a330759d81e6307f19aefc547e40687825` (named by the package); identical at tag RELEASE-2.3.0 |
| BCnEncoder.NET.notices.txt | same repository, tag RELEASE-2.3.0 = commit `51d3199b3bc374e0bdda3947d9f347cb6235a9a0` |
| CommunityToolkit.MIT.md, CommunityToolkit.ThirdPartyNotices.txt | NuGet package `communitytoolkit.common` 8.3.2, `License.md` and `ThirdPartyNotices.txt` (identical in the `.Diagnostics` 8.3.2, `.Mvvm` 8.3.2 and `.HighPerformance` 8.4.0 packages) |
| Cyotek.Drawing.BitmapFont.MIT.txt | github.com/cyotek/Cyotek.Drawing.BitmapFont `LICENSE.txt`, tag v2.0.4 = commit `2d5e22b2a3661cf11364c5755140d0d2f363a7a0` (see below) |
| Roslyn.MIT.txt | github.com/dotnet/roslyn `License.txt`, commit `5e3a11e2e7f952da93f9d35bd63a2fa181c0608b` (named by the 4.11.0 packages) |
| Roslyn.ThirdPartyNotices.rtf | NuGet package `microsoft.codeanalysis.common` 4.11.0, `ThirdPartyNotices.rtf` (identical in `.csharp` 4.11.0) |
| Microsoft.Extensions.MIT.txt, Microsoft.Extensions.THIRD-PARTY-NOTICES.txt | NuGet package `microsoft.extensions.logging.abstractions` 8.0.2, `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` (identical in `.dependencyinjection.abstractions` 8.0.2) |
| DotNet.Runtime.MIT.txt, DotNet.Runtime.THIRD-PARTY-NOTICES.txt | NuGet runtime pack `microsoft.netcore.app.runtime.win-x64` 10.0.12, `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` |
| WindowsDesktop.Runtime.MIT.txt | NuGet runtime pack `microsoft.windowsdesktop.app.runtime.win-x64` 10.0.12, `LICENSE` |
| WPF.THIRD-PARTY-NOTICES.txt | github.com/dotnet/dotnet `src/wpf/THIRD-PARTY-NOTICES.TXT`, tag v10.0.12 = commit `95017c711e6afc1085133d440e42b4bd78155701` (the commit both runtime packs name; the Windows Desktop pack carries no notices file) |
| Cast.MIT.txt | github.com/dtzxporter/cast `LICENSE`, commit `a8ca18a0acf3b97b19332c53b54b47fcc3217755` (the commit `CastFile.cs` names) |
| NAudio.MIT.txt, NAudio.notices.txt | github.com/naudio/NAudio `LICENSE`, tag v3.1.0 = commit `0aaef29d04bec9567bdf2f669036fabecc33a2e2` (the commit the packages name; the packages carry only a licence expression) |
| System.Numerics.Tensors.MIT.txt, System.Numerics.Tensors.THIRD-PARTY-NOTICES.txt | NuGet package `system.numerics.tensors` 9.0.0, `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` |
| vgmstream.COPYING.txt, vgmstream.notices.txt | github.com/vgmstream/vgmstream `COPYING` and source, commit `95cff213b1b1fbd292c313270cc679f02a1e624d` |
| ww2ogg.BSD-3-Clause.txt | github.com/hcs64/ww2ogg `COPYING`, commit `14ed9b0dd62e815a38702b5f03c57006cbe2501b` (identical at tag 0.24) |
| libvorbis.BSD-3-Clause.txt | github.com/xiph/vorbis `COPYING`, tag v1.3.7 (identical to vgmstream's `ext_libs/licenses/libvorbis-1.3.7.COPYING`) |
| libogg.BSD-3-Clause.txt | github.com/xiph/ogg `COPYING`, tag v1.3.5 (identical to vgmstream's `ext_libs/licenses/libogg-1.3.5.COPYING`) |
| MinGW-w64-runtime.COPYING.txt | mingw-w64 project git on SourceForge (git.code.sf.net/p/mingw-w64/mingw-w64), `COPYING.MinGW-w64-runtime/COPYING.MinGW-w64-runtime.txt` at tag v10.0.0 = commit `aa08f56da559016f10336dddca85d59f9bdc9e02` |

Cyotek.Drawing.BitmapFont: the 2.0.4 package names commit `1a4324d8aa08626fa0bfeaea975614e62125c26b`, whose
`LICENSE.txt` reads "2012-2021" and whose build properties still say version 2.0.3. Tag v2.0.4 is the next commit; it
sets version 2.0.4 and "Copyright © 2012-2022 Cyotek Ltd.", which is what the package's own metadata carries, so the
tag's text is used.
