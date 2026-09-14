# Scratch profiles for the UIA harnesses (T-194, T-197). Dot-source it AFTER uia-geometry.ps1 (it uses
# Close-TunqioShell):
#
#   . (Join-Path $here 'uia-geometry.ps1')
#   . (Join-Path $here 'scratch-profile.ps1')
#   $scratch = New-TunqioScratchProfile -Name 'check-something'
#   try { $process = Start-TunqioOnScratch $Exe $scratch; ... }
#   finally { ...close the shells this run launched...; Remove-TunqioScratchProfile $scratch -Keep:$KeepScratch }
#
# A harness never runs the app on the real %LOCALAPPDATA%\Tunqio. Every launch passes --data-root, which moves
# settings.json, library.db, logs, art, presets and exports\playlists under that folder (AppPaths), so a crash or a
# kill mid-run can only ever leave a scratch folder behind. The folder is artifacts\<name>\<stamp> in the repo, not
# %TEMP%: %TEMP% is inside %LOCALAPPDATA%, which a packaged app (the Claude desktop app) redirects into its
# LocalCache (T-183). A data root inside the real profile, or inside any package's LocalCache, is refused before
# anything is created.
#
# The helpers write their progress to the host, never to the pipeline, so a return value is only the value.
# ASCII only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

# The real profile the harnesses must never open.
function Get-TunqioRealProfile {
    return [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Tunqio'))
}

# The full path of $Path, or a throw when it is the real profile, inside it, or inside a package's redirected
# LocalCache copy of %LOCALAPPDATA%.
function Assert-TunqioScratchDataRoot([string]$Path) {
    if (-not $Path) { throw 'no scratch data root was given' }
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $real = (Get-TunqioRealProfile).TrimEnd('\')
    $inReal = $full.Equals($real, [System.StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($real + '\', [System.StringComparison]::OrdinalIgnoreCase)
    $redirected = ($full + '\') -like '*\Packages\*\LocalCache\*'
    if ($inReal -or $redirected) {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile $real. Nothing has been created or launched."
    }
    return $full
}

# Creates artifacts\<Name>\<stamp>\data under the repo and writes $Settings into its settings.json (UTF-8, no BOM).
# The default settles the first-run welcome (FirstRunWelcomeViewModel.ShouldShow: ui.welcomeShown present, either
# value), so the harness sees only the dialogs it opens itself. Pass $null to write no settings.json at all.
function New-TunqioScratchProfile {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][string]$Settings = '{ "ui.welcomeShown": false }'
    )
    $tools = $PSScriptRoot
    if (-not $tools) { $tools = Split-Path -Parent $MyInvocation.MyCommand.ScriptBlock.File }
    $repo = [System.IO.Path]::GetFullPath((Join-Path $tools '..'))
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $root = [System.IO.Path]::GetFullPath((Join-Path $repo ('artifacts\' + $Name + '\' + $stamp)))
    # Refused before anything exists on disk.
    $dataRoot = Assert-TunqioScratchDataRoot (Join-Path $root 'data')
    $null = Assert-TunqioScratchDataRoot $root
    if (Test-Path -LiteralPath $root) { throw "the scratch folder $root already exists; another run of $Name started this second. Run again." }
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $settingsPath = Join-Path $dataRoot 'settings.json'
    if ($null -ne $Settings) {
        [System.IO.File]::WriteAllText($settingsPath, $Settings, (New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Host "scratch:  $root"
    Write-Host "data:     $dataRoot"
    return [pscustomobject]@{
        Name             = $Name
        Repo             = $repo
        Root             = $root
        DataRoot         = $dataRoot
        Music            = (Join-Path $root 'music')
        SettingsPath     = $settingsPath
        DatabasePath     = (Join-Path $dataRoot 'library.db')
        LogsDirectory    = (Join-Path $dataRoot 'logs')
        ExportsDirectory = (Join-Path $dataRoot 'exports\playlists')
    }
}

# Today's log file on the scratch profile (AppPaths.LogFileName).
function Get-TunqioScratchLog($Profile) {
    return (Join-Path $Profile.LogsDirectory ('tunqio-' + (Get-Date -Format 'yyyyMMdd') + '.log'))
}

# Launches the shell on the scratch profile, with any further arguments after --data-root. The handle is opened at
# once, so Windows PowerShell 5.1 can read the exit code back later (Close-TunqioShell, T-188).
function Start-TunqioOnScratch([string]$Exe, $Profile, [string[]]$Arguments = @()) {
    $dataRoot = Assert-TunqioScratchDataRoot $Profile.DataRoot
    $argumentList = @('--data-root', ('"' + $dataRoot + '"')) + @($Arguments | Where-Object { $_ })
    $process = Start-Process $Exe -ArgumentList $argumentList -PassThru
    try { $null = $process.Handle } catch { }
    return $process
}

# Copies fixture albums (tests\fixtures\library, read only) into the profile's music folder: the ones named, or every
# album when none is. Returns the number of files copied.
function Copy-TunqioFixtureAlbums($Profile, [string[]]$Albums = @()) {
    $fixtures = Join-Path $Profile.Repo 'tests\fixtures\library'
    if (-not (Test-Path -LiteralPath $fixtures)) { throw "$fixtures not found." }
    New-Item -ItemType Directory -Force -Path $Profile.Music | Out-Null
    $folders = @(Get-ChildItem -LiteralPath $fixtures -Directory)
    if ($Albums.Count -gt 0) {
        $folders = @($folders | Where-Object { $Albums -contains $_.Name })
        if ($folders.Count -ne $Albums.Count) { throw "the fixture library has $($folders.Count) of the $($Albums.Count) albums asked for: $($Albums -join ', ')" }
    }
    foreach ($folder in $folders) { Copy-Item -LiteralPath $folder.FullName -Destination $Profile.Music -Recurse }
    return @(Get-ChildItem -LiteralPath $Profile.Music -Recurse -File).Count
}

# Generates $AlbumCount albums of $TracksPerAlbum quiet, tagged FLAC tones, $Seconds long each, in the profile's music
# folder. For harnesses that play: the fixture tracks are one second long, so a queue of them is over before a check
# has looked. Each encoder gets a bounded wait (T-174). Returns the album names, as "Album <album> by <artist>".
function New-TunqioScratchTones($Profile, [int]$AlbumCount = 2, [int]$TracksPerAlbum = 4, [int]$Seconds = 120, [string]$Ffmpeg) {
    if ($AlbumCount -lt 1 -or $AlbumCount -gt 4 -or $TracksPerAlbum -lt 1 -or $TracksPerAlbum -gt 8) { throw 'New-TunqioScratchTones makes 1..4 albums of 1..8 tracks' }
    if (-not $Ffmpeg) {
        $fetched = Join-Path $Profile.Repo 'artifacts\ffmpeg\bin\ffmpeg.exe'
        if (Test-Path -LiteralPath $fetched) { $Ffmpeg = $fetched }
        elseif (Get-Command ffmpeg -ErrorAction SilentlyContinue) { $Ffmpeg = (Get-Command ffmpeg).Source }
        else { throw 'ffmpeg was not found: run tools/fetch-ffmpeg.ps1 or put ffmpeg on PATH.' }
    }
    $words = @('One', 'Two', 'Three', 'Four', 'Five', 'Six', 'Seven', 'Eight')
    $names = @()
    for ($a = 0; $a -lt $AlbumCount; $a++) {
        $artist = 'Scratch Band ' + $words[$a]
        $album = 'Scratch Tones ' + $words[$a]
        $folder = Join-Path $Profile.Music ("{0} - {1} (20{2:00})" -f $artist, $album, (10 + $a))
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        for ($t = 0; $t -lt $TracksPerAlbum; $t++) {
            $title = 'Tone ' + $words[$a] + ' ' + ($t + 1)
            $out = Join-Path $folder ("{0:00} - {1}.flac" -f ($t + 1), $title)
            $argumentLine = ('-nostdin -hide_banner -loglevel error -y -f lavfi -i "sine=frequency={0}:sample_rate=44100:duration={1}" -af volume=0.02 ' +
                '-c:a flac -ac 2 -metadata "title={2}" -metadata "artist={3}" -metadata "album_artist={3}" -metadata "album={4}" -metadata "track={5}" -metadata "date=20{6:00}" "{7}"') -f
                (220 + 55 * $t + 20 * $a), $Seconds, $title, $artist, $album, ($t + 1), (10 + $a), $out
            $encoder = Start-Process -FilePath $Ffmpeg -ArgumentList $argumentLine -NoNewWindow -PassThru
            $null = $encoder.Handle
            if (-not $encoder.WaitForExit(60000)) { try { $encoder.Kill() } catch { }; throw "ffmpeg did not finish $out within 60 s" }
            if ($encoder.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $out)) { throw "ffmpeg could not write $out (exit $($encoder.ExitCode))" }
        }
        $names += "Album $album by $artist"
    }
    Write-Host "  note  generated $($AlbumCount * $TracksPerAlbum) tones of $Seconds s in $($Profile.Music)"
    return $names
}

# ---- sqlite, through the app's own native library ---------------------------------------------------------------------
function Add-TunqioScratchSqlite([string]$Exe) {
    if ('TunqioScratchSqlite' -as [type]) { return }
    Add-Type -TypeDefinition (@"
using System;
using System.Runtime.InteropServices;
public static class TunqioScratchSqlite {
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_errmsg(IntPtr db);
}
"@ -replace 'SQLITEDLL', (Join-Path (Split-Path $Exe) 'e_sqlite3.dll'))
}

# Runs $Sql against a database on a scratch profile. Refuses any path outside one.
function Invoke-TunqioScratchSql([string]$Path, [string]$Sql) {
    $null = Assert-TunqioScratchDataRoot (Split-Path -Parent $Path)
    $utf8 = [System.Text.Encoding]::UTF8
    $db = [IntPtr]::Zero
    $rc = [TunqioScratchSqlite]::sqlite3_open_v2($utf8.GetBytes($Path + "`0"), [ref]$db, 2, [IntPtr]::Zero)  # SQLITE_OPEN_READWRITE
    if ($rc -ne 0) { throw "could not open $Path ($rc)" }
    try {
        $err = [IntPtr]::Zero
        $rc = [TunqioScratchSqlite]::sqlite3_exec($db, $utf8.GetBytes($Sql + "`0"), [IntPtr]::Zero, [IntPtr]::Zero, [ref]$err)
        if ($rc -ne 0) { throw ('sqlite: ' + [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi([TunqioScratchSqlite]::sqlite3_errmsg($db))) }
    }
    finally { [TunqioScratchSqlite]::sqlite3_close_v2($db) | Out-Null }
}

# The first column of the first row as a string integer, 'NULL' for SQL NULL, or 'no row'.
function Read-TunqioScratchScalar([string]$Path, [string]$Sql) {
    $null = Assert-TunqioScratchDataRoot (Split-Path -Parent $Path)
    $utf8 = [System.Text.Encoding]::UTF8
    $db = [IntPtr]::Zero
    $rc = [TunqioScratchSqlite]::sqlite3_open_v2($utf8.GetBytes($Path + "`0"), [ref]$db, 2, [IntPtr]::Zero)
    if ($rc -ne 0) { throw "could not open $Path ($rc)" }
    try {
        $stmt = [IntPtr]::Zero
        $rc = [TunqioScratchSqlite]::sqlite3_prepare_v2($db, $utf8.GetBytes($Sql + "`0"), -1, [ref]$stmt, [IntPtr]::Zero)
        if ($rc -ne 0) { throw ('sqlite: ' + [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi([TunqioScratchSqlite]::sqlite3_errmsg($db))) }
        try {
            if ([TunqioScratchSqlite]::sqlite3_step($stmt) -ne 100) { return 'no row' }  # SQLITE_ROW
            if ([TunqioScratchSqlite]::sqlite3_column_type($stmt, 0) -eq 5) { return 'NULL' }  # SQLITE_NULL
            return [string][TunqioScratchSqlite]::sqlite3_column_int64($stmt, 0)
        }
        finally { [TunqioScratchSqlite]::sqlite3_finalize($stmt) | Out-Null }
    }
    finally { [TunqioScratchSqlite]::sqlite3_close_v2($db) | Out-Null }
}

# Makes the profile's library: a boot launch creates library.db (and must CREATE it - a launch that OPENED one did not
# honour --data-root, T-183), the music folder is seeded into library_folder through the app's own e_sqlite3.dll, and,
# when $ExpectTracks is given, a second launch runs the launch scan until the track table holds that many rows. Each
# launch is closed through Close-TunqioShell; a close that fails, or anything unexpected, throws.
function Initialize-TunqioScratchLibrary([string]$Exe, $Profile, [int]$ExpectTracks = 0, [int]$ScanSeconds = 90) {
    Add-TunqioScratchSqlite $Exe
    if (-not (Test-Path -LiteralPath $Profile.Music)) { throw "the scratch profile has no music folder at $($Profile.Music); copy or generate some first" }
    if (Test-Path -LiteralPath $Profile.DatabasePath) { throw "the scratch profile already has a library.db at $($Profile.DatabasePath)" }

    $boot = Start-TunqioOnScratch $Exe $Profile
    $problem = $null
    try {
        $deadline = (Get-Date).AddSeconds(60)
        for ($i = 0; $i -lt 240 -and -not (Test-Path -LiteralPath $Profile.DatabasePath) -and (Get-Date) -lt $deadline -and -not $boot.HasExited; $i++) {
            Start-Sleep -Milliseconds 250
        }
        if (-not (Test-Path -LiteralPath $Profile.DatabasePath)) { throw "the boot launch on $($Profile.DataRoot) did not create library.db within 60 s" }
        # The schema, then a closed window, so the write-ahead log is checkpointed into the file the seed opens.
        Start-Sleep -Seconds 6
    }
    finally { $problem = Close-TunqioShell $boot $null 20 }
    if ($problem) { throw "the boot launch on the scratch profile: $problem" }
    Start-Sleep -Seconds 1

    $log = Get-TunqioScratchLog $Profile
    $text = ''
    if (Test-Path -LiteralPath $log) {
        $stream = New-Object System.IO.FileStream($log, 'Open', 'Read', 'ReadWrite')
        try { $text = (New-Object System.IO.StreamReader($stream)).ReadToEnd() }
        finally { $stream.Dispose() }
    }
    $opening = [regex]::Match($text, 'library\.db (created|opened) at schema')
    if (-not $opening.Success) { throw "the boot launch logged no 'library.db created' or 'opened' line in $log; nothing has been seeded (T-183)" }
    if ($opening.Groups[1].Value -ne 'created') { throw "the boot launch OPENED an existing library database instead of creating one in the empty scratch profile $($Profile.DataRoot); nothing has been seeded (T-183, T-194)" }

    Invoke-TunqioScratchSql $Profile.DatabasePath ("INSERT INTO library_folder(path, enabled) VALUES ('" + $Profile.Music.Replace("'", "''") + "', 1);")
    Write-Host "  note  scratch library seeded with $($Profile.Music)"

    if ($ExpectTracks -gt 0) {
        $scan = Start-TunqioOnScratch $Exe $Profile
        $count = '0'
        try {
            $deadline = (Get-Date).AddSeconds($ScanSeconds)
            for ($i = 0; $i -lt ($ScanSeconds * 2 + 4) -and (Get-Date) -lt $deadline -and -not $scan.HasExited; $i++) {
                try { $count = Read-TunqioScratchScalar $Profile.DatabasePath 'SELECT COUNT(*) FROM track' } catch { $count = '0' }
                if ($count -match '^\d+$' -and [int]$count -ge $ExpectTracks) { break }
                Start-Sleep -Milliseconds 500
            }
            # A moment for the scan to finish the rows it has inserted (tags are refined after the insert).
            Start-Sleep -Seconds 3
        }
        finally { $problem = Close-TunqioShell $scan $null 20 }
        if ($problem) { throw "the scan launch on the scratch profile: $problem" }
        if (-not ($count -match '^\d+$' -and [int]$count -ge $ExpectTracks)) { throw "the launch scan indexed $count of the $ExpectTracks tracks within $ScanSeconds s" }
        Write-Host "  note  the launch scan indexed $count tracks"
        Start-Sleep -Seconds 1
    }
}

# Deletes the stamp folder, unless -Keep. Never while a Tunqio on this data root is still running: that is a shell the
# caller failed to close, and it is reported rather than deleted from under.
function Remove-TunqioScratchProfile($Profile, [switch]$Keep) {
    if (-not $Profile) { return }
    if ($Keep) { Write-Host "scratch folder kept at $($Profile.Root)"; return }
    $still = @(Get-CimInstance Win32_Process -Filter "Name='Tunqio.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($Profile.DataRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
    if ($still.Count -gt 0) {
        Write-Host "  note  a Tunqio on the scratch profile is still running (pid $(($still | ForEach-Object { $_.ProcessId }) -join ', ')), so $($Profile.Root) is left in place"
        return
    }
    for ($i = 0; $i -lt 5 -and (Test-Path -LiteralPath $Profile.Root); $i++) {
        Remove-Item -LiteralPath $Profile.Root -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $Profile.Root) { Start-Sleep -Seconds 1 }
    }
    if (Test-Path -LiteralPath $Profile.Root) { Write-Host "  note  the scratch folder could not be deleted completely: $($Profile.Root)"; return }
    $parent = Split-Path -Parent $Profile.Root
    if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) {
        Remove-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
    }
    Write-Host "scratch folder deleted: $($Profile.Root)"
}
