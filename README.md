# Tunqio

A Windows desktop music player for people who own their music: gapless bit-perfect playback, a fast local
library, and a native visualizer whose visuals react within one display refresh of the audio.

## For users

### What it is

Tunqio plays the music files on your own disks (FLAC, MP3, AAC/M4A, ALAC, Opus, Ogg Vorbis, WavPack, Monkey's
Audio, WAV, AIFF, WMA). It scans the folders you give it into a searchable library, plays albums gaplessly with
optional crossfade and ReplayGain, draws an audio-reactive visualization behind the music, and integrates with
Windows: the media keys and volume flyout, a tray icon, toasts, jump lists and file associations. There is no
account, no cloud and no telemetry.

### Supported Windows versions

Windows 10 version 2004 (build 19041) or later, x64; Windows 11 is recommended (ADR-001 in
[docs/decisions.md](docs/decisions.md)). The package declares `MinVersion="10.0.19041.0"`, so Windows refuses to
install it on anything older.

### Installing the MSIX

**The package is not signed yet.** Releases will stay self-signed rather than use a paid certificate (D-34), so installing
always means trusting Tunqio's certificate once per machine. Signed releases arrive with the release pipeline (E8-S1); until then CI builds
`Tunqio.msix` with signing turned off (the `tunqio-msix-unsigned` artifact of a CI run), and Windows will not
install an unsigned package. A tester signs it with a self-signed certificate whose subject is the manifest's
publisher, `CN=Tunqio`, trusts that certificate once, and installs. From an **administrator** Windows PowerShell,
in the folder holding the downloaded artifact:

```powershell
# 1. A code-signing certificate for CN=Tunqio (once per machine). Keep the thumbprint it prints.
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=Tunqio' -KeyUsage DigitalSignature `
    -FriendlyName 'Tunqio test signing' -CertStoreLocation Cert:\CurrentUser\My `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$cert.Thumbprint

# 2. Trust it for package installs (Local Machine > Trusted People; this is the step that needs administrator).
Export-Certificate -Cert $cert -FilePath .\Tunqio-test.cer
Import-Certificate -FilePath .\Tunqio-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

# 3. Sign the package with it (signtool ships with the Windows SDK).
& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint .\Tunqio.msix

# 4. Install, with the Windows App Runtime packages the build put beside it.
Add-AppxPackage -Path .\Tunqio.msix -DependencyPath (Get-ChildItem .\Dependencies\x64\*.msix).FullName
```

Double-clicking the signed `.msix` works too, once step 2 is done. Remove the certificate from Trusted People
when you no longer test Tunqio builds (`certlm.msc`); the E8-S1 signature replaces all of this.

Uninstall from Settings > Apps. Your library, settings and playlist exports are **not** removed (see below).

### Where your data lives

Everything is under `%LocalAppData%\Tunqio\` (the literal path, in the packaged and unpackaged app alike: the
manifest turns off MSIX file-system virtualisation so the folder survives an uninstall; [docs/identity.md](docs/identity.md)).

| What | Where |
|------|-------|
| Settings | `settings.json` |
| Library (catalogue, playlists, ratings, play history) | `library.db` (SQLite, with `library.db-wal` / `-shm` beside it) |
| Logs | `logs\tunqio-yyyyMMdd.log` (Settings > About & Diagnostics opens the folder) |
| Playlist exports | `exports\playlists\<name>.m3u8`, rewritten on every playlist change |
| Album art cache | `art\` |
| Your own visualization presets | `presets\` |

The library is a cache of your files plus your own work: deleting `library.db` and rescanning loses nothing but
ratings kept only in the database, and Settings > Library imports playlists back from `exports\`. The full layout
is in [docs/library-and-data.md](docs/library-and-data.md).

Third-party components and their licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), also shown in
Settings > About & Diagnostics.

## For developers

The design lives in [docs/](docs/README.md); start with [decisions.md](docs/decisions.md) and
[product-scope.md](docs/product-scope.md). Work is planned on the kanban board (`kanban next`).

### Layout

| Path | What |
|------|------|
| `native/mpcore/` | C++20 core DLL: audio (BASS), analysis, rendering behind the C ABI in `include/mpcore.h` |
| `native/mpcore.tests/` | Catch2 tests for the core (Debug, Release and ASan configurations) |
| `native/third_party/` | Vendored pffft, nlohmann/json, Catch2 (see `THIRD-PARTY-NOTICES.md`) |
| `src/Tunqio.Core` | Contracts, PlayQueue, PlaybackSession. Plain `net8.0`, no Windows |
| `src/Tunqio.Interop` | `LibraryImport` bindings over `mpcore.h`; the only project that names the DLL |
| `src/Tunqio.Library` | SQLite library, scanner, tags, art cache, playlists |
| `src/Tunqio.App` | WinUI 3 shell, MSIX manifest |
| `presets/` | Built-in visualization presets, placed beside `mpcore.dll` by the build |
| `tests/` | xUnit projects per layer; `Tunqio.Benchmarks` (BenchmarkDotNet gates, run in Release) |
| `tools/` | Build and verification scripts; `FixtureGen`, `IconGen`, `LatencyRunner`, `SoakRunner` |

### Prerequisites

Exact versions are pinned in the repository ([docs/build-test-release.md](docs/build-test-release.md), "Toolchain"):

| Item | Version | Pinned in |
|------|---------|-----------|
| Windows | 10 version 2004 (19041) or later, x64 | `Directory.Build.props` `TunqioMinOsVersion` |
| Visual Studio 2026 (Community is enough) | 18.x, with the **Desktop development with C++**, **.NET desktop development** and **WinUI application development** workloads | |
| MSVC toolset | v145 (installed by the C++ workload) | `TunqioPlatformToolset` |
| Windows SDK | 10.0.26100 (select it in the C++ workload if it is not ticked) | `TunqioWindowsSdkVersion` |
| .NET SDK | 10.0.301 or a later 10.0.3xx feature band (`rollForward: latestFeature`). The apps target `net8.0-windows`; the 10 SDK is for C# 14 | `global.json` |
| Git, Windows PowerShell 5.1 | any current | |
| Internet access on the first build | un4seen.com for BASS, nuget.org for packages | |

Optional: ffmpeg on `PATH` (`tools\fetch-ffmpeg.ps1` fetches the pinned one into `artifacts\ffmpeg`). Without it
three tag-writer tests skip themselves.

### Clone to a running app

Run these from a **Developer PowerShell** or plain Windows PowerShell at the repository root. Times beside each
step are what the fresh-clone run on card T-86 measured (see "Measured" below).

**1. Clone.**

```powershell
git clone https://github.com/PhilVon/Tunqio.git tunqio
cd tunqio
```

**2. Fetch the native packages.** BASS and its add-ons are not committed. This downloads the seven packages
listed in `tools\native-deps.json`, refuses any whose SHA-256 differs, and fills `native\bass\`:

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-native.ps1
```

A hash mismatch means un4seen re-released a package in place; see THIRD-PARTY-NOTICES.md before touching a hash.

**3. Build the solution, strictly, as CI does.** `msbuild` is normally **not on PATH** (not in plain PowerShell, not
in Git Bash), so find MSBuild.exe with vswhere and call it by its full path:

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
& $msbuild Tunqio.sln -restore -m:4 -p:Configuration=Release -p:Platform=x64 -warnaserror -nologo -v:minimal
```

`-m:4` caps parallelism so the machine stays usable; use `-m:2` when something else is building. Everything lands
under `artifacts\`.

**Why not `dotnet build`?** `Tunqio.sln` mixes C++ `.vcxproj` projects (the `mpcore.dll` engine and its tests) with
the C# projects, and the .NET CLI cannot build a `.vcxproj`: it needs the Visual C++ targets that only Visual
Studio's MSBuild.exe carries. `dotnet build Tunqio.sln` fails on the C++ projects, and `dotnet build
Tunqio.Managed.slnf` (the C# projects alone) builds an app with no audio engine: `Tunqio.App` warns that
`mpcore.dll` was not found, and a packaged build refuses. The reverse also bites: MSBuild.exe resolves the .NET
SDK only when the two .NET workloads above are installed; with the C++ workload alone the solution build fails
with NU1503, and `tools\build.ps1 -Configuration Release` builds the native projects with MSBuild.exe and the
managed ones with `dotnet build` instead. A green `dotnet build` is also not proof that the strict MSBuild.exe
build is green: the two use different compilers' warning sets, and CI runs the MSBuild.exe one.

**4. Run the tests.**

```powershell
# Native (Catch2, Release): the engine against the BASS "no sound" device, analysis, golden images on WARP.
artifacts\native\Release\x64\mpcore.tests.exe

# Managed (xUnit), against the build above.
dotnet test Tunqio.Managed.slnf -c Release -p:Platform=x64 --no-build -maxcpucount:4
```

CI additionally builds and runs the ASan configuration
(`& $msbuild native\mpcore.tests\mpcore.tests.vcxproj -p:Configuration=ASan -p:Platform=x64 -m:4`, then
`artifacts\native\ASan\x64\mpcore.tests.exe` and `tools\check-asan.ps1`), the format checks (`tools\check-format.ps1`,
`dotnet format --verify-no-changes`) and the performance gates (`dotnet run -c Release -p:Platform=x64 --project
tests\Tunqio.Benchmarks --no-build -- --gate`). `.github\workflows\ci.yml` is the full list.

**5. Run the app on a scratch profile.**

```powershell
artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe --data-root "$env:TEMP\tunqio-scratch"
```

`--data-root <folder>` keeps settings, the library, logs, art and presets under that folder instead of
`%LocalAppData%\Tunqio`, and runs as its own instance, so it never touches or redirects into a Tunqio you use for
real. Delete the folder to start over. Without the switch the app uses your real profile.

**Packaged build (optional).** The solution build above is the unpackaged app. The MSIX is built as CI's package
step does, after the solution build:

```powershell
& $msbuild src\Tunqio.App\Tunqio.App.csproj -m:4 -p:Configuration=Release -p:Platform=x64 -p:TunqioPackaged=true -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -nologo -v:minimal
```

It writes `artifacts\msix\Tunqio_Test\Tunqio.msix`, with the Windows App Runtime packages it depends on under
`artifacts\msix\Tunqio_Test\Dependencies\<arch>\` (installing it: "Installing the MSIX" above), and

```powershell
powershell -ExecutionPolicy Bypass -File tools\check-package.ps1 -Root artifacts\bin\Tunqio.App\release_win-x64_msix -Msix artifacts\msix\Tunqio_Test\Tunqio.msix
```

checks the engine, BASS, licences and icons are inside it.

### Measured

Card T-86 followed the steps above on 2026-09-14 from a fresh clone of main (f111806), with `-m:2` and
`-maxcpucount:2` in place of 4. **These are slower than a person's run would be:** the machine is Phil's 8-thread
desktop, in use, with two other agents building beside the run. Two things made them faster: the clone came from a
local repository, and the NuGet package cache already held every package, so a brand-new machine also spends a
few minutes downloading the clone (16 MB) and NuGet packages (roughly 1 GB).

| Step | Command | Exit | Wall clock |
|------|---------|------|-----------|
| 1 | `git clone` (local) | 0 | 1.8 s |
| 2 | `tools\fetch-native.ps1` (seven packages downloaded and verified) | 0 | 2.1 s |
| 3 | MSBuild.exe `Tunqio.sln -restore -m:2 ... -warnaserror` | 0, no warnings | 2 min 28 s |
| 4 | `mpcore.tests.exe` (211 test cases) | 0 | 5 min 22 s |
| 4 | `dotnet test Tunqio.Managed.slnf ... --no-build` (1760 tests) | 0 | 1 min 16 s |
| 5 | `Tunqio.exe --data-root <scratch>`: window shown, audio device opened, closed cleanly | 0 | window after 2.6 s |
| - | Packaged MSIX build, then `tools\check-package.ps1` over it | 0, PASS | 1 min 18 s + 2 s |

Clone to a running app (steps 1, 2, 3 and 5) took under 3 minutes; with both test suites, about 9 minutes.

### Build shapes

`Debug` and `Release` build the app unpackaged (`WindowsPackageType=None`) for a fast F5 with mixed-mode debugging
and for the managed tests; a package build (`-p:GenerateAppxPackageOnBuild=true`, as CI's package step passes)
produces the single-project MSIX. Override with `-p:TunqioPackaged=true|false`.

### Traps worth knowing

- `msbuild` is not on PATH; call MSBuild.exe by its full path (step 3).
- `dotnet build` cannot build the C++ projects (step 3).
- `native\bass\` is gitignored: a clone that skipped step 2 fails to link `mpcore.dll`.
- Windows PowerShell 5.1 reads a BOM-less `.ps1` as ANSI, so keep `tools\*.ps1` ASCII.
- A WinUI window captures black in a screenshot; the shell's on-screen checks are UI Automation harnesses under
  `tools\check-*.ps1`, which refuse to run while any Tunqio is open.

## Conventions

- Branch per kanban card: `T-<n>-<slug>`; `T-<n>` in commit subjects.
- C++: `/W4 /WX`, `clang-format` (`tools/check-format.ps1 -Fix`). C#: warnings as errors, `dotnet format`.
- Nothing on the audio path allocates or blocks after startup.
