<#
.SYNOPSIS
  E6-S7 (AC-449, AC-451, AC-453): the star control in a running shell, UIA only. Rates a fixture track from Now
  Playing and from its Tracks row through the RangeValue pattern, checks each place follows the other without a
  rescan, checks the Tracks header sorts by rating, and after the app exits reads the rating back out of the scratch
  library.db and the app's log. With write-to-file at its default (off) the fixture file is proved byte-identical.

  No keystrokes and no pointer: the stars are set with RangeValuePattern.SetValue, navigation is SelectionItem and
  Invoke. Ctrl+Alt+digit is ShellShortcutsTests' to hold.

  WHAT IT TOUCHES, AND WHERE. A rating is a library write, so this script never goes near a real library. Like
  tools/check-tag-editor.ps1 it copies one fixture album (tests/fixtures/library) into a scratch folder, moves the
  real library database aside for the run and puts it back in the finally, boots the shell once to create an empty
  database (and refuses if that boot OPENED one - T-183), seeds the scratch folder into it through the app's own
  e_sqlite3.dll, and only then drives the shell. The output is muted through the Mute button for the run, because
  playing the album is how Now Playing gets a track. If this script is killed between parking and restoring, the
  database is in the run folder named at the top of the output; a later run finds it there and puts it back first.

  ASCII only, Windows PowerShell 5.1, safe under -File.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER Seconds
  How long to give the window before reading the tree.
.PARAMETER KeepScratch
  Leave the scratch copy of the fixtures behind.
#>
[CmdletBinding()]
param(
    [switch]$SkipFreshnessCheck,
    [string]$Exe,
    [int]$Seconds = 12,
    [switch]$KeepScratch
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $here 'assert-fresh-build.ps1')
if (-not $SkipFreshnessCheck) { Assert-FreshBuild -AppDir (Split-Path $Exe) }
. (Join-Path $here 'uia-geometry.ps1')

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$repo = (Resolve-Path (Join-Path $here '..')).Path
$fixtures = Join-Path $repo 'tests\fixtures\library'
$albumFolder = 'Night Signal - Aurora Lines (2019)'
$trackFile = '01 - First Light.flac'
$rowPrefix = 'First Light by Night Signal, Aurora Lines'
$tileName = 'Album Aurora Lines by Night Signal, 2019'
$dataRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'
$dbPath = Join-Path $dataRoot 'library.db'
$logDir = Join-Path $dataRoot 'logs'
$runRoot = Join-Path $env:TEMP 'tunqio-check-ratings'
$parked = Join-Path $runRoot 'parked-library-database'
$music = Join-Path $runRoot 'music'
$script:failures = @()

# ---- sqlite, through the app's own native library: one exec to seed, one scalar read to prove the store -------------
if (-not ('TunqioRatingsSqlite' -as [type])) {
    Add-Type -TypeDefinition (@"
using System;
using System.Runtime.InteropServices;
public static class TunqioRatingsSqlite {
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
    $rc = [TunqioRatingsSqlite]::sqlite3_open_v2([System.Text.Encoding]::UTF8.GetBytes($path + "`0"), [ref]$db, 2, [IntPtr]::Zero)  # SQLITE_OPEN_READWRITE
    if ($rc -ne 0) { throw "could not open $path ($rc)" }
    return $db
}

function Invoke-Sql([string]$path, [string]$sql) {
    $db = Open-Sqlite $path
    try {
        $err = [IntPtr]::Zero
        $rc = [TunqioRatingsSqlite]::sqlite3_exec($db, [System.Text.Encoding]::UTF8.GetBytes($sql + "`0"), [IntPtr]::Zero, [IntPtr]::Zero, [ref]$err)
        if ($rc -ne 0) { throw ('sqlite: ' + [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi([TunqioRatingsSqlite]::sqlite3_errmsg($db))) }
    }
    finally { [TunqioRatingsSqlite]::sqlite3_close_v2($db) | Out-Null }
}

# The first column of the first row as an integer, 'NULL' for SQL NULL, or 'no row'.
function Read-SqlScalar([string]$path, [string]$sql) {
    $db = Open-Sqlite $path
    try {
        $stmt = [IntPtr]::Zero
        $rc = [TunqioRatingsSqlite]::sqlite3_prepare_v2($db, [System.Text.Encoding]::UTF8.GetBytes($sql + "`0"), -1, [ref]$stmt, [IntPtr]::Zero)
        if ($rc -ne 0) { throw ('sqlite: ' + [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi([TunqioRatingsSqlite]::sqlite3_errmsg($db))) }
        try {
            if ([TunqioRatingsSqlite]::sqlite3_step($stmt) -ne 100) { return 'no row' }  # SQLITE_ROW
            if ([TunqioRatingsSqlite]::sqlite3_column_type($stmt, 0) -eq 5) { return 'NULL' }  # SQLITE_NULL
            return [string][TunqioRatingsSqlite]::sqlite3_column_int64($stmt, 0)
        }
        finally { [TunqioRatingsSqlite]::sqlite3_finalize($stmt) | Out-Null }
    }
    finally { [TunqioRatingsSqlite]::sqlite3_close_v2($db) | Out-Null }
}

# ---- the automation tree ----------------------------------------------------------------------------------------------
function Wait-For([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "waited ${seconds}s and $what never happened"
}

function Get-TypeName($element) { $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '' }

function Find-Named($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)))
}

function Find-NamedOfType($scope, [string]$text, [string]$type) {
    foreach ($element in $scope.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($element.Current.Name -eq $text -and (Get-TypeName $element) -eq $type) { return $element }
    }
    return $null
}

function Find-Id($scope, [string]$id) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}

# The first list row whose name starts with $prefix, under the list with automation id 'List' (the Tracks table's and
# album detail's ListView both carry it; they are never in the frame together).
function Find-Row($window, [string]$prefix) {
    $list = Find-Id $window 'List'
    if (-not $list) { return $null }
    foreach ($row in $list.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($row.Current.Name -like "$prefix*") { return $row }
    }
    return $null
}

# The star control under $scope: a Slider named "Rating, ..." - the one element the control is to a screen reader.
function Find-Stars($scope) {
    $sliders = $scope.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Slider)))
    foreach ($slider in $sliders) { if ($slider.Current.Name -like 'Rating, *') { return $slider } }
    return $null
}

function Set-Stars($stars, [int]$value) { $stars.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($value) }
function Get-Stars($stars) { [int]$stars.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value }
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

# Shows or hides the Tracks table's Rating column through the page's own Columns menu, and returns whether it was
# visible before. The column chooser is remembered in settings.json (ui.tracksHiddenColumns), which this script does
# not park, so a profile that hides Rating would otherwise leave the star cells collapsed and out of the tree - which is
# exactly what the first live run found. Changed and put back through the app, as check-settings.ps1 does the theme.
function Set-RatingColumnVisible($window, [bool]$visible) {
    $button = Wait-For { Find-Named $window 'Choose columns' } 15 'the Tracks page offered Choose columns'
    try { $button.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { Invoke-Element $button }
    $item = Wait-For {
        $A::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::MenuItem))) |
            Where-Object { $_.Current.ProcessId -eq $process.Id -and $_.Current.Name -eq 'Rating' } | Select-Object -First 1
    } 8 'the Columns menu listed Rating'
    $toggle = $item.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $was = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($was -ne $visible) {
        # Invoke is a click, which is what the page listens to; Toggle only where the item offers no Invoke.
        $invoke = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke() } else { $toggle.Toggle() }
        Start-Sleep -Milliseconds 700
    }
    try { $button.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    Start-Sleep -Milliseconds 500
    return $was
}

function Find-RepeatButton($window) {
    $window.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button))) |
        Where-Object { $_.Current.Name -like 'Repeat *' } | Select-Object -First 1
}

# Cycles the Repeat button (off -> all -> one) until it says "Repeat one". Invoke where the button offers it, Toggle
# otherwise; the name carries the state, so the loop reads it back rather than counting presses.
function Set-RepeatOne($window) {
    for ($i = 0; $i -lt 6; $i++) {
        $button = Find-RepeatButton $window
        if (-not $button -or -not $button.Current.IsEnabled) { Start-Sleep -Milliseconds 300; continue }
        if ($button.Current.Name -eq 'Repeat one') { return }
        $invoke = $null
        $toggle = $null
        if ($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke() }
        elseif ($button.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) { $toggle.Toggle() }
        Start-Sleep -Milliseconds 300
    }
}

function Find-NowPlayingOn($window, [string]$title) {
    $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -like "Now playing: $title*" } | Select-Object -First 1
}

function Open-Section($window, [string]$name) {
    Select-Element (Wait-For { Find-NamedOfType $window $name 'ListItem' } 15 "the navigation offered $name")
    Start-Sleep -Milliseconds 800
}

# Waits until the row's star control says $expected, which is how "the shown value follows" is asserted rather than assumed.
function Wait-Stars([scriptblock]$find, [int]$expected, [int]$seconds, [string]$what) {
    Wait-For {
        $stars = & $find
        if ($stars -and (Get-Stars $stars) -eq $expected) { $stars }
    } $seconds $what
}

# ---- setting up and tearing down --------------------------------------------------------------------------------------
function Restore-Database {
    $parkedFiles = @(Get-ChildItem $parked -ErrorAction SilentlyContinue)
    if ($parkedFiles.Count -eq 0) { return $false }
    Remove-Item "$dbPath*" -Force -ErrorAction SilentlyContinue
    foreach ($file in $parkedFiles) { Move-Item $file.FullName (Join-Path $dataRoot $file.Name) -Force }
    return $true
}

function Close-Shell($process) {
    if (-not $process) { return }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $null 20
    if ($closeProblem) { $script:failures += $closeProblem }
    Start-Sleep -Seconds 1
}

Write-Output "shell:    $Exe"
Write-Output "scratch:  $runRoot"

# Refuse rather than kill: an app already running is somebody using it, and this script moves their database aside.
$running = @(Get-Process Tunqio -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw ("Tunqio is already running (pid $($running.Id -join ', ')). This script moves $dbPath aside for the run, " +
           'so it will not touch a session somebody is using. Close the app and run again.')
}

# T-183: under a packaged app %LOCALAPPDATA% is a merged view over the package's LocalCache, and the launched shell may
# see a different layer from this script. Refuse rather than guess which layer holds the user's library.
$redirected = @(Get-ChildItem $dataRoot -Force -ErrorAction SilentlyContinue | Where-Object { ($_.Target -join ';') -match '\\Packages\\' })
if ($redirected.Count -gt 0) {
    throw ("$dataRoot is redirected: $(($redirected | ForEach-Object { $_.Name + ' -> ' + ($_.Target -join ';') }) -join ' | '). " +
           'Nothing has been moved or launched. Run this from a shell that is not started by a packaged app (T-183).')
}

if (Restore-Database) { Write-Output 'note: a previous run had left the real library database parked; it has been put back.' }
Remove-Item $music -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $parked | Out-Null
New-Item -ItemType Directory -Force $music | Out-Null
# The real database's files and their hashes before they move, so the restore at the end is proved byte-identical
# rather than assumed. A harness once parked the real library.db and the app opened another one anyway (T-183).
$realHashes = @{}
foreach ($file in Get-ChildItem "$dbPath*" -ErrorAction SilentlyContinue) { $realHashes[$file.Name] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash }
Write-Output "real library files to park: $(if ($realHashes.Count) { ($realHashes.Keys | Sort-Object | ForEach-Object { $_ + ' sha256 ' + $realHashes[$_] }) -join ', ' } else { 'none' })"
foreach ($file in Get-ChildItem "$dbPath*" -ErrorAction SilentlyContinue) { Move-Item $file.FullName (Join-Path $parked $file.Name) -Force }
$stillThere = @(Get-ChildItem "$dbPath*" -ErrorAction SilentlyContinue)
if ($stillThere.Count -gt 0) {
    throw ("parking did not take: $(($stillThere | ForEach-Object { $_.Name }) -join ', ') still in $dataRoot after moving the " +
           "database to $parked. Nothing has been launched. Put the real library database back by hand (T-183).")
}

$process = $null
$window = $null
$mutedAtStart = $null
$script:ratingColumnWasVisible = $null
$reachedEnd = $false
$startedAt = Get-Date
$trackPath = Join-Path (Join-Path $music $albumFolder) $trackFile

# Everything from here to the end is inside one try whose finally puts the real database back, the same shape as
# check-tag-editor.ps1: no exception after parking - in the run or in the checks after it - can skip the restore.
try {
try {
    Copy-Item (Join-Path $fixtures $albumFolder) $music -Recurse
    $hashBefore = Get-Sha256 $trackPath
    Write-Output "copied $((Get-ChildItem $music -Recurse -File).Count) fixture files into the scratch library"

    # Boot once so the app creates its schema; it must CREATE the database, or the parking did not isolate anything.
    $today = 'tunqio-' + (Get-Date -Format 'yyyyMMdd') + '.log'
    $bootLog = Join-Path $logDir $today
    $bootLogStart = (Get-Item $bootLog -ErrorAction SilentlyContinue).Length
    if (-not $bootLogStart) { $bootLogStart = 0 }
    $boot = Start-Process $Exe -PassThru
    Wait-For { Test-Path $dbPath } 60 'the app created its library database' | Out-Null
    Start-Sleep -Seconds 6
    Close-Shell $boot
    $stream = New-Object System.IO.FileStream($bootLog, 'Open', 'Read', 'ReadWrite')
    try { $stream.Seek($bootLogStart, 'Begin') | Out-Null; $bootLines = (New-Object System.IO.StreamReader($stream)).ReadToEnd() }
    finally { $stream.Dispose() }
    $opening = [regex]::Match($bootLines, 'library\.db (created|opened) at schema')
    if (-not $opening.Success) { throw "the boot launch logged no 'library.db created' or 'opened' line in $bootLog; nothing has been seeded (T-183)" }
    if ($opening.Groups[1].Value -ne 'created') { throw "the boot launch OPENED an existing library database after the real one was parked in $parked. Nothing has been seeded (T-183)." }
    Invoke-Sql $dbPath ("INSERT INTO library_folder(path, enabled) VALUES ('" + $music.Replace("'", "''") + "', 1);")
    Write-Output 'scratch library seeded; launching the shell over it'

    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-For { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    Set-UiaWindowSize -ProcessId $process.Id -Width 1600 -Height 900

    $toggle = (Wait-For { Find-Id $window 'MuteButton' } 10 'the Mute button appeared').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }

    # ---- the Tracks row, rated through RangeValue ----------------------------------------------------------------
    Open-Section $window 'Tracks'
    $row = Wait-For { Find-Row $window $rowPrefix } 60 "the Tracks table showed '$rowPrefix'"
    Check 'The row announces its four columns and not the rating' ($row.Current.Name -notmatch 'star|Rating') "'$($row.Current.Name)'"
    $script:ratingColumnWasVisible = Set-RatingColumnVisible $window $true
    Write-Output "  note  the Rating column was $(if ($script:ratingColumnWasVisible) { 'visible' } else { 'hidden in this profile; shown for the run and hidden again at the end' })"
    $stars = $null
    try { $stars = Wait-For { $r = Find-Row $window $rowPrefix; if ($r) { Find-Stars $r } } 15 'the row carried a star control' }
    catch {
        # Say what IS under the row, raw view included, so a missing control is a finding and not a guess.
        $r = Find-Row $window $rowPrefix
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
        $dump = @()
        $stack = New-Object System.Collections.Stack
        if ($r) { $stack.Push(@($r, 0)) }
        while ($stack.Count -gt 0 -and $dump.Count -lt 40) {
            $pair = $stack.Pop(); $el = $pair[0]; $depth = $pair[1]
            $dump += ('{0}{1} "{2}" id={3} class={4} offscreen={5}' -f ('  ' * $depth), (Get-TypeName $el), $el.Current.Name, $el.Current.AutomationId, $el.Current.ClassName, $el.Current.IsOffscreen)
            $children = @(); $c = $walker.GetFirstChild($el); while ($c) { $children += $c; $c = $walker.GetNextSibling($c) }
            for ($i = $children.Count - 1; $i -ge 0; $i--) { $stack.Push(@($children[$i], ($depth + 1))) }
        }
        Write-Output '  note  raw automation subtree of the row:'
        $dump | ForEach-Object { Write-Output "          $_" }
        throw
    }
    $range = $stars.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    Check 'The star control is a slider named for Narrator with a 0..5 range' ($stars.Current.Name -eq 'Rating, not rated' -and $range.Current.Minimum -eq 0 -and $range.Current.Maximum -eq 5 -and -not $range.Current.IsReadOnly) "'$($stars.Current.Name)', $($range.Current.Minimum)..$($range.Current.Maximum)"
    Check 'The star control is on screen inside its row' (-not (Get-UiaRect $stars).Offscreen -and $null -eq (Test-UiaInside (Get-UiaRect $stars) (Get-UiaRect $row) 'stars' 'row')) (Get-UiaRect $stars).Describe
    Check 'The star control takes keyboard focus' ($stars.Current.IsKeyboardFocusable) "focusable=$($stars.Current.IsKeyboardFocusable)"

    Set-Stars $stars 4
    $stars = Wait-Stars { Find-Stars (Find-Row $window $rowPrefix) } 4 10 'the row showed four stars'
    Check 'Setting the row to four stars renames the control' ($stars.Current.Name -eq 'Rating, 4 of 5 stars') "'$($stars.Current.Name)'"

    # Sort by rating: the rated track leads a descending list (AC-453 on screen).
    Invoke-Element (Wait-For { Find-Named $window 'Sort by rating' } 10 'the header offered Sort by rating')
    $first = Wait-For {
        $list = Find-Id $window 'List'
        if ($list) { $rows = $list.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition); if ($rows.Count -gt 0 -and $rows[0].Current.Name -like "$rowPrefix*") { $rows[0] } }
    } 20 'the rated track led the list sorted by rating'
    Check 'Sort by rating puts the only rated track first' ($null -ne $first) "'$($first.Current.Name)'"
    Invoke-Element (Find-Named $window 'Sort by title')
    Start-Sleep -Milliseconds 800

    # ---- Now Playing, by playing the album from its detail page ---------------------------------------------------
    Open-Section $window 'Albums'
    Wait-For { Find-Named $window $tileName } 30 "the Albums grid showed '$tileName'" | Out-Null
    # The fixture tracks are one second long, so an album left to play is past First Light before the rest of this
    # script has looked; run 3 cleared nothing because Now Playing had moved on by then. Repeat one holds it: the tile
    # is invoked (AlbumsGrid.OnTileClick plays the album; detail's Play album is used if a tile opens detail instead),
    # Repeat is cycled to one at once, and the attempt counts only if First Light is still showing 2.5 s later. Repeat
    # is queue state, which lives in the scratch library.db, so nothing of it outlives the run.
    $nowPlaying = $null
    for ($attempt = 1; $attempt -le 6 -and -not $nowPlaying; $attempt++) {
        $tile = Find-Named $window $tileName
        if ($tile) { Invoke-Element $tile }
        else { $play = Find-Named $window 'Play album'; if ($play) { Invoke-Element $play } }
        Set-RepeatOne $window
        Start-Sleep -Milliseconds 2500
        $nowPlaying = Find-NowPlayingOn $window 'First Light'
    }
    if (-not $nowPlaying) { throw "after 6 attempts Now Playing was not holding First Light (repeat is '$((Find-RepeatButton $window).Current.Name)')" }
    Write-Output "  note  Now Playing holds First Light with '$((Find-RepeatButton $window).Current.Name)'"
    $npStars = Wait-Stars { Find-Stars $nowPlaying } 4 15 'Now Playing showed the four stars the row set'
    Check 'Now Playing shows the rating the Tracks row set, without a rescan' ($npStars.Current.Name -eq 'Rating, 4 of 5 stars') "'$($npStars.Current.Name)'"

    # Rate from Now Playing; album detail's row (still open in the sidebar) follows.
    Set-Stars $npStars 2
    $npStars = Wait-Stars { Find-Stars $nowPlaying } 2 10 'Now Playing showed two stars'
    Check 'Setting two stars in Now Playing renames its control' ($npStars.Current.Name -eq 'Rating, 2 of 5 stars') "'$($npStars.Current.Name)'"
    # Album detail, reached through Now Playing's own "Go to album" link unless the tile already opened it. Its rows
    # announce the same four columns as a Tracks row, so the same prefix finds First Light there.
    if (-not (Find-Named $window 'Play album')) {
        Invoke-Element (Wait-For { Find-Named $window 'Go to album' } 10 'Now Playing offered Go to album')
    }
    Wait-For { Find-Named $window 'Play album' } 15 'album detail opened' | Out-Null
    $detailRow = Wait-For { Find-Row $window $rowPrefix } 15 'album detail listed First Light by its four-column name'
    Check 'An album detail row announces its four columns and not the rating' ($detailRow.Current.Name -notmatch 'star|Rating|AlbumTrackRow') "'$($detailRow.Current.Name)'"
    $detailStars = Wait-Stars { $r = Find-Row $window $rowPrefix; if ($r) { Find-Stars $r } } 2 10 'the album detail row followed to two stars'
    Check 'The album detail row follows a rating set in Now Playing' ($detailStars.Current.Name -eq 'Rating, 2 of 5 stars') "'$($detailStars.Current.Name)'"

    # And the Tracks row follows too, then clears from Now Playing.
    Open-Section $window 'Tracks'
    $rowStars = Wait-Stars { $r = Find-Row $window $rowPrefix; if ($r) { Find-Stars $r } } 2 30 'the Tracks row showed two stars'
    Check 'The Tracks row shows a rating set in Now Playing' ($rowStars.Current.Name -eq 'Rating, 2 of 5 stars') "'$($rowStars.Current.Name)'"
    Check 'Now Playing is still holding First Light before the clear' ($null -ne (Find-NowPlayingOn $window 'First Light')) "'$($nowPlaying.Current.Name)'"
    Set-Stars $npStars 0
    $npStars = Wait-Stars { Find-Stars $nowPlaying } 0 10 'Now Playing cleared its own stars'
    Check 'Clearing in Now Playing clears its own control' ($npStars.Current.Name -eq 'Rating, not rated') "'$($npStars.Current.Name)'"
    $rowStars = Wait-Stars { $r = Find-Row $window $rowPrefix; if ($r) { Find-Stars $r } } 0 10 'the Tracks row cleared'
    Check 'Clearing from Now Playing clears the row' ($rowStars.Current.Name -eq 'Rating, not rated') "'$($rowStars.Current.Name)'"
    Set-Stars $rowStars 3
    $npStars = Wait-Stars { Find-Stars $nowPlaying } 3 10 'Now Playing showed the three stars the row set'
    Check 'Now Playing follows a rating set on the Tracks row' ($npStars.Current.Name -eq 'Rating, 3 of 5 stars') "'$($npStars.Current.Name)'"

    $reachedEnd = $true
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    if ($window -and $script:ratingColumnWasVisible -eq $false) {
        try {
            Open-Section $window 'Tracks'
            Set-RatingColumnVisible $window $false | Out-Null
            Write-Output 'cleanup: the Rating column is hidden again, as the profile had it'
        }
        catch { Write-Output "WARNING: the Rating column could not be hidden again: $($_.Exception.Message). Untick Rating under Tracks > Columns." }
    }
    if ($window -and $null -ne $mutedAtStart -and -not $mutedAtStart) {
        try { (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { }
    }
    Close-Shell $process
}

# ---- what was stored: the scratch database and the log, read after the app has exited (the sink buffers) -------------
if ($reachedEnd) {
    try {
        $stored = Read-SqlScalar $dbPath ("SELECT rating FROM track WHERE path = '" + $trackPath.Replace("'", "''") + "';")
        Check 'The rating is stored in the track row as stars times 20' ($stored -eq '60') "rating = $stored"
    }
    catch { Check 'The rating is stored in the track row as stars times 20' $false $_.Exception.Message }
    Check 'With write-to-file off (the default) the file is byte-identical' ((Get-Sha256 $trackPath) -eq $hashBefore) 'SHA-256 unchanged'
    $log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
    $lines = @(if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'Rated track \d+ .* in the library' } })
    Check 'The app logged each rating it wrote' ($lines.Count -ge 5) "$($lines.Count) 'Rated track' line(s); last: $(if ($lines.Count) { ($lines[-1] -replace '^.*Rated track', 'Rated track').Trim() } else { 'none' })"
    Check 'No file write was attempted with the switch off' (@(if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'written to the file|could not be written to the file' } }).Count -eq 0) 'no tag-write line'
}

}
catch {
    $script:failures += "the checks after the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the checks after the run stopped: $($_.Exception.Message)"
}
finally {
    # ---- put the real library back, then prove it ----------------------------------------------------------------
    Close-Shell $process
    # The scratch database goes first: with a real one parked, Restore-Database removes it anyway; with none parked
    # (a profile that had no library), removing it is what restores the folder to how it was.
    Remove-Item "$dbPath*" -Force -ErrorAction SilentlyContinue
    if (Restore-Database) { Write-Output 'cleanup: the real library database has been moved back' }
    if ($realHashes.Count -gt 0) {
        $problems = @()
        foreach ($name in $realHashes.Keys) {
            $back = Join-Path $dataRoot $name
            if (-not (Test-Path -LiteralPath $back)) { $problems += "$name is not back in $dataRoot" }
            elseif ((Get-FileHash -Algorithm SHA256 -LiteralPath $back).Hash -ne $realHashes[$name]) { $problems += "$name is back but its SHA-256 differs" }
        }
        Check 'The real library database is back, byte-identical' ($problems.Count -eq 0) $(if ($problems.Count) { $problems -join '; ' } else { "$($realHashes.Count) file(s), SHA-256 matches" })
    }
    # Only an EMPTY park folder is removed: anything still in it is the user's real database.
    if (@(Get-ChildItem $parked -Force -ErrorAction SilentlyContinue).Count -eq 0) { Remove-Item $parked -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Output "WARNING: files remain in $parked - they are the real library database. Put them back in $dataRoot by hand." }
    if (-not $KeepScratch) { Remove-Item $music -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Output "scratch library kept at $music" }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-ratings: PASS'
    exit 0
}
Write-Output "check-ratings: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
