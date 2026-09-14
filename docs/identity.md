# Product Identity

The single source of truth for every string that names the product. Code, manifests, scripts and the other design documents take their values from this page; if a value here changes, this page changes first. Decided on card T-1 (Q-11, 2026-09-08); applied by story E0-S8 (card T-18).

## The name

**Tunqio.** Written exactly so in prose and UI: capital T, the rest lowercase, never all-caps, never "TunqIO", never "tunqio" in running text. Lowercase `tunqio` appears only inside machine identifiers (the URI scheme, the execution alias, log file names).

The native library is **not** renamed. It stays `mpcore.dll` with the `mp_` ABI prefix, because it is an internal component with its own versioned ABI and its name is never user-visible (ADR-004).

## Identity table

Values marked **frozen at 1.0** cannot change after the first public release without breaking in-place upgrade, pinned shortcuts, toast registrations or user bookmarks (ADR-011, risk R-19). Everything else may be tuned freely.

| Surface | Value | Where it lives | Frozen at 1.0 |
|---------|-------|----------------|---------------|
| Display name | `Tunqio` | `Package.appxmanifest` `DisplayName`, `uap:VisualElements DisplayName`, About page | no |
| Short description | `Tunqio music player` | manifest `Description` | no |
| Package identity name | `Tunqio` | manifest `Identity Name` | **yes** |
| Publisher (self-signed) | `CN=Tunqio` | manifest `Identity Publisher`; must match the signing certificate subject | **yes**: stays `CN=Tunqio`, since releases stay self-signed for the foreseeable future (D-34, Phil 2026-09-14); moving to a paid identity later would break in-place upgrade (R-19) |
| Publisher display name | `Tunqio` | manifest `PublisherDisplayName` | no |
| Application Id | `Tunqio` | manifest `Application Id` | **yes** (it is half of the AUMID) |
| Application User Model ID | `<PackageFamilyName>!Tunqio` | derived by Windows from identity name, publisher hash and Application Id; never hard-coded | derived |
| Executable | `Tunqio.exe` | `Tunqio.App.csproj` `AssemblyName=Tunqio` | no (but keep it) |
| Execution alias | `tunqio.exe` | manifest `uap5:AppExecutionAlias` | **yes** |
| URI scheme | `tunqio` | manifest `uap:Protocol Name`, `CommandRouter` | **yes** |
| File type association group | `tunqio-audio` | manifest `uap:FileTypeAssociation Name`; display name `Tunqio audio file` | **yes** (ProgId derives from it) |
| Data folder (logical) | `%LocalAppData%\Tunqio\` | `IAppPaths.DataRoot`; layout in [library-and-data.md](library-and-data.md) | **yes** |
| Log files | `%LocalAppData%\Tunqio\logs\tunqio-yyyyMMdd.log` | Serilog sink in `Tunqio.App` | no |
| Solution and projects | `Tunqio.sln`, `Tunqio.Core`, `Tunqio.Interop`, `Tunqio.Library`, `Tunqio.App` and their `.Tests`; root namespace `Tunqio` | [solution-structure.md](solution-structure.md) | no |
| Repository folder | `tunqio/` | [solution-structure.md](solution-structure.md) | no |
| Native core | `mpcore.dll`, `mpcore.h`, `mp_` prefix; `mp_version()` returns the product version | ADR-004 | ABI-versioned separately |
| Release artefacts | `Tunqio_<ver>_x64.msix`, `Tunqio.appinstaller` | `release.yml`, [build-test-release.md](build-test-release.md) | the `.appinstaller` URL is **frozen** |
| Version | SemVer `major.minor.patch` in `Directory.Build.props`; MSIX `major.minor.patch.0`; `mp_version()` reports the same string | ADR-011 | scheme frozen |

## User-visible strings

These are formats, not identifiers, and may be tuned after user testing.

| Surface | Idle | Track loaded |
|---------|------|--------------|
| Main window title | `Tunqio` | `<Title> – <Artist> — Tunqio` |
| Tray tooltip | `Tunqio` | `<Title> – <Artist>` (Windows truncates tooltips at 127 characters; trim the title first, keep the artist) |
| Mini player | no title bar | n/a |
| SMTC (volume flyout) | shows the display name automatically | title, artist, album, art from `PlaybackSession` |
| Toast header | shows the display name automatically | body per E7-S4 |
| Taskbar / Start | `Tunqio` (from `VisualElements`) | n/a |

The en dash between title and artist and the em dash before the product name are deliberate: the three-part window title reads as two fields plus a suffix, and screen readers announce it cleanly.

## URI scheme

`tunqio://<command>[?query]`, handled by `CommandRouter` in `Tunqio.App` (E7-S1). The 1.0 commands:

| URI | Effect |
|-----|--------|
| `tunqio://play?path=<url-encoded path>` | Replace the queue with the path (file or folder) and play |
| `tunqio://queue?path=<url-encoded path>` | Append to the queue |
| `tunqio://toggle` | Play / pause |
| `tunqio://next`, `tunqio://previous` | Transport |
| `tunqio://show` | Bring the main window to the foreground |

Unknown commands are logged and ignored. The scheme is declared in the manifest only; there are no registry writes (ADR-006).

## Data folder and MSIX virtualisation

The logical data root is `%LocalAppData%\Tunqio\`. A packaged full-trust app that writes there is, by default, redirected by MSIX file-system virtualisation into `%LocalAppData%\Packages\<PackageFamilyName>\LocalCache\Local\Tunqio\`, and that tree is **deleted on uninstall**. That would destroy the playlist exports that [library-and-data.md](library-and-data.md) relies on for durability.

Decision for 1.0: declare `<desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>` in the manifest so the path is literal, survives uninstall and is the same in packaged and unpackaged (F5) runs. MakeAppx requires the restricted capability `unvirtualizedResources` alongside it (declared in E0-S1); sideloading accepts it, the Store needs approval for it, so a Store build (1.1) may have to keep virtualisation on. E0-S6 (settings store) codes against `IAppPaths.DataRoot`; E7-S1 verifies the literal path from an installed package. If virtualisation has to stay on, the path stays logically the same and only `IAppPaths` changes.

## Assets

| Asset | Path in `Tunqio.App` |
|-------|----------------------|
| Source (Phil's logo, 512 x 512 viewBox, transparent) | `assets/brand/tunqio-icon.svg` at the repository root, the one file every asset below is rendered from |
| App icon (multi-size `.ico`) | `Assets/Tunqio.ico`: PNG entries at 16, 20, 24, 32, 40, 48, 64 and 256 px. Tunqio.exe's `ApplicationIcon` and the window icon (`Shell/AppIcon.cs`, title bar, taskbar, Alt+Tab) |
| MSIX tiles | `Assets/Square44x44Logo.png`, `Assets/Square150x150Logo.png`, `Assets/Wide310x150Logo.png`, `Assets/StoreLogo.png`, `Assets/SplashScreen.png` (standard names, each as `scale-100`, `125`, `150`, `200` and `400`; Square44x44Logo also as `targetsize-16`, `24`, `32`, `48` and `256`, plain and `_altform-unplated`). The wide tile and the splash screen are the icon centred on transparency |
| File association logo | `Assets/FileAssociation.png` (256 px, plus `targetsize-16`, `24`, `32`, `48` and `256`), the manifest's `tunqio-audio` `uap:Logo` |
| Tray icon | `Assets/Tray/tunqio-16-light.ico`, `tunqio-16-dark.ico`, `tunqio-32-light.ico`, `tunqio-32-dark.ico`, named for the taskbar they sit on (`Tray/TrayIconFiles.cs` also accepts an unqualified `tunqio-16.ico` or `tunqio-32.ico`, which are not generated) |
| About page logo | `Assets/TunqioLogo.png` (256 px, shown at 64 epx) |

**Generator (T-191).** Every file above is rendered, never hand-exported, by `tools/IconGen` from the source, at each pixel size
directly rather than downscaled; the list of files and sizes is `assets/brand/icon-assets.json`. After changing the SVG or the list:

```
dotnet run -c Release -p:Platform=x64 --project tools/IconGen
dotnet run -c Release -p:Platform=x64 --project tools/IconGen -- --check
dotnet run -c Release -p:Platform=x64 --project tools/IconGen -- --previews artifacts/icon-previews
```

The first writes the assets (a second run changes no byte), `--check` fails unless the committed files are what a fresh render
gives, and `--previews` also writes the small sizes over light and dark taskbar colours, enlarged 8x. `Tunqio.App.Tests`
(`IconAssetsTests`) parses every file at its declared size, and `tools/check-package.ps1` asserts each one in the unpackaged
output and in the MSIX.

## What each story takes from here

| Story | Uses |
|-------|------|
| E0-S1 scaffold | solution, project and namespace names; `AssemblyName`; manifest identity, display name, Application Id; window title idle string |
| E0-S6 logging and settings | data folder, log file name, `IAppPaths` |
| E7-S1 manifest and protocol | execution alias, URI scheme and commands, file type association, virtualisation setting |
| E7-S3 tray | tooltip formats, tray icon assets |
| E8-S1 release | artefact names, `.appinstaller` name, publisher replacement and freeze |
