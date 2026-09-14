<#
.SYNOPSIS
  E3-S10 AC-250, AC-251 and AC-252: F2 and the row menu open the tag editor over the Tracks selection, a batch
  shows "(multiple values)" in the fields the selection disagrees on and lists the files it is about to touch, the
  progress bar moves during the write, and the Undo bar the write leaves behind puts the values back.

  Launches the shell and reads its automation tree the way Narrator does, rather than asking the app what it
  believes about itself. The dialog's own view model is already asserted headless in Tunqio.App.Tests; what cannot
  be asserted there is whether the dialog is reachable at all - whether F2 gets past the ListView, whether the row
  menu raises the same thing, whether the placeholder the view model computes ever becomes text on a control, and
  whether the notice the shell is asked to show lands on the notice panel the window is showing. Each of those is a
  wiring question, and a wiring question is only answered from outside the process.

  WHAT IT WRITES, AND WHERE. A tag write edits real files, so this script never goes near a real library. It copies
  two albums out of the committed fixture library (tests/fixtures/library - eight FLACs and eight MP3s, whose tags
  are described by the fixtures' own manifest.json) into a scratch folder, and points the app at that. The fixtures
  are chosen over the user's music for the reason a fixture always wins: they disagree on Title, Artist, Album,
  Album artist, Year and Genre in a way this script knows about in advance, so "the fields the selection disagrees
  on" is a statement with a list behind it rather than a hope.

  The app runs on a scratch profile passed as --data-root (artifacts\check-tag-editor\<stamp>\data, beside the music
  copy in artifacts\check-tag-editor\<stamp>\music), so the real %LocalAppData%\Tunqio is never read or written: its
  library.db, settings, logs and art are not opened, and the script refuses a data root inside it or inside a
  package's redirected LocalCache copy of it (T-194). The whole stamp folder is deleted at the end unless -KeepScratch.
  This replaces T-183's parking of the real library.db for the run, which had failed once and which the data root
  makes unnecessary.

  Seeding is a single INSERT into library_folder through the app's own e_sqlite3.dll, because the only route to it
  in the UI is a system folder picker, and driving a system folder picker by keystroke is exactly the sleep-and-hope
  this kind of script exists to avoid.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output (T-196).
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else started to go away, checking every 30 s, before refusing (T-196).
.PARAMETER Seconds
  How long to give the window before reading the tree.
.PARAMETER KeepScratch
  Leave the scratch folder (the fixture copy and the scratch profile) behind, for looking at what the run wrote.
#>
[CmdletBinding()]
param(
    # T-161: drive a build that is older than the source on purpose (comparing against an old shell).
    [switch]$SkipFreshnessCheck,
    [string]$Exe,
    [int]$Seconds = 14,
    [switch]$KeepScratch,
    # Window widths the open dialog is measured at, comma-separated. A string because under powershell.exe -File an
    # [int[]] of "1600,640" becomes one integer.
    [string]$Widths = '1600,1000,640',
    [int]$WaitMinutes = 10
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, Microsoft.VisualBasic

# Resolved in the body rather than in the param default: $PSScriptRoot is empty there under powershell.exe -File (T-158).
# Release, the build main's merge gate rebuilds: a Debug default drove a build a merge had left stale (T-196).
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$Exe = (Resolve-Path $Exe -ErrorAction SilentlyContinue).Path
if (-not $Exe) { throw 'The shell is not built; run msbuild Tunqio.sln -restore -p:Configuration=Release -p:Platform=x64 first (a project-scoped build leaves a stale native core beside the app -- T-161).' }

# T-161: a harness driving a build that predates its own source reports the OLD binary's behaviour, and every
# symptom of that reads as a product bug. Refuse up front and say which binary is behind.
. (Join-Path $PSScriptRoot 'assert-fresh-build.ps1')
if (-not $SkipFreshnessCheck) { Assert-FreshBuild -AppDir (Split-Path $Exe) }
. (Join-Path $PSScriptRoot 'uia-geometry.ps1')
$dialogWidths = @($Widths -split ',' | Where-Object { $_.Trim() } | ForEach-Object { [int]$_.Trim() })
if ($dialogWidths.Count -eq 0) { throw "-Widths '$Widths' names no width" }
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$fixtures = Join-Path $repo 'tests\fixtures\library'
if (-not (Test-Path $fixtures)) { throw "$fixtures not found." }
if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) {
    throw 'ffprobe is not on PATH. The read-back after the undo is done by a tagger that shares no code with the app, which is the whole point of it.'
}

# The two albums, and what the fixtures say is in them. The write sets Album artist, so these are what the undo owes
# back; everything else here is what makes the batch disagree with itself.
$albums = @(
    @{ Folder = 'Night Signal - Aurora Lines (2019)'; AlbumArtist = 'Night Signal'; Album = 'Aurora Lines'; Year = '2019'; Genre = 'Electronic' },
    @{ Folder = 'The Lanterns - Harbour Songs (2007)'; AlbumArtist = 'The Lanterns'; Album = 'Harbour Songs'; Year = '2007'; Genre = 'Folk' }
)
$newAlbumArtist = 'Tunqio Check T116'
$multiple = '(multiple values)'
# TagEditorViewModel.MultipleValuesHelp, word for word: what a screen reader hears in place of the placeholder (T-123).
$mixedHelp = 'Multiple values. The selected tracks differ on this field, and it is left as it is unless you type in it.'
$batchSize = 12

# T-194: everything the run writes is under one stamped folder in the repo's artifacts, not under %TEMP% (which is
# inside %LocalAppData% and so redirected under a packaged app) and never in the real profile.
$realRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Tunqio'))
$runRoot = [System.IO.Path]::GetFullPath((Join-Path $repo ('artifacts\check-tag-editor\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$dataRoot = Join-Path $runRoot 'data'
$dbPath = Join-Path $dataRoot 'library.db'
$music = Join-Path $runRoot 'music'
if ($dataRoot.StartsWith($realRoot, [System.StringComparison]::OrdinalIgnoreCase) -or $dataRoot -like '*\Packages\*\LocalCache\*') {
    throw "refusing data root ${dataRoot}: it is (or is a redirected copy of) the real profile $realRoot"
}

# ---- sqlite, through the app's own native library ------------------------------------------------------------
# Only one statement is ever run against the database (the folder row the launch scan needs), so this is exec and
# nothing else: no reader, no marshalling of rows. Everything the script wants to know afterwards it reads off the
# automation tree or out of the files, which is where it should be reading it from anyway.
if (-not ('TunqioCheckSqlite' -as [type])) {
    Add-Type -TypeDefinition (@"
using System;
using System.Runtime.InteropServices;
public static class TunqioCheckSqlite {
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(@"SQLITEDLL", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_errmsg(IntPtr db);
}
"@ -replace 'SQLITEDLL', (Join-Path (Split-Path $Exe) 'e_sqlite3.dll'))
}

function Invoke-Sql([string]$path, [string]$sql) {
    $utf8 = [System.Text.Encoding]::UTF8
    $db = [IntPtr]::Zero
    $rc = [TunqioCheckSqlite]::sqlite3_open_v2($utf8.GetBytes($path + "`0"), [ref]$db, 2, [IntPtr]::Zero)  # SQLITE_OPEN_READWRITE
    if ($rc -ne 0) { throw "could not open $path ($rc)" }
    try {
        $error = [IntPtr]::Zero
        $rc = [TunqioCheckSqlite]::sqlite3_exec($db, $utf8.GetBytes($sql + "`0"), [IntPtr]::Zero, [IntPtr]::Zero, [ref]$error)
        if ($rc -ne 0) { throw ('sqlite: ' + [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi([TunqioCheckSqlite]::sqlite3_errmsg($db))) }
    }
    finally { [TunqioCheckSqlite]::sqlite3_close_v2($db) | Out-Null }
}

# ---- the automation tree -------------------------------------------------------------------------------------

$script:window = $null
$script:processId = 0
# Set by the failure-path case, so the log check can tell the failure it caused on purpose from a real one.
$script:expectedFailurePath = $null
$script:t137Verdict = $null
$script:boundsDump = $null

function Get-Descendants($scope) {
    $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Get-TypeName($element) { $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '' }

# By name and, where it is given, control type: a text box sits inside a group of the same name with a label of the
# same name beside it, and only one of the three has anything to say about what was typed into it.
function Get-ElementNamed($scope, [string]$name, [string]$type) {
    foreach ($element in Get-Descendants $scope) {
        if ($element.Current.Name -ne $name) { continue }
        if (-not $type -or (Get-TypeName $element) -eq $type) { return $element }
    }
    return $null
}

# Every element under $scope that a user could scroll vertically, keyed by runtime id. UIA exposes this through
# ScrollPattern, and a control that cannot scroll still advertises the pattern with VerticallyScrollable false -
# which is the distinction the nested-scroll check below rests on.
function Get-VerticallyScrollable($scope) {
    $found = @{}
    foreach ($element in @($scope) + @(Get-Descendants $scope)) {
        $pattern = $null
        if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { continue }
        if (-not $pattern.Current.VerticallyScrollable) { continue }
        $found[($element.GetRuntimeId() -join '.')] = $element
    }
    return $found
}

function Get-ElementWithId($scope, [string]$id) {
    $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}

# The dialog is a ContentDialog, so it is a Window inside the shell's tree rather than a window of its own; its name
# is the title the view model built, which is how the script knows how many tracks the app thinks it opened over.
function Get-Dialog {
    foreach ($element in Get-Descendants $script:window) {
        if ((Get-TypeName $element) -eq 'Window' -and $element.Current.Name -like 'Edit tags*') { return $element }
    }
    return $null
}

function Get-Value($element) {
    try { return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
    catch { return $null }
}

# The placeholder is not in the control view. WinUI puts PlaceholderTextContentPresenter in the raw view only, so a
# screen reader never says "(multiple values)" - it is a thing you see and not a thing you are told, which is worth
# knowing, and is why this reads the raw tree rather than pretending the control view would have had it.
function Get-Placeholder($edit) {
    $raw = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $child = $raw.GetFirstChild($edit)
    while ($child) {
        if ($child.Current.AutomationId -eq 'PlaceholderTextContentPresenter') { return $child.Current.Name }
        $child = $raw.GetNextSibling($child)
    }
    return $null
}

# Activated and then CHECKED (tools/uia-geometry.ps1): throws rather than let a keystroke reach another window.
# This used to be AppActivate and a sleep, which returns having done nothing when someone else holds the foreground,
# and the F2 or Shift+F10 after it then lands in whatever they are typing into (T-163).
function Set-Foreground {
    Assert-UiaForeground -ProcessId $script:processId
}

function Send-Keys([string]$keys) {
    Set-Foreground
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds 600
}

function Wait-For([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "waited ${seconds}s and $what never happened"
}

$script:failures = @()
$script:notes = @()
$script:detail = @()

# A case's scriptblock must not write to the pipeline: everything it emits is its verdict, so anything it wants to
# say on the way goes into $script:detail and is printed under the result.
function Test-Case([string]$what, [scriptblock]$check) {
    $script:detail = @()
    $problem = & $check
    if ($problem) {
        $script:failures += "$what - $problem"
        Write-Output "  FAIL  $what"
        Write-Output "        $problem"
    }
    else {
        Write-Output "  ok    $what"
    }

    foreach ($line in $script:detail) { Write-Output "        $line" }
}

# ---- what the files say, read by something that is not this app ----------------------------------------------

function Get-AlbumArtistOnDisk([string]$path) {
    (& ffprobe -v error -show_entries format_tags=album_artist -of default=nw=1:nk=1 $path 2>$null | Select-Object -First 1)
}

function Get-TitleOnDisk([string]$path) {
    (& ffprobe -v error -show_entries format_tags=title -of default=nw=1:nk=1 $path 2>$null | Select-Object -First 1)
}

# Title -> path over the copy this script made. It used to read the path straight out of each row's automation
# name, because a row with no name of its own fell back to TrackDto's ToString and that included the path. T-122
# gave rows a real name - "<title> by <artist>, <album>, <duration>" - so the path is no longer there to scrape,
# and it should not have been: a script reading a file path out of a screen reader's text was living off an
# accessibility defect. The titles are unique across the two fixture albums, which is what makes this a map.
function Get-PathsByTitle([string]$root) {
    $map = @{}
    foreach ($file in Get-ChildItem $root -Recurse -File) {
        $title = Get-TitleOnDisk $file.FullName
        if ($title) { $map[$title] = $file.FullName }
    }
    return $map
}

# ---- setting up and tearing down ------------------------------------------------------------------------------

$boot = $null
$process = $null

# T-189: closes only the shells this run launched ($boot and $process), each through Close-TunqioShell, and only the
# ones still running. It used to close every Tunqio on the machine, so a run could close Phil's own window or another
# agent's instance that opened during it. Returns the helper's problems for the caller to fail the run with.
function Stop-LaunchedShells {
    $problems = @()
    foreach ($launched in @($boot, $process)) {
        if (-not $launched -or $launched.HasExited) { continue }
        $window = if ($script:processId -eq $launched.Id) { $script:window } else { $null }
        $problem = Close-TunqioShell $launched $window 20
        if ($problem) { $problems += $problem }
    }
    return $problems
}

Write-Output "shell:    $Exe"
Write-Output "scratch:  $runRoot"
Write-Output "data:     $dataRoot"
Write-Output ''

# Refuse rather than kill. This script writes tags, and it used to do that through whatever instance happened to be
# running - including the one its author had open, with their own music in it (2026-09-12). An app already running is
# somebody using it. There is no override, and this script never closes a shell it did not launch (T-189). It waits
# within -WaitMinutes for that app to exit before refusing (T-196).
if (-not (Wait-TunqioExited -WaitMinutes $WaitMinutes)) {
    $running = @(Get-Process Tunqio -ErrorAction SilentlyContinue)
    throw ("Tunqio is still running after $WaitMinutes minute(s) (pid $($running.Id -join ', ')). This script writes tags to files through the " +
           'shell it launches, so it will not run beside a session somebody is using, and it never closes a ' +
           'shell it did not launch. Close the app and run again.')
}

New-Item -ItemType Directory -Force $dataRoot | Out-Null
New-Item -ItemType Directory -Force $music | Out-Null
# ui.welcomeShown present (either value) keeps the first-run welcome away (FirstRunWelcomeViewModel.ShouldShow), so
# the tag editor is the only dialog the run ever sees.
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'), '{ "ui.welcomeShown": false }')

$logBefore = 0
try {
    foreach ($album in $albums) { Copy-Item (Join-Path $fixtures $album.Folder) $music -Recurse }
    $files = @(Get-ChildItem $music -Recurse -File)
    Write-Output "copied $($files.Count) fixture files into the scratch library"

    # Two launches: the schema is the app's to create, and the folder row can only go into a database that exists.
    # The first window is closed rather than killed, so the write-ahead log is checkpointed and the schema is really
    # in the file the second launch opens.
    # T-183, kept on the scratch profile (T-194): the boot launch must CREATE the database. If it opened one, the app
    # did not honour --data-root, and seeding a folder row into it would be writing to a library nobody chose.
    $bootLog = Join-Path $dataRoot ('logs\tunqio-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    $bootLogStart = (Get-Item $bootLog -ErrorAction SilentlyContinue).Length
    if (-not $bootLogStart) { $bootLogStart = 0 }

    $boot = Start-Process $Exe -ArgumentList @('--data-root', "`"$dataRoot`"") -PassThru
    Wait-For { Test-Path $dbPath } 60 'the app created its library database' | Out-Null
    Start-Sleep -Seconds 6
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $boot $null 20
    if ($closeProblem) { $script:failures += $closeProblem }
    Start-Sleep -Seconds 1

    $bootLines = ''
    $stream = New-Object System.IO.FileStream($bootLog, 'Open', 'Read', 'ReadWrite')
    try {
        $stream.Seek($bootLogStart, 'Begin') | Out-Null
        $bootLines = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
    }
    finally { $stream.Dispose() }
    $opening = [regex]::Match($bootLines, 'library\.db (created|opened) at schema')
    if (-not $opening.Success) {
        throw "the boot launch logged no 'library.db created' or 'opened' line in $bootLog, so whether it made a fresh database cannot be told; nothing has been seeded (T-183)"
    }
    if ($opening.Groups[1].Value -ne 'created') {
        throw "the boot launch OPENED an existing library database instead of creating one in the empty scratch profile $dataRoot. Nothing has been seeded, selected or written (T-183, T-194)."
    }

    Invoke-Sql $dbPath ("INSERT INTO library_folder(path, enabled) VALUES ('" + $music.Replace("'", "''") + "', 1);")
    Write-Output 'scratch library seeded; launching the shell over it'

    $log = Join-Path $dataRoot ('logs\tunqio-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    $logBefore = (Get-Item $log -ErrorAction SilentlyContinue).Length
    if (-not $logBefore) { $logBefore = 0 }

    $process = Start-Process $Exe -ArgumentList @('--data-root', "`"$dataRoot`"") -PassThru
    $script:processId = $process.Id
    Start-Sleep -Seconds $Seconds

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $script:window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $script:window) { throw 'The shell window never appeared in the automation tree.' }

    # ---- a Tracks list with something in it ------------------------------------------------------------------
    (Get-ElementNamed $script:window 'Tracks' 'ListItem').GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $table = Wait-For {
        $candidate = Get-ElementWithId $script:window 'List'
        if ($candidate -and $candidate.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition).Count -ge $batchSize) { $candidate }
    } 60 "the Tracks table filled with at least $batchSize rows"

    $rows = $table.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Output "Tracks table: $($rows.Count) rows"

    # The rows carry no automation name of their own, so what UIA reports is the row object's ToString - the whole
    # TrackDto. That is a real defect (Narrator reads out a C# record), it is reported below rather than worked
    # around, and in the meantime it is also the only place the tree says which file a row is. Reading it is not an
    # endorsement of it.
    $pathsByTitle = Get-PathsByTitle $music

    # Waited for, not assumed. The launch scan inserts a row as soon as it has seen the file and refines it when
    # the tags are read, so for a moment a row's title is the one derived from the file name rather than the one
    # in the tag - and the map above is built from tags. Reading the table before that settles gave "matched 3 of
    # 12" twice, with all twelve rows correctly named: the names were right and simply not final yet.
    #
    # So the condition is the thing itself: every row this script is about to select resolves to a file on disk.
    # A fixed sleep here would be the same guess that cost T-134 two rounds.
    $namedRows = 0
    $selected = @()
    $unmatched = @()
    $deadline = (Get-Date).AddSeconds(45)
    while ($true) {
        $rows = $table.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        $namedRows = 0
        $selected = @()
        $unmatched = @()
        for ($i = 0; $i -lt [Math]::Min($batchSize, $rows.Count); $i++) {
            $name = $rows[$i].Current.Name
            if ($name -notlike 'TrackDto {*') { $namedRows++ }
            # The name leads with the title (T-122): "<title> by <artist>, <album>, <duration>". Take the title and
            # resolve the file through the map, so what is asserted later is a real path on disk rather than a
            # string this script parsed out of presentation text.
            $title = if ($name -like 'TrackDto {*') { if ($name -match ', Title = (?<t>.*?), Artists = ') { $Matches['t'] } else { $null } }
                     else { ($name -split ' by ', 2)[0] }
            if ($title -and $pathsByTitle.ContainsKey($title)) {
                $selected += [pscustomobject]@{ Path = $pathsByTitle[$title]; Title = $title }
            }
            else {
                $unmatched += $title
            }
        }

        if ($selected.Count -eq $batchSize -or (Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds 500
    }

    # The isolation, proved rather than assumed. The scratch profile is supposed to leave the app with nothing but the
    # fixture copy; on 2026-09-12, when this script parked the real database instead, the Tracks table came up full of the author's own music while
    # this script was one step away from selecting twelve rows of it and writing tags to them. It threw first, by
    # luck rather than design, because those titles did not match the fixtures. So that coincidence becomes the
    # rule: if what is on screen is not the fixture library, stop before touching anything.
    if ($selected.Count -ne $batchSize -and $unmatched.Count -gt 0) {
        $fixtureTitles = @($pathsByTitle.Keys)
        $strangers = @($unmatched | Where-Object { $_ -and $_ -notin $fixtureTitles })
        if ($strangers.Count -gt 0) {
            throw ("the Tracks table is not showing the fixture library - it holds titles this script did not put " +
                   "there: $($strangers -join ' | '). The shell was launched on the scratch profile $dataRoot, " +
                   'which held only the fixture copy. Nothing has been selected and nothing has been written. Check ' +
                   'that the shell honours --data-root before letting this run again.')
        }
    }

    if ($selected.Count -ne $batchSize) {
        throw ("could not match $batchSize rows to files on disk after 45s; got $($selected.Count). " +
               "Rows named: $namedRows of $batchSize. Titles on disk: $($pathsByTitle.Count). " +
               "Row titles that matched nothing: $($unmatched -join ' | '). " +
               'A row name that no longer leads with the title would show up here first.')
    }

    $rows = $table.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    for ($i = 0; $i -lt $batchSize; $i++) {
        $item = $rows[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($i -eq 0) { $item.Select() } else { $item.AddToSelection() }
    }

    if ($namedRows -eq 0) {
        $script:notes += 'every row in the Tracks table falls back to TrackDto.ToString() for its automation name, so Narrator reads out the record (E3-S8, not one of the criteria here)'
    }
    Write-Output "selected $batchSize rows: $(($selected | ForEach-Object { $_.Title }) -join ', ')"
    Write-Output ''

    # ---- AC-252, first half: F2 over the selection -------------------------------------------------------------
    Write-Output 'AC-252  F2 and the row menu open the dialog over the selection'

    function Open-AndCheck([string]$how) {
        $dialog = Get-Dialog
        if (-not $dialog) { return "$how did not open the tag editor" }
        if ($dialog.Current.Name -notlike "*$batchSize tracks*") {
            return "the dialog opened as '$($dialog.Current.Name)', which does not say it is over $batchSize tracks"
        }

        # The preview list is the dialog's own account of which files it is about to touch; each row's Display is
        # the track title. Comparing it to the titles read off the Tracks rows is the only way to say that the
        # dialog opened over *this* selection rather than over twelve tracks of its own choosing.
        $lists = @()
        foreach ($element in Get-Descendants $dialog) { if ((Get-TypeName $element) -eq 'List') { $lists += $element } }
        if ($lists.Count -ne 1) { return "the dialog has $($lists.Count) lists in it; the preview list should be the only one" }
        $previewed = @()
        foreach ($item in $lists[0].FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            $text = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
            if ($text -and $text.Current.Name) { $previewed += $text.Current.Name }
        }
        $expected = @($selected | ForEach-Object { $_.Title } | Sort-Object)
        $actual = @($previewed | Sort-Object)
        if ($actual.Count -ne $expected.Count) { return "the preview list names $($actual.Count) files for a selection of $($expected.Count)" }
        if (Compare-Object $expected $actual) {
            return "the preview list names files the selection does not: [$($actual -join ', ')] against [$($expected -join ', ')]"
        }
        return $null
    }

    Test-Case 'F2 opens the tag editor over the twelve selected tracks' {
        Send-Keys '{F2}'
        Start-Sleep -Seconds 3
        Open-AndCheck 'F2'
    }

    # ---- AC-250, the placeholder and the preview list ----------------------------------------------------------
    Write-Output ''
    Write-Output 'AC-250  the batch shape: (multiple values) where the selection disagrees, and the files it will touch'

    $dialog = Get-Dialog
    Test-Case 'every field the two albums disagree on is blank behind the (multiple values) placeholder' {
        if (-not $dialog) { return 'no dialog' }
        $problems = @()
        foreach ($field in 'Title', 'Artist', 'Album', 'Album artist', 'Year', 'Track', 'Genre') {
            $edit = Get-ElementNamed $dialog $field 'Edit'
            if (-not $edit) { $problems += "no box named '$field'"; continue }
            $text = Get-Value $edit
            if ($text) { $problems += "'$field' holds '$text' for a selection that does not agree on it" }
            $placeholder = Get-Placeholder $edit
            if ($placeholder -ne $multiple) { $problems += "'$field' shows placeholder '$placeholder', not '$multiple'" }
        }
        # Disc is the other half (T-204): both albums are single-disc and the scanner reads no disc number for either,
        # so the selection AGREES the box is blank, and a placeholder there would tell a sighted user the tracks differ.
        $disc = Get-ElementNamed $dialog 'Disc' 'Edit'
        if (-not $disc) { $problems += "no box named 'Disc'" }
        else {
            $discText = Get-Value $disc
            $discPlaceholder = Get-Placeholder $disc
            if ($discText) { $problems += "'Disc' holds '$discText' although neither album has a disc number" }
            if ($discPlaceholder) { $problems += "'Disc' shows placeholder '$discPlaceholder' although both albums agree it is blank" }
        }
        $problems -join '; '
    }

    Test-Case 'the preview list is named and scrollable rather than a wall of text' {
        if (-not $dialog) { return 'no dialog' }
        if (-not (Get-ElementNamed $dialog 'Files affected' 'Text')) { return "the preview list has no 'Files affected' heading" }
        return $null
    }

    # This case exists because its neighbour above used to claim "scrollable" in its name while asserting only
    # that a heading existed, and the gap was real: the dialog shipped with a ScrollViewer wrapped around a
    # StackPanel holding a height-capped list, so the page scrolled before the list did and a reader chasing the
    # twelfth file moved the whole dialog instead (Phil, reviewing E3-S10; T-138). A list that holds more rows
    # than it can show has to be the thing that scrolls, and it has to be the ONLY thing, or the wheel goes to
    # whichever ancestor claims it first.
    Test-Case 'the file list is what scrolls, and nothing around it scrolls with it' {
        if (-not $dialog) { return 'no dialog' }
        $list = $null
        foreach ($element in Get-Descendants $dialog) {
            if ((Get-TypeName $element) -eq 'List') { $list = $element; break }
        }
        if (-not $list) { return 'no list in the dialog' }

        $scrollable = Get-VerticallyScrollable $dialog
        $inList = Get-VerticallyScrollable $list
        if ($inList.Count -eq 0) {
            return "the file list does not scroll, with $batchSize files in it - either it is showing all of them, or it cannot be reached"
        }

        $outside = @($scrollable.Keys | Where-Object { -not $inList.ContainsKey($_) })
        if ($outside.Count -gt 0) {
            $names = @($outside | ForEach-Object { $t = Get-TypeName $scrollable[$_]; if ($scrollable[$_].Current.Name) { "$t '$($scrollable[$_].Current.Name)'" } else { $t } })
            return "$($outside.Count) thing(s) outside the list scroll too, so the wheel moves the wrong one: $($names -join ', ')"
        }

        return $null
    }

    # Phil, reviewing E3-S10 a second time: "the Line of options with Genre Year Track and Disc breaks the width of
    # the dialog and gets cut off". It did - three boxes at 76 + 60 + 60 with two 8px gaps is 212, in a column that
    # is (420 - 12) / 2 = 204 wide, and a horizontal StackPanel does not shrink to fit: it squeezes its last child
    # instead (T-139).
    #
    # My first attempt at this case compared every box against the dialog's own bounding rectangle and passed on
    # the broken layout, which is worth recording as a warning rather than quietly replacing. Two reasons it could
    # not work. Nothing overflowed - the measurements were Track 64 wide and Disc 48, both ending exactly on the
    # content edge - so "outside the dialog" was never the symptom. And a ContentDialog's rectangle is the
    # full-window overlay (1424 px wide here, for 420 px of content), so almost nothing is ever outside it.
    #
    # What IS the symptom is compression. Track and Disc are declared the same width, so the moment they render at
    # different widths the row is being squeezed to fit and the rightmost box is paying for it. That is a fact
    # about the layout rather than about a number somebody chose, so it survives the boxes being resized.
    Test-Case 'the number boxes are not squeezed to fit the row they are in' {
        if (-not $dialog) { return 'no dialog' }

        $boxes = @{}
        foreach ($element in Get-Descendants $dialog) {
            if ((Get-TypeName $element) -ne 'Edit') { continue }
            $r = $element.Current.BoundingRectangle
            if ($r.Width -le 0) { continue }  # not laid out; nothing to say about its size
            $boxes[$element.Current.Name] = $r
        }

        $script:boundsDump = @()
        foreach ($name in 'Title', 'Genre', 'Year', 'Track', 'Disc') {
            if ($boxes.ContainsKey($name)) {
                $r = $boxes[$name]
                $script:boundsDump += ("  {0,-14} L={1,6} R={2,6} W={3,5}" -f $name, [math]::Round($r.Left), [math]::Round($r.Right), [math]::Round($r.Width))
            }
        }

        foreach ($name in 'Year', 'Track', 'Disc') {
            if (-not $boxes.ContainsKey($name)) { return "no box named '$name'" }
        }

        # Declared identical in the XAML, so any real difference is the row running out of room.
        $track = $boxes['Track'].Width
        $disc = $boxes['Disc'].Width
        if ([math]::Abs($track - $disc) -gt 2) {
            return ("Track and Disc are the same size in the layout but render {0} and {1} px wide, " -f [math]::Round($track), [math]::Round($disc)) +
                   'so the row does not fit and the last box is being cut down to make it'
        }

        # And they must stay past the point where a four-character header stops fitting, whatever the design does
        # next: this is the floor the squeeze went through, not a target.
        foreach ($name in 'Year', 'Track', 'Disc') {
            if ($boxes[$name].Width -lt 52) {
                return "the $name box renders $([math]::Round($boxes[$name].Width)) px wide, too narrow for its own header"
            }
        }

        # The four full-width boxes share an edge; the number row must end on it too rather than past it.
        if ($boxes.ContainsKey('Title') -and ($boxes['Disc'].Right -gt $boxes['Title'].Right + 1)) {
            return ("Disc ends {0} px past the right edge the full-width boxes share" -f [math]::Round($boxes['Disc'].Right - $boxes['Title'].Right))
        }

        return $null
    }

    # The measurements, kept for a failure to be read with rather than guessed at.
    # The measurements, kept for a failure to be read with rather than guessed at. Note that UIA reports CLIPPED
    # rectangles, not layout ones: a box cut off by its column comes back at its visible width, ending exactly on
    # the boundary that cut it. That is why "does anything extend past the edge" could never catch this, and why
    # the assertion that works compares two boxes against each other instead of against a boundary.
    if ($script:boundsDump -and $script:failures.Count -gt 0) {
        $script:boundsDump | ForEach-Object { Write-Output "        $_" }
    }

    # T-163. Every on-screen defect this dialog has shipped (T-137, T-138, T-139) was one a wide window hid, so the
    # open dialog is measured at each width: every box and both buttons on screen, and none narrower than the widest
    # it measured. Not "inside the dialog": UIA clips a rectangle to what is visible, so a box cut off by its column
    # or by the window edge reads as inside whatever cut it, and the dialog's own rectangle is the full-window overlay
    # anyway. Genre is the star column that gives way by design, so it is held to a floor rather than to its widest.
    Test-Case 'the dialog''s boxes and buttons are whole at every window width' {
        if (-not $dialog) { return 'no dialog' }
        $boxes = 'Title', 'Artist', 'Album', 'Album artist', 'Genre', 'Year', 'Track', 'Disc'
        $readings = @()
        $problems = @()
        foreach ($w in $dialogWidths) {
            Set-UiaWindowSize -ProcessId $script:processId -Width $w -Height 900
            $open = Get-Dialog
            if (-not $open) { $problems += "the dialog closed when the window went to ${w}px"; break }
            $measured = 0
            $line = @()
            foreach ($control in @($boxes | ForEach-Object { @{ Name = $_; Type = 'Edit' } }) + @(
                    @{ Name = 'Confirm'; Type = 'Button' }, @{ Name = 'Cancel'; Type = 'Button' })) {
                $element = Get-ElementNamed $open $control.Name $control.Type
                if (-not $element) { $problems += "at ${w}px there is no $($control.Type) '$($control.Name)'"; continue }
                $rect = Get-UiaRect $element
                if ($rect.Offscreen) { $problems += "at ${w}px $($control.Type) '$($control.Name)' has nothing on screen"; continue }
                $measured++
                $readings += [pscustomobject]@{ Width = $w; Name = $control.Name; Rect = $rect }
                if ($control.Name -in 'Title', 'Disc', 'Confirm') { $line += "$($control.Name) $($rect.Left)..$($rect.Right)" }
            }
            $script:detail += ("{0,5}px  {1} of 10 measured; {2}" -f $w, $measured, ($line -join ', '))
        }
        # Back to the widest, so the cases after this one run against the window they were written for.
        Set-UiaWindowSize -ProcessId $script:processId -Width ($dialogWidths | Measure-Object -Maximum).Maximum -Height 900
        $problems += @(Get-UiaClippedControls $readings -Stretch @('Genre') -StretchFloor 60)
        if ($problems.Count -gt 0) { return ($problems -join '; ') }
        return $null
    }

    Test-Case 'Confirm is offered and Cancel is there to leave by' {
        if (-not $dialog) { return 'no dialog' }
        foreach ($button in 'Confirm', 'Cancel') {
            if (-not (Get-ElementNamed $dialog $button 'Button')) { return "the dialog has no '$button' button" }
        }
        # Nothing has been typed yet, so Confirm has nothing to do and should say so.
        if ((Get-ElementNamed $dialog 'Confirm' 'Button').Current.IsEnabled) { return 'Confirm is enabled before anything has been changed' }
        return $null
    }

    # T-123. The placeholder above is read from the raw view because that is the only view WinUI puts it in, which
    # means Narrator never says it. The dialog now carries the same fact as HelpText on each box, and this case reads
    # it the way a screen reader does: the box is found through the CONTROL view, and HelpText is read off that. It
    # has to be present on a box the selection disagrees on, absent on one it agrees on (Disc: both albums carry no
    # disc number), and gone as soon as the box is typed into, because a typed box is no longer "left as it is".
    # SetValue rather than keystrokes, and the box is put back to blank so the cases after this one see it untouched.
    Test-Case 'a screen reader is told which boxes the selection disagrees on (HelpText, control view)' {
        if (-not $dialog) { return 'no dialog' }
        $control = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $inControlView = @{}
        foreach ($element in $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Automation]::ControlViewCondition)) {
            if ((Get-TypeName $element) -eq 'Edit' -and $element.Current.Name) { $inControlView[$element.Current.Name] = $element }
        }
        $problems = @()
        foreach ($field in 'Title', 'Artist', 'Album', 'Album artist', 'Year', 'Track', 'Genre') {
            if (-not $inControlView.ContainsKey($field)) { $problems += "no box named '$field' in the control view"; continue }
            $help = $inControlView[$field].Current.HelpText
            if ($help -ne $mixedHelp) { $problems += "'$field' has HelpText '$help' in the control view, not the multiple-values help" }
        }
        if ($inControlView.ContainsKey('Disc') -and $inControlView['Disc'].Current.HelpText) {
            $problems += "'Disc' says '$($inControlView['Disc'].Current.HelpText)' although both albums agree on it"
        }
        # And the walker agrees the box is a control-view element, not just something FindAll surfaced.
        if ($inControlView.ContainsKey('Title') -and -not $control.GetParent($inControlView['Title'])) {
            $problems += "'Title' has no parent in the control view"
        }
        if ($problems.Count -gt 0) { return ($problems -join '; ') }

        $title = $inControlView['Title']
        $value = $title.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.SetValue('Tunqio Check T123')
        $typed = $null
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline) {
            $typed = $title.Current.HelpText
            if (-not $typed) { break }
            Start-Sleep -Milliseconds 200
        }
        $value.SetValue('')
        $restored = $null
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline) {
            $restored = $title.Current.HelpText
            if ($restored -eq $mixedHelp) { break }
            Start-Sleep -Milliseconds 200
        }
        $script:detail += "Title in the control view: HelpText '$mixedHelp' while mixed; '$typed' once typed into; '$restored' once blanked again"
        if ($typed) { return "'Title' still says '$typed' after it was typed into, when it is about to be written" }
        if ($restored -ne $mixedHelp) { return "'Title' says '$restored' after being blanked back to its loaded value, when it is again left as it is" }
        return $null
    }

    # ---- AC-252, second half: the row menu ---------------------------------------------------------------------
    (Get-ElementNamed $dialog 'Cancel' 'Button').GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2

    Write-Output ''
    Test-Case 'the Edit tags row-menu item opens the same dialog over the same selection' {
        # Shift+F10 is the keyboard's context menu, and it raises the list's ContextRequested exactly as a right
        # click does - which keeps this a walk of the menu the app built rather than a click at a guessed pixel.
        Send-Keys '+{F10}'
        Start-Sleep -Seconds 2
        $item = Get-ElementNamed $script:window 'Edit tags' 'MenuItem'
        if (-not $item) { return 'the row menu has no item named "Edit tags"' }
        if (-not $item.Current.IsEnabled) { return 'the "Edit tags" row-menu item is disabled' }
        $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3
        Open-AndCheck 'the Edit tags row-menu item'
    }

    # ---- AC-250, the progress bar ------------------------------------------------------------------------------
    Write-Output ''
    Write-Output 'AC-250  the progress bar during a twelve-track write'

    $dialog = Get-Dialog
    if (-not $dialog) { throw 'the dialog is not open; there is nothing to write with' }
    $before = @{}
    foreach ($file in $selected) { $before[$file.Path] = Get-AlbumArtistOnDisk $file.Path }

    (Get-ElementNamed $dialog 'Album artist' 'Edit').GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).SetValue($newAlbumArtist)
    Start-Sleep -Milliseconds 800
    $confirm = Get-ElementNamed $dialog 'Confirm' 'Button'
    if (-not $confirm.Current.IsEnabled) { throw 'Confirm stayed disabled after Album artist was typed into' }
    $confirm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    # "Visibly moves" is a thing a person sees; what a machine can honestly say is that the bar's value took more
    # than one reading on the way to the end. So: find the bar once, then read that element as fast as it answers,
    # rather than re-walking the tree each time and sampling the write four times in total.
    $samples = @()
    $bar = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 60) {
        if (-not $bar) {
            $open = Get-Dialog
            if (-not $open) { break }
            $bar = $open.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ProgressBar)))
            continue
        }
        try { $samples += [double]$bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value }
        catch { break }   # the bar went with the dialog, which is how a clean write ends
    }
    $distinct = @($samples | Sort-Object -Unique)

    Test-Case 'a ProgressBar exists during the write and its value advances' {
        if ($samples.Count -eq 0) { return 'no ProgressBar appeared in the dialog while the write was running' }
        if ($distinct.Count -lt 2) { return "the bar only ever read $($distinct -join ', '); it did not move" }
        $moving = @($distinct | Where-Object { $_ -gt 0 -and $_ -lt 1 })
        if ($moving.Count -eq 0) { return "the bar jumped straight to $($distinct[-1]); no reading between empty and full" }
        $script:detail += "$($samples.Count) readings, $($distinct.Count) distinct: " + (($distinct | ForEach-Object { '{0:0.00}' -f $_ }) -join ' ')
        return $null
    }

    # ---- the write itself, and then AC-251 ---------------------------------------------------------------------
    Write-Output ''
    Write-Output 'AC-251  the Undo bar in the sidebar, and what it puts back'

    $stillOpen = Get-Dialog
    if ($stillOpen) {
        # The dialog only stays up when something failed - it is holding the per-file verdicts so the user can read
        # them. Report them, because they are the reason the rest of this section cannot be trusted.
        $verdicts = @()
        foreach ($element in Get-Descendants $stillOpen) {
            if ((Get-TypeName $element) -eq 'Text' -and $element.Current.Name -and $element.Current.Name -notin 'Files affected', 'Confirm', 'Cancel') {
                $verdicts += $element.Current.Name
            }
        }
        $script:failures += 'the write did not finish cleanly - ' + (($verdicts | Where-Object { $_ -like '*could not*' -or $_ -like '*Failed*' }) -join ' | ')
        Write-Output '  FAIL  the twelve-track write finished without a failed file'
        foreach ($verdict in $verdicts) { Write-Output "        $verdict" }
        (Get-ElementNamed $stillOpen 'Cancel' 'Button').GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 2
    }
    else {
        Write-Output '  ok    the twelve-track write finished without a failed file'
    }

    Test-Case 'the write reached the files' {
        $unwritten = @($selected | Where-Object { (Get-AlbumArtistOnDisk $_.Path) -ne $newAlbumArtist })
        if ($unwritten.Count -gt 0) { return "$($unwritten.Count) of $batchSize files do not carry the new album artist" }
        return $null
    }

    $undo = $null
    Test-Case 'an Undo bar appears in the shell notice area' {
        $undo = $null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and -not $undo) {
            $undo = Get-ElementNamed $script:window 'Undo' 'Button'
            if (-not $undo) { Start-Sleep -Milliseconds 400 }
        }
        $script:undoButton = $undo
        if (-not $undo) { return 'no button named "Undo" anywhere in the window after the batch' }
        if (-not $undo.Current.IsEnabled) { return 'the Undo button is there but disabled' }
        if (-not $undo.Current.IsKeyboardFocusable) { return 'the Undo button cannot be reached by keyboard' }
        return $null
    }

    Test-Case 'the bar says what it is about' {
        if (-not $script:undoButton) { return 'no bar' }
        $said = @()
        foreach ($element in Get-Descendants $script:window) {
            if ((Get-TypeName $element) -eq 'Text' -and $element.Current.Name -like '*track*') { $said += $element.Current.Name }
        }
        if (($said | Where-Object { $_ -like "*$batchSize*" }).Count -eq 0) {
            return "the notice area does not say how many tracks were changed; it says [$($said -join ' | ')]"
        }
        return $null
    }

    Test-Case 'Undo puts the values back and takes the bar down' {
        if (-not $script:undoButton) { return 'no Undo button to press' }
        $script:undoButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and (Get-ElementNamed $script:window 'Undo' 'Button')) { Start-Sleep -Milliseconds 400 }
        if (Get-ElementNamed $script:window 'Undo' 'Button') { return 'the Undo bar is still up 30s after it was pressed' }

        $wrong = @()
        foreach ($file in $selected) {
            $now = Get-AlbumArtistOnDisk $file.Path
            if ($now -ne $before[$file.Path]) { $wrong += "$(Split-Path $file.Path -Leaf) is '$now', was '$($before[$file.Path])'" }
        }
        if ($wrong.Count -gt 0) { return "$($wrong.Count) files did not come back: $($wrong -join '; ')" }
        return $null
    }

    # ---- the single-track shape, as the control on the placeholder rule ----------------------------------------
    Write-Output ''
    Write-Output 'AC-250  the control: one track, and the placeholder is not there'

    Test-Case 'a single-track dialog shows that track''s values and no (multiple values)' {
        # Fresh rows: the undo rescanned the folder it wrote, so the containers the earlier walk held are gone.
        $table = Get-ElementWithId $script:window 'List'
        $fresh = $table.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        if ($fresh.Count -eq 0) { return 'the Tracks table is empty after the undo' }
        $fresh[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Send-Keys '{F2}'
        Start-Sleep -Seconds 3
        $single = Get-Dialog
        if (-not $single) { return 'F2 over one row did not open the dialog' }
        if ($single.Current.Name -like '*tracks*') { return "the single-track dialog is titled '$($single.Current.Name)'" }
        $problems = @()
        $album = Get-Value (Get-ElementNamed $single 'Album' 'Edit')
        if (-not $album) { $problems += 'the Album box is empty for a single track' }
        foreach ($field in 'Title', 'Album', 'Album artist') {
            $placeholder = Get-Placeholder (Get-ElementNamed $single $field 'Edit')
            if ($placeholder -eq $multiple) { $problems += "'$field' shows the batch placeholder for a single track" }
            # T-123: nor is a screen reader told one track disagrees with itself.
            $help = (Get-ElementNamed $single $field 'Edit').Current.HelpText
            if ($help) { $problems += "'$field' has HelpText '$help' for a single track" }
        }
        (Get-ElementNamed $single 'Cancel' 'Button').GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $problems -join '; '
    }

    # ---- the failure path, which is the only time the verdict column is on screen ------------------------------
    Write-Output ''
    Write-Output 'T-137  a file that cannot be written keeps the dialog up with its verdict'

    Test-Case 'a failed file keeps the dialog open and says so, instead of closing over its own error' {
        # Phil, reviewing E3-S10, could not find the verdict column. It is real, and it is only ever visible here:
        # ConfirmAsync hides the dialog when report.Failed is 0, so on a clean write the verdicts are written onto
        # the rows and dismissed in the same turn. Q-31 settled that this is right - the shell's Undo bar is the
        # report a person actually reads - which makes the column a failure surface, and makes THIS the path worth
        # holding still. If that Hide() condition ever loosened, a failed file would take its own error off the
        # screen with it and a green suite would not notice.
        #
        # Read-only rather than a held handle: File.Replace cannot swap a read-only destination, so the outcome is
        # Failed without racing anything, and T-124's retry has nothing to retry into.
        $table = Get-ElementWithId $script:window 'List'
        $rows = $table.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        if ($rows.Count -eq 0) { return 'the Tracks table is empty' }

        $name = $rows[0].Current.Name
        $title = ($name -split ' by ', 2)[0]
        $victimPath = $pathsByTitle[$title]
        if (-not $victimPath) { return "could not resolve '$title' to a file on disk" }
        $victim = Get-Item $victimPath
        $script:expectedFailurePath = $victimPath
        $victim.IsReadOnly = $true
        try {
            $rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Send-Keys '{F2}'
            Start-Sleep -Seconds 3
            $failing = Get-Dialog
            if (-not $failing) { return 'F2 did not open the dialog' }

            $box = Get-ElementNamed $failing 'Album artist' 'Edit'
            if (-not $box) { return 'no Album artist box' }
            $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Tunqio Check T137')
            Start-Sleep -Milliseconds 400
            $confirm = Get-ElementNamed $failing 'Confirm' 'Button'
            if (-not $confirm.Current.IsEnabled) { return 'Confirm stayed disabled after a change' }
            $confirm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

            $deadline = (Get-Date).AddSeconds(20)
            $verdict = $null
            while ((Get-Date) -lt $deadline -and -not $verdict) {
                $open = Get-Dialog
                if (-not $open) { return 'the dialog closed on a file it could not write, taking the error with it' }
                foreach ($element in Get-Descendants $open) {
                    if ((Get-TypeName $element) -ne 'Text') { continue }
                    if ($element.Current.Name -match 'could not|failed|read-only|denied') { $verdict = $element.Current.Name; break }
                }
                if (-not $verdict) { Start-Sleep -Milliseconds 400 }
            }

            if (-not $verdict) { return 'the dialog stayed open but nothing on it says why the file was not written' }
            # Kept for the caller to print. A Write-Output in here would be part of what the scriptblock returns,
            # and Test-Case reads a non-empty return as the reason it failed.
            $script:t137Verdict = $verdict

            $onDisk = Get-AlbumArtistOnDisk $victimPath
            if ($onDisk -eq 'Tunqio Check T137') { return 'the file was written despite being read-only' }

            (Get-ElementNamed (Get-Dialog) 'Cancel' 'Button').GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Seconds 2
            return $null
        }
        finally {
            $victim.IsReadOnly = $false
        }
    }

    if ($script:t137Verdict) { Write-Output "        verdict on screen: $($script:t137Verdict)" }

    # ---- what the app said about itself while all that was happening -------------------------------------------
    Write-Output ''
    Test-Case 'the app logged no error of its own during the run' {
        $stream = New-Object System.IO.FileStream($log, 'Open', 'Read', 'ReadWrite')
        try {
            $stream.Seek($logBefore, 'Begin') | Out-Null
            $reader = New-Object System.IO.StreamReader($stream)
            $tail = $reader.ReadToEnd()
        }
        finally { $stream.Dispose() }
        # An exception is logged over several lines, and the first of them is the least informative one Serilog
        # writes; the reason is on the lines after it, so the report carries those too.
        $lines = $tail -split "`r?`n"
        $bad = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -notmatch '\[ERR\]|\[FTL\]' -and $lines[$i] -notmatch 'Tag write of .* failed') { continue }
            # The failure-path case above makes one file unwritable on purpose, and the app is right to log that.
            # Excluded by path so it stays one specific expected line rather than a hole the size of the word.
            if ($script:expectedFailurePath -and $lines[$i] -like ('*' + $script:expectedFailurePath + '*')) { continue }
            $bad += $lines[$i]
            for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $lines.Count); $j++) {
                if ($lines[$j] -match '^\d{4}-\d{2}-\d{2} ') { break }
                if ($lines[$j].Trim()) { $bad += '  ' + $lines[$j].Trim() }
            }
        }
        if ($bad.Count -gt 0) {
            $script:detail += @($bad | Select-Object -First 10)
            return "the app logged an error of its own; the lines are below"
        }
        return $null
    }

    # T-189: the shell is closed before the verdict, so an app that does not exit, or exits with a crash code, fails
    # this run with Close-TunqioShell's message instead of being closed silently in the finally.
    Write-Output ''
    Write-Output 'closing the shell this run launched'
    $script:failures += @(Stop-LaunchedShells)

    Write-Output ''
    foreach ($note in $script:notes) { Write-Output "note: $note" }
    if ($script:notes.Count -gt 0) { Write-Output '' }

    if ($script:failures.Count -eq 0) {
        Write-Output 'PASS: F2 and the row menu reach the tag editor, the batch says which fields it does not agree on and which files it will touch, the bar moves, and the Undo bar puts it all back'
        exit 0
    }

    foreach ($failure in $script:failures) { Write-Output "FAIL: $failure" }
    exit 1
}
finally {
    # Reached with a shell still up only when the run threw before its verdict, which has already failed it; the
    # helper still writes a FAIL line if that shell hangs or crashes on close. Only this run's own shells (T-189).
    $leftOpen = @(Stop-LaunchedShells)
    foreach ($problem in $leftOpen) { Write-Output "FAIL: $problem" }
    Start-Sleep -Seconds 1
    # T-194: the scratch profile and the fixture copy go together; nothing outside $runRoot was written.
    if (-not $KeepScratch) {
        Remove-Item $runRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $runRoot) { Write-Output "note: the scratch folder could not be deleted completely: $runRoot" }
    }
    else { Write-Output "scratch folder kept at $runRoot" }
}
