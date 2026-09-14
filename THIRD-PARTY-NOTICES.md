# Third-party notices

Every third-party component that ships in the Tunqio MSIX, with its licence. Checked against the contents of a
packaged Release build (`Tunqio.msix`, 404 entries) on card T-86, 2026-09-14; the check is described at the end.

Settings > About & Diagnostics reads this file (T-198): every row of both tables of shipped components, the native
rows (the BASS packages and the vendored sources compiled into `mpcore.dll`) and the NuGet packages and the runtime,
is listed there with the licence its row names. A row whose `Ships` cell starts with "No" is kept here for
completeness and is not shown as shipped, and the build-only tools under "Not shipped" are not rows at all.

## BASS audio library (proprietary, free for non-commercial use)

Audio playback uses the BASS audio library and its add-ons (BASSmix, BASSWASAPI, BASSFLAC, BASSOPUS, BASSWV, BASS_APE) by Un4seen Developments, www.un4seen.com, under the free non-commercial licence.

Tunqio is a non-commercial product (decision Q-1 on card T-1), so BASS and its add-ons are used under the
free non-commercial licence stated in each package's text file. The About page shows the attribution above
and the licence texts, which `tools/fetch-native.ps1` extracts into `native/bass/licenses/` and the build
copies next to the executable under `licenses/`. Should distribution ever become commercial, ADR-003 has to be
revisited and a BASS licence purchased before release.

| Package | Version | Source | Licence | Licence text |
|---------|---------|--------|---------|--------------|
| `bass` | 2.4.18 | https://www.un4seen.com/files/bass24.zip | Free for non-commercial use (Un4seen Developments Ltd.) | `licenses/bass.txt` |
| `bassmix` | 2.4.13 | https://www.un4seen.com/files/bassmix24.zip | Free to use with BASS | `licenses/bassmix.txt` |
| `basswasapi` | 2.4.4 | https://www.un4seen.com/files/basswasapi24.zip | Free to use with BASS | `licenses/basswasapi.txt` |
| `bassflac` | 2.4.6 | https://www.un4seen.com/files/bassflac24.zip | Free to use with BASS; FLAC decoding based on libFLAC (BSD) | `licenses/bassflac.txt` |
| `bassopus` | 2.4.3 | https://www.un4seen.com/files/bassopus24.zip | Free to use with BASS; Opus decoding based on libopus (BSD) | `licenses/bassopus.txt` |
| `basswv` | 2.4.7 | https://www.un4seen.com/files/basswv24.zip | Free to use with BASS; WavPack decoding based on libwavpack (BSD) | `licenses/basswv.txt` |
| `bass_ape` | 2.4.2 | https://www.un4seen.com/files/z/2/bass_ape24.zip | Third-party add-on by Sebastian Andersson; Monkey's Audio SDK by Matthew T. Ashland | `licenses/bass_ape.txt` |

The exact SHA-256 of every package is pinned in `tools/native-deps.json`; the fetch script refuses a
package whose hash differs. Binaries are not committed (`native/bass/` is gitignored).

Un4seen publish each package at a stable URL and re-release **in place**, so a pin goes stale whenever they
push a new build and `tools/fetch-native.ps1` then fails with a hash mismatch. That is the pin working, not a
bug: treat it as a dependency update. Before changing a hash, confirm the new archive is a genuine re-release —
its version resource and the version history in the package's text file should agree and name a new release,
the file set should not have gained entries, and the DLLs should carry a valid Un4seen Developments Authenticode
signature. Bump the version in the table above in the same commit. Note that CI caches `native/bass/.cache`
keyed on `tools/native-deps.json`, so a stale pin stays invisible on CI until the file changes.

**BASS_AAC is not used.** The Windows BASS_AAC add-on ships under the GPL (its archive carries `gpl.txt`),
which the build's licence policy excludes. AAC, M4A, ALAC and WMA decode through BASS's built-in Media
Foundation codec support on Windows 10 and 11 instead, so no add-on is needed for them.

## NuGet packages and the runtime

These ship as assemblies in the package, pinned in `Directory.Packages.props` (versions below are the ones in the
package). The About page lists every row of this table with its licence. Their licence texts are not yet shipped
beside the executable; the release pipeline collects them (E8-S1). The `Files in the package` column is what
`tools/check-package.ps1` matches the package's entries against.

| Component | Version | Files in the package | Licence |
|-----------|---------|----------------------|---------------|
| .NET runtime and Windows Desktop runtime (self-contained) | 8.0.28 | `System.dll`, `System.*.dll`, `Microsoft.CSharp.dll`, `Microsoft.Win32.*.dll`, `coreclr.dll`, `clrjit.dll`, `clrgc*.dll`, `clretwrc.dll`, `mscor*.dll`, `netstandard.dll`, `hostfxr.dll`, `hostpolicy.dll`, `createdump.exe`, `Microsoft.DiaSymReader.Native.amd64.dll`, `msquic.dll`, `Microsoft.VisualBasic*.dll`; WPF and Windows Forms assemblies (`Presentation*.dll`, `PenImc_cor3.dll`, `wpfgfx_cor3.dll`, `vcruntime140_cor3.dll`, `D3DCompiler_47_cor3.dll`, `UIAutomation*.dll`, `WindowsBase.dll`, `WindowsFormsIntegration.dll`, `ReachFramework.dll`, `DirectWriteForwarder.dll`, `Accessibility.dll`), which the self-contained Windows Desktop runtime carries whole | MIT (.NET Foundation and Microsoft) |
| Windows App SDK and WinUI 3 | 1.8.260804001 | `Microsoft.WinUI.dll`, `Microsoft.Windows.*.Projection.dll`, `Microsoft.WindowsAppRuntime.Bootstrap*.dll`, `Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll`, `Microsoft.InteractiveExperiences.Projection.dll`, `Microsoft.Graphics.Imaging.Projection.dll`, `Microsoft.Security.Authentication.OAuth.Projection.dll`, `Microsoft.ML.OnnxRuntime.dll` (Windows ML projection), `resources.pri` | Microsoft Software License Terms, Microsoft Windows App SDK (and Windows Machine Learning for the ML part); redistributable |
| Windows App Runtime 1.8 (framework package) | 1.8 | Not inside `Tunqio.msix`: a package dependency installed beside it (`Dependencies\x64\Microsoft.WindowsAppRuntime.1.8.msix`) | Microsoft Software License Terms, Microsoft Windows App SDK |
| C#/WinRT runtime and Windows SDK projection | 2.2.0 / 10.0.19041.55 | `WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.dll` | MIT (C#/WinRT); Microsoft Windows SDK licence (projection) |
| Microsoft Edge WebView2 SDK | 1.0.3179.45 | `Microsoft.Web.WebView2.Core*.dll`, `Microsoft.Web.WebView2.Core.winmd`, `WebView2Loader.dll` (a Windows App SDK dependency; Tunqio shows no web content) | BSD-3-Clause-style (Microsoft WebView2 SDK licence) |
| CommunityToolkit.Mvvm | 8.4.2 | `CommunityToolkit.Mvvm.dll` | MIT |
| H.NotifyIcon.WinUI, H.NotifyIcon, H.GeneratedIcons.System.Drawing | 2.3.2 | `H.NotifyIcon.WinUI.dll`, `H.NotifyIcon.dll`, `H.GeneratedIcons.System.Drawing.dll` | MIT |
| System.Drawing.Common, Microsoft.Win32.SystemEvents | 9.0.1 | `System.Drawing.Common.dll`, `Microsoft.Win32.SystemEvents.dll` (H.NotifyIcon dependencies) | MIT |
| Microsoft.Extensions.Hosting and its dependencies (Configuration, DependencyInjection, Logging, Options, FileProviders, Diagnostics, Primitives) | 8.0.x | `Microsoft.Extensions.*.dll` | MIT |
| Serilog, Serilog.Extensions.Hosting, Serilog.Extensions.Logging, Serilog.Sinks.File, Serilog.Sinks.Debug | 4.4.0, 8.0.0, 8.0.0, 7.0.0, 3.0.0 | `Serilog*.dll` | Apache-2.0 |
| System.Reactive | 6.1.0 | `System.Reactive.dll` | MIT |
| Microsoft.Data.Sqlite | 8.0.31 | `Microsoft.Data.Sqlite.dll` | MIT |
| SQLitePCLRaw (core, provider, bundle) | 2.1.12 | `SQLitePCLRaw.*.dll` | Apache-2.0 |
| SQLite (native build `e_sqlite3`) | via SQLitePCLRaw.lib.e_sqlite3 2.1.12 | `e_sqlite3.dll` | Public domain |
| TagLibSharp | 2.3.0 | `TagLibSharp.dll`, dynamically linked and unmodified, as LGPL 2.1 requires | LGPL-2.1-only |

## Vendored native sources (`native/third_party/`)

pffft and nlohmann/json are compiled into `mpcore.dll` and so ship in the package. Catch2 is a test-only
dependency and is not part of the shipped package.

| Component | Version | Source | Licence | Ships | Files | SHA-256 |
|-----------|---------|--------|---------|-------|-------|---------|
| Catch2 | v3.16.0 | https://github.com/catchorg/Catch2 (`extras/catch_amalgamated.*`) | Boost Software License 1.0 (`catch2/LICENSE.txt`) | No: test-only (`mpcore.tests.exe`) | `catch2/catch_amalgamated.hpp` | `d4cc143ea76ae212204363922d8adf376d66a1fda5a33ac73f93a7d1c119f4e0` |
| | | | | | `catch2/catch_amalgamated.cpp` | `1fe7f10334e0ae5494419cfa84c270f15235eada0fc02bdb493d1def537601f3` |
| nlohmann/json | v3.12.0 | https://github.com/nlohmann/json (`single_include/nlohmann/json.hpp`) | MIT (`nlohmann/LICENSE.MIT`) | Yes, compiled into `mpcore.dll` | `nlohmann/json.hpp` | `aaf127c04cb31c406e5b04a63f1ae89369fccde6d8fa7cdda1ed4f32dfc5de63` |
| pffft | commit `0aec0327a6912e1a0ec5326eef737c2ce19bc836` (2026-08-14) | https://bitbucket.org/jpommier/pffft | FFTPACK licence (BSD-style; text at the top of `pffft.h`) | Yes, compiled into `mpcore.dll` | `pffft/pffft.h` | `d6ac7f26f7c3f87ed2ad7f0264c09d72285526d937b4dccc5fc1c97645a0d55d` |
| | | | | | `pffft/pffft.c` | `485f2c641b9bc9434720757307e825c2f694b682438da9052959ab1e445e1f16` |

Vendoring was chosen over a vcpkg manifest (E0-S1 "decide and record"): three small, stable dependencies,
no bootstrap step on CI or a fresh clone, and the exact bytes are hash-pinned here. Update by replacing the
files and the hashes in the same commit.

## Not shipped

Build-time and test-only packages are not in the package: tools/IconGen's Svg.Skia and SkiaSharp, BenchmarkDotNet,
xUnit, FluentAssertions, NetArchTest, coverlet, Microsoft.NET.Test.Sdk, System.IO.Hashing (FixtureGen and IconGen
only), Microsoft.VisualStudio.Threading.Analyzers and Microsoft.Windows.SDK.BuildTools.

## How this list was checked

Build the package as CI's package step does, then read `Tunqio.msix` as the zip it is:

```powershell
& $msbuild src\Tunqio.App\Tunqio.App.csproj -p:Configuration=Release -p:Platform=x64 -p:TunqioPackaged=true -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path artifacts\msix\Tunqio_Test\Tunqio.msix))
$zip.Entries.FullName | Where-Object { $_ -notmatch '^(System\.|Tunqio|Assets/|presets/|licenses/)' }
$zip.Dispose()
```

Every file that list prints belongs to a row above. When a package is added to `Directory.Packages.props` or the
package gains a file this page does not account for, add the row in the same change.

Since T-198 `tools/check-package.ps1 -Msix` makes this a gate, and CI runs it on the package it builds: every entry
of `Tunqio.msix` other than Tunqio's own (`Tunqio*`, `mpcore.dll`, `Assets/`, `presets/`, `licenses/` and the
package's `Appx*` and `[Content_Types].xml` records) has to match a BASS package row or a pattern in the
`Files in the package` column above, or the check fails and names the file.
