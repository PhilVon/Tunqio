<#
.SYNOPSIS
  E6-S1: playlists in a running shell, UIA only. Library > Playlists makes a playlist through New playlist; an album is
  added to it from album detail's More > Add to playlist; its page shows the totals; Rename renames it; Delete, after
  its confirmation, removes it from the list.

  UIA patterns only: no keystrokes and no pointer. That leaves out what needs one or the other: a drag, the row menu
  (right-click or Shift+F10) and the Delete key, so moving and removing a track are not exercised here; they are
  PlaylistViewModelTests and PlaylistRepositoryTests.

  WHAT IT CHANGES. It writes a playlist called "Tunqio check" into the user's library for the length of the run and
  deletes it at the end, through the app. If the run stops before the delete, the cleanup tries the delete again and
  says by name if it could not; a later run deletes a playlist left under either of its two names ("Tunqio check",
  "Tunqio check renamed") before it starts, and touches no other playlist. Nothing is played unless the restored queue is empty, and then the app is muted.
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
    throw 'Tunqio is already running. This script makes and deletes a playlist through the instance it launches, so it will not touch one somebody is using.'
}

$A = [System.Windows.Automation.AutomationElement]
$name = 'Tunqio check'
$renamed = 'Tunqio check renamed'
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
    $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($property, $value)))
}
function Find-Named($scope, [string]$text) { Find-By $scope $A::NameProperty $text }
function Find-Id($scope, [string]$id) { Find-By $scope $A::AutomationIdProperty $id }
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Set-Text($element, [string]$text) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

# A ContentDialog is a Window inside the shell's tree; its buttons and text box are found under it.
function Get-Dialog($window, [string]$titleLike) {
    Wait-Until {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $_.Current.Name -like $titleLike } |
            Select-Object -First 1
    } 10 "a dialog like '$titleLike' opened"
}

# A sidebar item by name. Typed as a ListItem because the Playlists page title and its list share the name.
function Find-SidebarItem($window, [string]$text) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)),
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))))
}

# After Go to album the sidebar still has Playlists selected (a detail page leaves the selection alone), and selecting
# the selected item navigates nowhere. A click on it does (LibraryPane.OnItemInvoked), but a NavigationViewItem offers
# UIA only SelectionItem and ScrollItem, no Invoke (measured), so the check steps through Albums instead.
function Open-Playlists($window) {
    $item = Wait-Until { Find-SidebarItem $window 'Playlists' } 10 'the sidebar showed Playlists'
    if ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
        Select-Element (Find-SidebarItem $window 'Albums')
        Start-Sleep -Milliseconds 800
    }
    Select-Element (Find-SidebarItem $window 'Playlists')
    Wait-Until { Find-Named $window 'New playlist' } 10 'Library > Playlists opened' | Out-Null
    Start-Sleep -Milliseconds 800
}

# The playlist's row in Library > Playlists, found by its name (the row template's automation name), or null.
function Find-PlaylistRow($window, [string]$text) {
    $rows = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name -eq $text }
    return $rows | Select-Object -First 1
}

function Delete-OpenPlaylist($window) {
    Invoke-Element (Wait-Until { Find-Named $window 'Delete playlist' } 10 'the Delete playlist button appeared')
    $confirm = Get-Dialog $window 'Delete *'
    Invoke-Element (Wait-Until { Find-Named $confirm 'Delete' } 5 'the confirmation offered Delete')
    Start-Sleep -Milliseconds 1500
}

$process = $null
$window = $null
$mutedAtStart = $null
$startedPlayback = $false
$playlistExists = $false

try {
    Write-Output "shell: $Exe"
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds

    $toggle = (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }

    Open-Playlists $window
    # A playlist under one of this check's own two names is what an earlier run failed to delete; it is removed first,
    # so the checks below start from a library without it.
    foreach ($leftover in @($name, $renamed)) {
        $row = Find-PlaylistRow $window $leftover
        if ($row) {
            Invoke-Element $row
            Wait-Until { Find-Id $window 'PlaylistSummary' } 10 "the leftover '$leftover' opened" | Out-Null
            Delete-OpenPlaylist $window
            Write-Output "  note  deleted '$leftover', left by an earlier run"
        }
    }
    if ((Find-PlaylistRow $window $name) -or (Find-PlaylistRow $window $renamed)) { throw "a leftover '$name' playlist could not be deleted" }

    # ---- create ---------------------------------------------------------------------------------------------------
    Invoke-Element (Wait-Until { Find-Named $window 'New playlist' } 10 'the New playlist button appeared')
    $dialog = Get-Dialog $window 'New playlist'
    Set-Text (Wait-Until { Find-Named $dialog 'Playlist name' } 5 'the name box appeared') $name
    Invoke-Element (Wait-Until { Find-Named $dialog 'Create' } 5 'the dialog offered Create')
    $playlistExists = $true
    $summary = Wait-Until { Find-Id $window 'PlaylistSummary' } 10 'the new playlist opened'
    Start-Sleep -Milliseconds 800
    Check 'New playlist makes an empty playlist and opens it' ($null -ne (Find-Named $window $name) -and (Find-Id $window 'PlaylistSummary').Current.Name -eq '0 tracks') "summary '$((Find-Id $window 'PlaylistSummary').Current.Name)'"

    # ---- add an album ---------------------------------------------------------------------------------------------
    # Album detail is reached through Now Playing's album link, which needs a loaded track: the restored queue, or the
    # first album tile played (muted) when there is none.
    if (-not (Find-Id $window 'Scrubber').Current.IsEnabled) {
        Select-Element (Wait-Until { Find-Named $window 'Albums' } 10 'the sidebar showed Albums')
        Start-Sleep -Milliseconds 1200
        $tile = Wait-Until {
            $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.Name -like 'Album * by *' } | Select-Object -First 1
        } 15 'an album tile appeared'
        Invoke-Element $tile
        $startedPlayback = $true
        Wait-Until { (Find-Id $window 'Scrubber').Current.IsEnabled } 10 'a track loaded' | Out-Null
    }
    $albumLink = Wait-Until { $l = Find-Named $window 'Go to album'; if ($l -and $l.Current.IsEnabled) { $l } } 10 'Now Playing offered Go to album'
    Invoke-Element $albumLink
    $more = Wait-Until { Find-Named $window 'More' } 10 'album detail opened'
    $more.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Invoke-Element (Wait-Until { Find-Named $A::RootElement 'Add to playlist' } 5 'the More menu offered Add to playlist')
    $add = Get-Dialog $window 'Add * to a playlist'
    $choice = Wait-Until {
        $add.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name -eq $name } | Select-Object -First 1
    } 5 "the dialog listed '$name'"
    Select-Element $choice
    Start-Sleep -Milliseconds 300
    Invoke-Element (Find-Named $add 'Add')
    Start-Sleep -Milliseconds 1200

    Open-Playlists $window
    $row = Wait-Until { Find-PlaylistRow $window $name } 10 "Library > Playlists listed '$name'"
    Invoke-Element $row
    Start-Sleep -Milliseconds 1200
    $summaryText = (Wait-Until { Find-Id $window 'PlaylistSummary' } 10 'the playlist opened').Current.Name
    Check 'Add to playlist from album detail adds the album, and the totals show it' ($summaryText -match '^\d+ tracks? \u00B7 .+' -and $summaryText -ne '0 tracks') "summary '$summaryText'"

    # ---- rename ---------------------------------------------------------------------------------------------------
    Invoke-Element (Find-Named $window 'Rename playlist')
    $rename = Get-Dialog $window 'Rename playlist'
    Set-Text (Wait-Until { Find-Named $rename 'Playlist name' } 5 'the name box appeared') $renamed
    Invoke-Element (Find-Named $rename 'Rename')
    Start-Sleep -Milliseconds 1200
    Check 'Rename renames it' ($null -ne (Find-Named $window $renamed)) "title '$renamed' shown"
    $name = $renamed

    # ---- delete ---------------------------------------------------------------------------------------------------
    Delete-OpenPlaylist $window
    $playlistExists = $false
    Start-Sleep -Milliseconds 500
    Check 'Delete, confirmed, removes it and returns to the list' ($null -ne (Find-Named $window 'New playlist') -and $null -eq (Find-PlaylistRow $window $renamed)) 'back on Library > Playlists, row gone'

    Write-Output '  note  moving and removing tracks need a drag, the row menu or the Delete key, so they are not exercised here (PlaylistViewModelTests)'
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    if ($window) {
        try {
            if ($playlistExists) {
                Open-Playlists $window
                foreach ($candidate in @('Tunqio check', 'Tunqio check renamed')) {
                    $row = Find-PlaylistRow $window $candidate
                    if ($row) { Invoke-Element $row; Start-Sleep -Milliseconds 1200; Delete-OpenPlaylist $window; Write-Output "cleanup: deleted '$candidate'" }
                }
                if ((Find-PlaylistRow $window 'Tunqio check') -or (Find-PlaylistRow $window 'Tunqio check renamed')) {
                    Write-Output "WARNING: a playlist called 'Tunqio check' (or 'Tunqio check renamed') is still in the library; delete it from Library > Playlists"
                }
            }
            if ($startedPlayback) { $p = Find-Id $window 'PlayPauseButton'; if ($p -and $p.Current.Name -like 'Pause*') { Invoke-Element $p } }
            if ($mutedAtStart -eq $false) { (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        }
        catch { Write-Output "WARNING: cleanup did not finish: $($_.Exception.Message). Check Library > Playlists for 'Tunqio check'." }
        try { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    }
    if ($process -and -not $process.WaitForExit(15000)) { $process.Kill() }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-playlists: PASS'
    exit 0
}
Write-Output "check-playlists: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
