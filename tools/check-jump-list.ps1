<#
.SYNOPSIS
  E7-S5 (T-78; AC-506, AC-508, the unpackaged half of AC-507): the jump list's pin action and its items' launch arguments,
  proven on the unpackaged app. The taskbar list itself needs the installed package (Q-119), so it is not looked at here;
  this checks what the list would launch, and that the app skips the list with one log line and carries on.

  Against a scratch --data-root, one step after another:
    1. generates a three-track album with ffmpeg and launches Tunqio once to create library.db, then closes it;
    2. seeds the album's folder as a library folder, launches again until the launch scan has indexed the three tracks,
       and closes it; then seeds a playlist "Jump Mix" holding the third track then the first;
    3. launches Tunqio with the playlist item's own arguments (tunqio://playlist?id=N): a cold start plays the playlist,
       and the window title names its first track;
    4. opens the playlist's page (Library > Playlists) and toggles Pin to jump list: playlist.pinned in library.db goes
       to 1, back to 0, and to 1 again, and the toggle reports each state;
    5. starts a second process with a track item's arguments (tunqio://track?id=N): it exits with 0, the running window
       plays that track; then one with the playlist item's arguments: the window plays the playlist again;
    6. starts second processes with a track id and a playlist id that do not exist: each exits with 0, nothing changes,
       and the log has exactly one refusal line for each;
  then closes the window (Close-TunqioShell, which fails the run on a hang or a crash code) and reads the log: the jump list
  skip line once per launch, no jump list error, the plays, the refusals, and "Shutdown: jump list".

  WHAT IT CHANGES. Nothing outside artifacts\check-jump-list\<stamp>, deleted at the end unless -Keep. The real profile is
  never opened, nothing is installed or registered, and no jump list is written (the unpackaged app never calls the API).
  It refuses to start while any Tunqio is running (checking again once a minute for at most -WaitMinutes), and it only
  ever closes the processes it launched, by their ids. One process at a time.

  ASCII only, Windows PowerShell 5.1, safe under -File. UIA only; no keystrokes and no pointer.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output beside this script's repository.
.PARAMETER Ffmpeg
  ffmpeg.exe for generating the audio. Defaults to artifacts\ffmpeg\bin (tools/fetch-ffmpeg.ps1), then PATH.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else opened to go away, checking once a minute, before refusing. At most 10.
.PARAMETER Keep
  Keep the scratch folder for inspection.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Ffmpeg,
    [int]$WaitMinutes = 10,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'uia-geometry.ps1')
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if ($WaitMinutes -gt 10) { $WaitMinutes = 10 }
if ($WaitMinutes -lt 0) { $WaitMinutes = 0 }

# ---- refuse while anybody's Tunqio is open: once a minute, at most -WaitMinutes times (T-174: every wait has an end) ----
for ($attempt = 0; @(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0; $attempt++) {
    if ($attempt -ge $WaitMinutes) {
        Write-Output "check-jump-list: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (attempt $($attempt + 1) of $WaitMinutes)"
    Start-Sleep -Seconds 60
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-jump-list\$stamp"))
$root = Join-Path $scratch 'root'
$music = Join-Path $scratch 'music'
$dbPath = Join-Path $root 'library.db'
$artist = 'Jump Artist'
$titles = @('Jump One', 'Jump Two', 'Jump Three')
$playlistName = 'Jump Mix'
$missingId = 999999
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'

if (-not $Ffmpeg) {
    $fetched = Join-Path $here '..\artifacts\ffmpeg\bin\ffmpeg.exe'
    if (Test-Path $fetched) { $Ffmpeg = [System.IO.Path]::GetFullPath($fetched) }
    elseif (Get-Command ffmpeg -ErrorAction SilentlyContinue) { $Ffmpeg = (Get-Command ffmpeg).Source }
    else { throw 'ffmpeg was not found: run tools/fetch-ffmpeg.ps1, put ffmpeg on PATH, or pass -Ffmpeg.' }
}

# ---- sqlite, through the app's own native library ---------------------------------------------------------------------
if (-not ('TunqioJumpListSqlite' -as [type])) {
    Add-Type -TypeDefinition (@"
using System;
using System.Runtime.InteropServices;
public static class TunqioJumpListSqlite {
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

function Open-Sqlite([string]$path) {
    $db = [IntPtr]::Zero
    $rc = [TunqioJumpListSqlite]::sqlite3_open_v2([System.Text.Encoding]::UTF8.GetBytes($path + "`0"), [ref]$db, 2, [IntPtr]::Zero)  # SQLITE_OPEN_READWRITE
    if ($rc -ne 0) { throw "could not open $path ($rc)" }
    return $db
}
function Invoke-Sql([string]$path, [string]$sql) {
    $db = Open-Sqlite $path
    try {
        $err = [IntPtr]::Zero
        $rc = [TunqioJumpListSqlite]::sqlite3_exec($db, [System.Text.Encoding]::UTF8.GetBytes($sql + "`0"), [IntPtr]::Zero, [IntPtr]::Zero, [ref]$err)
        if ($rc -ne 0) { throw ('sqlite: ' + [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi([TunqioJumpListSqlite]::sqlite3_errmsg($db))) }
    }
    finally { [TunqioJumpListSqlite]::sqlite3_close_v2($db) | Out-Null }
}
# The first column of the first row as a string integer, 'NULL' for SQL NULL, or 'no row'.
function Read-SqlScalar([string]$path, [string]$sql) {
    $db = Open-Sqlite $path
    try {
        $stmt = [IntPtr]::Zero
        $rc = [TunqioJumpListSqlite]::sqlite3_prepare_v2($db, [System.Text.Encoding]::UTF8.GetBytes($sql + "`0"), -1, [ref]$stmt, [IntPtr]::Zero)
        if ($rc -ne 0) { throw ('sqlite: ' + [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi([TunqioJumpListSqlite]::sqlite3_errmsg($db))) }
        try {
            if ([TunqioJumpListSqlite]::sqlite3_step($stmt) -ne 100) { return 'no row' }  # SQLITE_ROW
            if ([TunqioJumpListSqlite]::sqlite3_column_type($stmt, 0) -eq 5) { return 'NULL' }  # SQLITE_NULL
            return [string][TunqioJumpListSqlite]::sqlite3_column_int64($stmt, 0)
        }
        finally { [TunqioJumpListSqlite]::sqlite3_finalize($stmt) | Out-Null }
    }
    finally { [TunqioJumpListSqlite]::sqlite3_close_v2($db) | Out-Null }
}
function Sql-Text([string]$text) { "'" + $text.Replace("'", "''") + "'" }

# One tagged FLAC tone, quiet. Start-Process with a bounded wait, so a stuck encoder cannot hold the run.
function New-Tone([string]$out, [int]$hz, [int]$seconds, [string]$title, [int]$trackNo) {
    $argumentLine = ('-nostdin -hide_banner -loglevel error -y -f lavfi -i "sine=frequency={0}:sample_rate=44100:duration={1}" -af volume=0.2 ' +
        '-c:a flac -ac 2 -metadata "title={2}" -metadata "artist={3}" -metadata "album=Jump List" -metadata "track={4}" "{5}"') -f $hz, $seconds, $title, $artist, $trackNo, $out
    $encoder = Start-Process -FilePath $Ffmpeg -ArgumentList $argumentLine -NoNewWindow -PassThru
    $null = $encoder.Handle
    if (-not $encoder.WaitForExit(60000)) { $encoder.Kill(); throw "ffmpeg did not finish $out within 60 s" }
    if ($encoder.ExitCode -ne 0 -or -not (Test-Path $out)) { throw "ffmpeg could not write $out (exit $($encoder.ExitCode))" }
}

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "waited ${seconds}s and $what never happened"
}
function Find-Named($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)))
}
function Find-ById($scope, [string]$id) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Find-ListItem($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)),
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))))
}
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}
function Start-Shell([string]$argumentLine) {
    $p = Start-Process $Exe -ArgumentList $argumentLine -PassThru
    $null = $p.Handle   # Windows PowerShell 5.1: ExitCode reads back empty unless the handle was opened while it was alive.
    return $p
}
function Quote([string]$text) { '"' + $text + '"' }
function Get-WindowOf([int]$processId) {
    $A::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId)))
}
function Get-TunqioOn([string]$dataRoot) {
    @(Get-CimInstance Win32_Process -Filter "Name='Tunqio.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($dataRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}
# The window title is "Title - Artist - Tunqio" with a track loaded (Identity.WindowTitle); it names what is playing and
# nothing else, unlike a search of the tree, which also finds the playlist page's rows.
function Wait-Title([object]$window, [string]$title, [int]$seconds) {
    $found = $null
    try { $found = Wait-Until { if ($window.Current.Name -like "$title*") { $window.Current.Name } } $seconds "the window title named '$title'" } catch { }
    return $found
}
function Close-Welcome($window) {
    $welcome = $null
    try { $welcome = Wait-Until { Find-Named $window 'Welcome to Tunqio' } 15 'the welcome dialog appeared' } catch { }
    if ($welcome) {
        Invoke-Element (Wait-Until { Find-Named $welcome 'Skip all' } 5 'the welcome offered Skip all')
        Wait-Until { -not (Find-Named $window 'Welcome to Tunqio') } 10 'the welcome closed' | Out-Null
    }
}

# A second process on the root: it must hand over its activation and exit, cleanly, within $seconds.
function Invoke-Second([string]$label, [string]$arguments, [int]$seconds = 15) {
    $second = Start-Shell ("--data-root {0} {1}" -f (Quote $root), $arguments)
    $script:launched += $second
    $exited = $second.WaitForExit($seconds * 1000)
    Check "$label - the second process exits" $exited "pid $($second.Id), $(if ($exited) { 'exited' } else { "still running after $seconds s" })"
    if ($exited) {
        Check "$label - it exits with code 0" ($second.ExitCode -eq 0) ("exit code 0x{0:X8}" -f $second.ExitCode)
    }
    else {
        $problem = Close-TunqioShell $second $null 10
        if ($problem) { $script:failures += $problem }
    }
    $onRoot = @(Get-TunqioOn $root)
    Check "$label - the root still has exactly one Tunqio process" ($onRoot.Count -eq 1 -and $onRoot[0].ProcessId -eq $script:main.Id) "$($onRoot.Count) process(es)"
}

# A launch that is closed again once $ready says it has done its job.
function Invoke-Setup([string]$label, [scriptblock]$ready, [int]$seconds) {
    $p = Start-Shell ("--data-root {0}" -f (Quote $root))
    $script:launched += $p
    try {
        Wait-Until $ready $seconds $label | Out-Null
    }
    finally {
        $problem = Close-TunqioShell $p (Get-WindowOf $p.Id) 20
        if ($problem) { $script:failures += "$label launch: $problem" }
    }
}

$script:launched = @()
$script:main = $null
$launches = 0
try {
    # ---- the audio and the scratch root -----------------------------------------------------------------------------
    New-Item -ItemType Directory -Force -Path $root, $music | Out-Null
    $full = [System.IO.Path]::GetFullPath($root)
    if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    for ($i = 0; $i -lt 3; $i++) { New-Tone (Join-Path $music ('{0:00} - {1}.flac' -f ($i + 1), $titles[$i])) (330 + 110 * $i) 60 $titles[$i] ($i + 1) }
    Write-Output "  note  generated three 60 s tracks with $Ffmpeg"
    Write-Output "shell: $Exe"
    Write-Output "scratch root: $root"

    # ---- 1. create the database ---------------------------------------------------------------------------------------
    $launches++
    Invoke-Setup 'the first launch created library.db' { (Test-Path $dbPath) -and ((Get-ChildItem (Join-Path $root 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Select-String -Pattern 'library\.db created at schema' -Quiet)) } 60
    Start-Sleep -Seconds 1

    # ---- 2. index the album, then seed the playlist -------------------------------------------------------------------
    Invoke-Sql $dbPath ("INSERT INTO library_folder(path, enabled) VALUES (" + (Sql-Text ($music + '\')) + ", 1);")
    $launches++
    Invoke-Setup 'the launch scan indexed the three tracks' { (Read-SqlScalar $dbPath 'SELECT COUNT(*) FROM track') -eq '3' } 90
    Start-Sleep -Seconds 1
    $ids = @{}
    foreach ($t in $titles) {
        $ids[$t] = Read-SqlScalar $dbPath ("SELECT id FROM track WHERE title = " + (Sql-Text $t))
        if ($ids[$t] -notmatch '^\d+$') { throw "the scan did not index '$t' by title ($($ids[$t]))" }
    }
    Invoke-Sql $dbPath ("INSERT INTO playlist(name, created_at, modified_at) VALUES (" + (Sql-Text $playlistName) + ", 0, 0);")
    $playlistId = Read-SqlScalar $dbPath ("SELECT id FROM playlist WHERE name = " + (Sql-Text $playlistName))
    Invoke-Sql $dbPath ("INSERT INTO playlist_item(playlist_id, position, track_id) VALUES ($playlistId, 0, $($ids['Jump Three'])), ($playlistId, 1, $($ids['Jump One']));")
    Write-Output "  note  tracks $($titles[0]) $($ids['Jump One']), $($titles[1]) $($ids['Jump Two']), $($titles[2]) $($ids['Jump Three']); playlist '$playlistName' $playlistId"
    $trackItem = 'tunqio://track?id=' + $ids['Jump Two']
    $playlistItem = 'tunqio://playlist?id=' + $playlistId

    # ---- 3. a cold start with a playlist item's arguments -------------------------------------------------------------
    $launches++
    $script:main = Start-Shell ("--data-root {0} {1}" -f (Quote $root), (Quote $playlistItem))
    $script:launched += $script:main
    $window = Wait-Until { Get-WindowOf $script:main.Id } 30 'the shell window appeared'
    Close-Welcome $window
    $mute = Wait-Until { Find-ById $window 'MuteButton' } 20 'the Mute button appeared'
    $toggle = $mute.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    $cold = Wait-Title $window 'Jump Three' 30
    Check 'A cold start with a playlist item''s arguments plays the playlist from its first track' ($null -ne $cold) "$(if ($cold) { "title '$cold'" } else { "title '$($window.Current.Name)'" })"

    # ---- 4. Pin to jump list sets and clears playlist.pinned ----------------------------------------------------------
    Select-Element (Wait-Until { Find-ListItem $window 'Playlists' } 15 'the sidebar showed Playlists')
    Wait-Until { Find-Named $window 'New playlist' } 10 'Library > Playlists opened' | Out-Null
    Invoke-Element (Wait-Until { Find-ListItem $window $playlistName } 10 "Library > Playlists listed '$playlistName'")
    $pin = Wait-Until { Find-ById $window 'PinToJumpList' } 10 'the playlist page offered Pin to jump list'
    Start-Sleep -Milliseconds 800
    Check 'The toggle is named for Narrator' ($pin.Current.Name -eq 'Pin to jump list') "name '$($pin.Current.Name)'"
    $pinToggle = $pin.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Check 'An unpinned playlist shows the toggle off' ($pinToggle.Current.ToggleState -eq 'Off' -and (Read-SqlScalar $dbPath "SELECT pinned FROM playlist WHERE id = $playlistId") -eq '0') "toggle $($pinToggle.Current.ToggleState)"
    foreach ($want in @('1', '0', '1')) {
        $pinToggle.Toggle()
        $stored = $null
        try { $stored = Wait-Until { $v = Read-SqlScalar $dbPath "SELECT pinned FROM playlist WHERE id = $playlistId"; if ($v -eq $want) { $v } } 10 "playlist.pinned became $want" } catch { }
        $state = $pinToggle.Current.ToggleState
        Check "Pin to jump list stores pinned = $want" ($stored -eq $want -and (($want -eq '1' -and $state -eq 'On') -or ($want -eq '0' -and $state -eq 'Off'))) "library.db $(Read-SqlScalar $dbPath "SELECT pinned FROM playlist WHERE id = $playlistId"), toggle $state"
    }
    # Away from the page, so nothing on screen but Now Playing and the title names a track.
    Select-Element (Wait-Until { Find-ListItem $window 'Albums' } 10 'the sidebar showed Albums')

    # ---- 5. a track item, then a playlist item, from second processes -------------------------------------------------
    Invoke-Second 'a track item' (Quote $trackItem)
    $two = Wait-Title $window 'Jump Two' 20
    Check 'The running window plays the track item''s track' ($null -ne $two) "$(if ($two) { "title '$two'" } else { "title '$($window.Current.Name)'" })"
    Invoke-Second 'a playlist item' (Quote $playlistItem)
    $again = Wait-Title $window 'Jump Three' 20
    Check 'The running window plays the playlist item''s playlist' ($null -ne $again) "$(if ($again) { "title '$again'" } else { "title '$($window.Current.Name)'" })"

    # ---- 6. items whose track or playlist has gone -------------------------------------------------------------------
    Invoke-Second 'a missing track' (Quote "tunqio://track?id=$missingId")
    Invoke-Second 'a missing playlist' (Quote "tunqio://playlist?id=$missingId")
    Start-Sleep -Seconds 3
    Check 'Neither missing item changed what plays' ($window.Current.Name -like 'Jump Three*') "title '$($window.Current.Name)'"

    $problem = Close-TunqioShell $script:main $window 20
    if ($problem) { $script:failures += $problem }

    # ---- the log ------------------------------------------------------------------------------------------------------
    $log = @()
    foreach ($file in @(Get-ChildItem (Join-Path $root 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) { $log += @(Get-Content $file.FullName) }
    $skipped = @($log | Where-Object { $_ -match 'Jump list: skipped, this process has no package identity' })
    $jumpErrors = @($log | Where-Object { $_ -match 'Jump list unavailable|Jump list: the refresh' })
    $fatal = @($log | Where-Object { $_ -match 'Unhandled exception' })
    $playlistPlays = @($log | Where-Object { $_ -match "Activation: playing playlist $playlistId " })
    $trackPlays = @($log | Where-Object { $_ -match "Activation: playing track $($ids['Jump Two']) " })
    $missingTrack = @($log | Where-Object { $_ -match "refused: track $missingId is not in the library" })
    $missingPlaylist = @($log | Where-Object { $_ -match "refused: playlist $missingId does not exist" })
    $anyRefusal = @($log | Where-Object { $_ -match 'Activation input refused|Activation command .* refused' })
    $redirected = @($log | Where-Object { $_ -match 'Single instance: received a redirected' })
    $shutdown = @($log | Where-Object { $_ -match 'Shutdown: jump list' })
    Check 'Every launch skipped the jump list with one line: unpackaged, no package identity' ($skipped.Count -eq $launches) "$($skipped.Count) skip line(s) for $launches launch(es)$(if ($skipped.Count) { ': ' + $skipped[0].Substring($skipped[0].IndexOf('Jump list')) })"
    Check 'No jump list error and no unhandled exception' ($jumpErrors.Count -eq 0 -and $fatal.Count -eq 0) "$($jumpErrors.Count) jump list error(s), $($fatal.Count) unhandled"
    Check 'The playlist item played twice: cold start and redirect' ($playlistPlays.Count -eq 2) "$($playlistPlays.Count) line(s)"
    Check 'The track item played once' ($trackPlays.Count -eq 1) "$($trackPlays.Count) line(s)"
    Check 'The running instance received the four second launches' ($redirected.Count -eq 4) "$($redirected.Count) line(s)"
    Check 'A missing track is refused with exactly one line' ($missingTrack.Count -eq 1) "$($missingTrack.Count) line(s)$(if ($missingTrack.Count) { ': ' + $missingTrack[0].Substring($missingTrack[0].IndexOf('Activation')) })"
    Check 'A missing playlist is refused with exactly one line' ($missingPlaylist.Count -eq 1) "$($missingPlaylist.Count) line(s)$(if ($missingPlaylist.Count) { ': ' + $missingPlaylist[0].Substring($missingPlaylist[0].IndexOf('Activation')) })"
    Check 'Nothing else was refused' ($anyRefusal.Count -eq 2) "$($anyRefusal.Count) refusal line(s) in all"
    Check 'Shutdown stopped the jump list step' ($shutdown.Count -ge 1) "$($shutdown.Count) line(s)"
}
catch {
    $script:failures += "script error: $($_.Exception.Message)"
    Write-Output "  FAIL  script error: $($_.Exception.Message)"
}
finally {
    foreach ($p in $script:launched) {
        if ($p -and -not $p.HasExited) {
            $problem = Close-TunqioShell $p $null 20
            if ($problem) { $script:failures += $problem }
        }
    }
    if (-not $Keep) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
    else { Write-Output "  note  scratch kept at $scratch" }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -gt 0) {
    Write-Output "check-jump-list: FAIL ($($script:failures.Count) check(s)): $($script:failures -join '; ')"
    exit 1
}
Write-Output 'check-jump-list: PASS'
exit 0
