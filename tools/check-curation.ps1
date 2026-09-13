<#
.SYNOPSIS
  E5-S4: Curation's dual pane in a running shell, UIA only. Switches to Curation, makes a playlist with the pane's +,
  selects six source tracks (the batch bar appears), adds them, moves the first one down, undoes that move with the
  pane's Undo (the order comes back and the add is still there to undo), redoes and undoes it again, and reads the
  add's timing from the log.

  UIA patterns only: no keystrokes and no pointer. So it does not drag, and it does not press Ctrl+Z; the drag is a
  human criterion on the board, and the key is ShellShortcutsTests plus the same UndoAsync the button calls.

  WHAT IT CHANGES. A playlist called "Tunqio curation check" for the length of the run, deleted at the end through
  Library > Playlists; a later run deletes one left behind before it starts, and touches no other playlist. The mode is
  persisted (ui.mode), so the mode the app opened in is put back before the window closes. Nothing is played.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Tunqio is already running. This script makes and deletes a playlist and changes the mode through the instance it launches, so it will not touch one somebody is using.'
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$name = 'Tunqio curation check'
$script:failures = @()

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 200
    }
    throw "waited ${seconds}s and $what never happened"
}

function Find-By($scope, $property, [string]$value) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($property, $value)))
}
function Find-Named($scope, [string]$text) { Find-By $scope $A::NameProperty $text }
function Find-Id($scope, [string]$id) { Find-By $scope $A::AutomationIdProperty $id }
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Add-ToSelection($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection() }
function Set-Text($element, [string]$text) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

function Get-Dialog($window, [string]$titleLike) {
    Wait-Until {
        $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $_.Current.Name -like $titleLike } |
            Select-Object -First 1
    } 10 "a dialog like '$titleLike' opened"
}

function Find-Typed($scope, [string]$text, $type) {
    $scope.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)),
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type)))))
}

function Select-Mode($window, [string]$mode) {
    Select-Element (Wait-Until { Find-Named $window "$mode mode" } 10 "the switcher offered $mode")
    Start-Sleep -Milliseconds 800
}

function Get-SelectedMode($window) {
    foreach ($mode in @('Discovery', 'Focus', 'Curation')) {
        $button = Find-Named $window "$mode mode"
        if ($button -and $button.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { return $mode }
    }
    return $null
}

# The rows of a list, found by the list's automation name, as their automation names.
function Get-Rows($window, [string]$listName) {
    $list = Find-Typed $window $listName ([System.Windows.Automation.ControlType]::List)
    if (-not $list) { return @() }
    return @($list.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
}

function Get-Summary($window) { $s = Find-Id $window 'CurationSummary'; if ($s) { $s.Current.Name } else { '' } }

# A button whose name starts with a prefix, since Undo and Redo carry what they would do.
function Find-ButtonLike($window, [string]$like) {
    $window.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
        Where-Object { $_.Current.Name -like $like } | Select-Object -First 1
}

# Deletes a playlist of this check's name through Library > Playlists, which is outside Curation.
function Remove-CheckPlaylist($window) {
    Select-Mode $window 'Discovery'
    $item = Wait-Until { Find-Typed $window 'Playlists' ([System.Windows.Automation.ControlType]::ListItem) } 10 'the sidebar showed Playlists'
    if ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
        Select-Element (Find-Typed $window 'Albums' ([System.Windows.Automation.ControlType]::ListItem))
        Start-Sleep -Milliseconds 800
    }
    Select-Element (Find-Typed $window 'Playlists' ([System.Windows.Automation.ControlType]::ListItem))
    Wait-Until { Find-Named $window 'New playlist' } 10 'Library > Playlists opened' | Out-Null
    Start-Sleep -Milliseconds 800
    $deleted = 0
    while ($row = Find-Typed $window $name ([System.Windows.Automation.ControlType]::ListItem)) {
        Invoke-Element $row
        Invoke-Element (Wait-Until { Find-Named $window 'Delete playlist' } 10 'the Delete playlist button appeared')
        $confirm = Get-Dialog $window 'Delete *'
        Invoke-Element (Wait-Until { Find-Named $confirm 'Delete' } 5 'the confirmation offered Delete')
        Start-Sleep -Milliseconds 1500
        $deleted++
        if ($deleted -gt 5) { throw "could not delete '$name'" }
    }
    return $deleted
}

$process = $null
$window = $null
$startMode = $null
$playlistExists = $false
$logDir = Join-Path $env:LOCALAPPDATA 'Tunqio\logs'
$startedAt = Get-Date

try {
    Write-Output "shell: $Exe"
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    $startMode = Get-SelectedMode $window
    Write-Output "  note  the app opened in $startMode"

    $left = Remove-CheckPlaylist $window
    if ($left -gt 0) { Write-Output "  note  deleted $left '$name' left by an earlier run" }

    # ---- enter Curation -----------------------------------------------------------------------------------------------
    Select-Mode $window 'Curation'
    Wait-Until { Find-Named $window 'Target playlist' } 10 'the Curation pane appeared' | Out-Null
    Check 'Curation shows the dual pane in place of the library pane' ($null -ne (Find-Typed $window 'Source tracks' ([System.Windows.Automation.ControlType]::List)) -and $null -eq (Find-Named $window 'Search')) 'source list present, library search box absent'

    # ---- create "Sunday" --------------------------------------------------------------------------------------------
    Invoke-Element (Wait-Until { Find-Named $window 'New playlist' } 10 'the pane offered New playlist')
    $dialog = Get-Dialog $window 'New playlist'
    Set-Text (Wait-Until { Find-Named $dialog 'Playlist name' } 5 'the name box appeared') $name
    Invoke-Element (Wait-Until { Find-Named $dialog 'Create' } 5 'the dialog offered Create')
    $playlistExists = $true
    Wait-Until { (Get-Summary $window) -eq '0 tracks' } 10 'the new playlist became the target' | Out-Null
    Check 'The pane''s + makes a playlist and makes it the target' ((Get-Summary $window) -eq '0 tracks') "summary '$(Get-Summary $window)'"

    # ---- six tracks, the batch bar, add ----------------------------------------------------------------------------
    $source = Wait-Until { $r = Get-Rows $window 'Source tracks'; if ($r.Count -ge 6) { $r } } 15 'the source listed six tracks'
    Select-Element $source[0]
    for ($i = 1; $i -lt 6; $i++) { Add-ToSelection $source[$i] }
    Start-Sleep -Milliseconds 500
    $batch = Find-Id $window 'CurationBatchText'
    Check 'Selecting six shows the batch bar with the count' ($batch -and $batch.Current.Name -like '6 selected in Library*') "batch text '$(if ($batch) { $batch.Current.Name })'"

    Invoke-Element (Wait-Until { Find-Named $window 'Batch add to playlist' } 5 'the batch bar offered Add')
    Wait-Until { (Get-Summary $window) -like '6 tracks*' } 10 'six tracks reached the target' | Out-Null
    $added = @(Get-Rows $window 'Playlist tracks in Curation' | ForEach-Object { $_.Current.Name })
    $expected = @($source[0..5] | ForEach-Object { $_.Current.Name })
    Check 'Add puts the six in the target in source order' (($added -join '|') -eq ($expected -join '|')) "summary '$(Get-Summary $window)'"

    # ---- reorder two, undo, redo -------------------------------------------------------------------------------------
    $target = Get-Rows $window 'Playlist tracks in Curation'
    Select-Element $target[0]
    Start-Sleep -Milliseconds 300
    Invoke-Element (Find-Named $window 'Move selected down')
    Start-Sleep -Milliseconds 1000
    $moved = @(Get-Rows $window 'Playlist tracks in Curation' | ForEach-Object { $_.Current.Name })
    Check 'Move down swaps the first two' ($moved[0] -eq $added[1] -and $moved[1] -eq $added[0] -and (($moved[2..5]) -join '|') -eq (($added[2..5]) -join '|')) 'rows 1 and 2 swapped, the rest in place'

    $undo = Wait-Until { Find-ButtonLike $window 'Undo move down' } 5 "Undo named the move ('Undo move down')"
    Invoke-Element $undo
    Start-Sleep -Milliseconds 1000
    $undone = @(Get-Rows $window 'Playlist tracks in Curation' | ForEach-Object { $_.Current.Name })
    Check 'Undo puts the reorder back and leaves the add' (($undone -join '|') -eq ($added -join '|') -and (Get-Summary $window) -like '6 tracks*' -and $null -ne (Find-ButtonLike $window 'Undo add 6 tracks')) "undo now reads '$((Find-ButtonLike $window 'Undo*').Current.Name)'"

    Invoke-Element (Wait-Until { Find-ButtonLike $window 'Redo move down' } 5 'Redo named the move')
    Start-Sleep -Milliseconds 1000
    $redone = @(Get-Rows $window 'Playlist tracks in Curation' | ForEach-Object { $_.Current.Name })
    Check 'Redo puts the move back' (($redone -join '|') -eq ($moved -join '|')) 'the swapped order again'

    # ---- the add's timing, from the log --------------------------------------------------------------------------------
    $log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
    $line = if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'Curation added 6 track\(s\) by Add in (\d+) ms' } | Select-Object -Last 1 }
    Check 'The log times the add' ($null -ne $line) "$(if ($line) { $line.Trim() } else { "no line in $logDir" })"
    Write-Output '  note  dragging and the Ctrl+Z key are not exercised here (UIA has neither); see the board'
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    if ($window) {
        try {
            if ($playlistExists) {
                $deleted = Remove-CheckPlaylist $window
                Write-Output "cleanup: deleted $deleted '$name'"
            }
            if ($startMode) { Select-Mode $window $startMode; Write-Output "cleanup: mode back to $startMode" }
        }
        catch { Write-Output "WARNING: cleanup did not finish: $($_.Exception.Message). Check Library > Playlists for '$name' and the mode switcher." }
        try { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    }
    if ($process -and -not $process.WaitForExit(15000)) { $process.Kill() }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-curation: PASS'
    exit 0
}
Write-Output "check-curation: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
