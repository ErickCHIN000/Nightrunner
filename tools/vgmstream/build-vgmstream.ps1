<#
Builds the optional audio decoder for Nightrunner (libvgmstream.dll) from vgmstream's source at a pinned commit, and
puts it with upstream's own prebuilt libvorbis.dll and the licence texts into third_party\vgmstream\win-x64\.

Needs: git, Visual Studio 2022 or later with the "Desktop development with C++" workload (MSVC x64; its bundled CMake is
used when cmake is not on PATH), network access to github.com. Nothing is downloaded except the vgmstream repository.

The result needs the Microsoft Visual C++ v14 x64 Redistributable (VCRUNTIME140.dll), at least as new as the MSVC
toolset that built it, on the machine that runs Nightrunner. MSVC output is not bit-for-bit reproducible (it embeds a timestamp);
PROVENANCE.txt records what was built, from what, with which compiler.
#>
param(
    [string]$Commit = "95cff213b1b1fbd292c313270cc679f02a1e624d",
    [string]$Out = (Join-Path $PSScriptRoot "..\..\third_party\vgmstream\win-x64"),
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) "nightrunner-vgmstream")
)
$ErrorActionPreference = "Stop"

function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed ($LASTEXITCODE)" }
}

# Visual Studio with the x64 compiler, its CMake generator name, and a CMake (PATH first, else the one VS bundles).
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
if (-not $vs) { throw "Visual Studio with the C++ x64 tools (MSVC) was not found" }
$major = [int]($vs[0].installationVersion.Split('.')[0])
$generator = @{ 17 = "Visual Studio 17 2022"; 18 = "Visual Studio 18 2026" }[$major]
if (-not $generator) { throw "no CMake generator known for Visual Studio $major" }
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) { $cmake = Join-Path $vs[0].installationPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" }
if (-not (Test-Path $cmake)) { throw "CMake not found" }

if (-not (Test-Path (Join-Path $Work ".git"))) {
    Run git @("clone", "--filter=blob:none", "--no-checkout", "https://github.com/vgmstream/vgmstream.git", $Work)
}
Run git @("-C", $Work, "fetch", "origin", $Commit)
Run git @("-C", $Work, "checkout", "--force", $Commit)

$build = Join-Path $Work "build-nightrunner"
if (Test-Path $build) { Remove-Item -Recurse -Force $build }
# Upstream's documented shared-library build (doc/BUILD.md, "Shared lib"), with Vorbis the only external codec:
# DLTB and DL2 ship Wwise Vorbis and PCM WEMs only.
$flags = @(
    "-DBUILD_SHARED_LIBS:BOOL=YES", "-DBUILD_CLI:BOOL=OFF", "-DBUILD_FB2K:BOOL=OFF", "-DBUILD_WINAMP:BOOL=OFF",
    "-DBUILD_XMPLAY:BOOL=OFF", "-DBUILD_V123:BOOL=OFF", "-DBUILD_AUDACIOUS:BOOL=OFF",
    "-DUSE_VORBIS:BOOL=ON", "-DUSE_MPEG:BOOL=OFF", "-DUSE_FFMPEG:BOOL=OFF", "-DUSE_G7221:BOOL=OFF",
    "-DUSE_G719:BOOL=OFF", "-DUSE_ATRAC9:BOOL=OFF", "-DUSE_CELT:BOOL=OFF", "-DUSE_SPEEX:BOOL=OFF")
Run $cmake (@("-A", "x64", "-S", $Work, "-B", $build, "-G", $generator) + $flags)
Run $cmake @("--build", $build, "--config", "Release", "--target", "libvgmstream_shared")

$dll = Get-ChildItem $build -Recurse -Filter "libvgmstream.dll" | Where-Object { $_.FullName -match "\\Release\\" } | Select-Object -First 1
if (-not $dll) { throw "libvgmstream.dll was not produced under $build" }
$vorbis = Join-Path $Work "ext_libs\dll-x64\libvorbis.dll"   # upstream's prebuilt (MinGW-w64), what the build links against
if (-not (Test-Path $vorbis)) { throw "upstream libvorbis.dll not found at $vorbis" }

New-Item -ItemType Directory -Force $Out | Out-Null
Copy-Item $dll.FullName, $vorbis $Out -Force
$licenses = Join-Path $Out "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $Work "COPYING") (Join-Path $licenses "vgmstream.COPYING.txt") -Force
Get-ChildItem (Join-Path $Work "ext_libs\licenses") -File | Where-Object Name -match "^(libvorbis|libogg)" |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $licenses ($_.Name + ".txt")) -Force }

$probe = Get-ChildItem (Join-Path $build "CMakeFiles") -Recurse -Filter "CMakeCCompiler.cmake" | Select-Object -First 1
$compiler = ((Get-Content $probe.FullName | Select-String 'CMAKE_C_COMPILER_(ID|VERSION) "') |
             ForEach-Object { $_.Line -replace '.*"(.*)".*', '$1' }) -join " "
$lines = @(
    "vgmstream commit: $Commit (https://github.com/vgmstream/vgmstream)",
    "built: $((Get-Date).ToUniversalTime().ToString('u'))",
    "cmake: $((& $cmake --version | Select-Object -First 1))",
    "compiler: $compiler",
    "flags: -A x64 -G `"$generator`" $($flags -join ' '); config Release; target libvgmstream_shared",
    "libvorbis.dll: upstream prebuilt ext_libs/dll-x64/libvorbis.dll at the same commit (MinGW-w64 build of libVorbis 1.3.7 with libogg)",
    "needs: Microsoft Visual C++ v14 x64 Redistributable (VCRUNTIME140.dll + UCRT), at least as new as the toolset above")
foreach ($f in "libvgmstream.dll", "libvorbis.dll") {
    $lines += "sha256 ${f}: $((Get-FileHash (Join-Path $Out $f) -Algorithm SHA256).Hash)"
}
$lines | Set-Content -Encoding utf8 (Join-Path $Out "PROVENANCE.txt")
$lines | ForEach-Object { Write-Host $_ }
