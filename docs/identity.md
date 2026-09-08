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
| Publisher (dev / self-signed) | `CN=Tunqio` | manifest `Identity Publisher`; must match the signing certificate subject | replaced once, at E8-S1, then **frozen** |
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
| App icon (multi-size `.ico`) | `Assets/Tunqio.ico` |
| MSIX tiles | `Assets/Square44x44Logo.png`, `Assets/Square150x150Logo.png`, `Assets/Wide310x150Logo.png`, `Assets/StoreLogo.png`, `Assets/SplashScreen.png` (standard names, scale-qualified) |
| File association logo | `Assets/FileAssociation.png` |
| Tray icon | `Assets/Tray/tunqio-16.ico`, `Assets/Tray/tunqio-32.ico` (light and dark variants) |

Artwork is a separate task; until it exists the WinUI template placeholders ship with the name applied.

## What each story takes from here

| Story | Uses |
|-------|------|
| E0-S1 scaffold | solution, project and namespace names; `AssemblyName`; manifest identity, display name, Application Id; window title idle string |
| E0-S6 logging and settings | data folder, log file name, `IAppPaths` |
| E7-S1 manifest and protocol | execution alias, URI scheme and commands, file type association, virtualisation setting |
| E7-S3 tray | tooltip formats, tray icon assets |
| E8-S1 release | artefact names, `.appinstaller` name, publisher replacement and freeze |
