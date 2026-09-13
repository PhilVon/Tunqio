<#
.SYNOPSIS
  E6-S2: M3U8 auto-export in a running shell, UIA only. Makes a playlist in Library > Playlists, adds an album to it
  from album detail's More > Add to playlist, and checks the export in exports\playlists appears within 5 s of the
  change, lists the album's tracks as files that exist, follows a rename (new file, old one gone) and goes when the
  playlist is deleted. It also checks the playlist page offers Export playlist and Settings > Library offers the three
  playlist actions. The change-to-file times are read from the app log after it exits.

  UIA patterns only: no keystrokes and no pointer. The Export buttons open the system save picker, which this check does
  not drive; exporting through it and opening the file in another player is a human criterion on the board.

  WHAT IT CHANGES. A playlist called "Tunqio export check" (renamed "Tunqio export check renamed") for the length of
  the run, deleted at the end through the app, which also removes its export. A later run deletes one left under either
  name before it starts, and touches no other playlist. Starting the app also brings every other playlist's export up to
  date, which is what the shipped app does on every launch. Nothing is played unless the restored queue is empty, and
  then the app is muted.
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
$TS = [System.Windows.Automation.TreeScope]
$name = 'Tunqio export check'
$renamed = 'Tunqio export check renamed'
$exports = Join-Path $env:LOCALAPPDATA 'Tunqio\exports\playlists'
$logDir = Join-Path $env:LOCALAPPDATA 'Tunqio\logs'
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
# Names with an ellipsis are matched by wildcard: a non-ASCII literal does not survive Windows PowerShell 5.1 -File.
function Find-Like($scope, [string]$pattern) {
    $scope.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -like $pattern } | Select-Object -First 1
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
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

function Find-SidebarItem($window, [string]$text) {
    $window.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)),
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))))
}

# Selecting the item already selected navigates nowhere (see check-playlists.ps1), so step through Albums first.
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

function Find-PlaylistRow($window, [string]$text) {
    $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name -eq $text } |
        Select-Object -First 1
}

function Delete-OpenPlaylist($window) {
    Invoke-Element (Wait-Until { Find-Named $window 'Delete playlist' } 10 'the Delete playlist button appeared')
    $confirm = Get-Dialog $window 'Delete *'
    Invoke-Element (Wait-Until { Find-Named $confirm 'Delete' } 5 'the confirmation offered Delete')
    Start-Sleep -Milliseconds 1500
}

function Export-PathFor([string]$playlist) { Join-Path $exports ($playlist + '.m3u8') }

$process = $null
$window = $null
$mutedAtStart = $null
$startedPlayback = $false
$playlistExists = $false
$reachedEnd = $false
$startedAt = Get-Date

try {
    Write-Output "shell: $Exe"
    Write-Output "exports: $exports"
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds

    $toggle = (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }

    Open-Playlists $window
    foreach ($leftover in @($name, $renamed)) {
        $row = Find-PlaylistRow $window $leftover
        if ($row) {
            Invoke-Element $row
            Wait-Until { Find-Id $window 'PlaylistSummary' } 10 "the leftover '$leftover' opened" | Out-Null
            Delete-OpenPlaylist $window
            Write-Output "  note  deleted '$leftover', left by an earlier run"
        }
    }
    Start-Sleep -Seconds 5 # the leftovers' exports go with them, inside the window
    Check 'No export is left for this check''s names before it starts' (-not (Test-Path (Export-PathFor $name)) -and -not (Test-Path (Export-PathFor $renamed))) $exports

    # ---- create -----------------------------------------------------------------------------------------------------
    Invoke-Element (Wait-Until { Find-Named $window 'New playlist' } 10 'the New playlist button appeared')
    $dialog = Get-Dialog $window 'New playlist'
    Set-Text (Wait-Until { Find-Named $dialog 'Playlist name' } 5 'the name box appeared') $name
    Invoke-Element (Wait-Until { Find-Named $dialog 'Create' } 5 'the dialog offered Create')
    $playlistExists = $true
    Wait-Until { Find-Id $window 'PlaylistSummary' } 10 'the new playlist opened' | Out-Null
    $createdAt = Get-Date
    $file = Wait-Until { if (Test-Path (Export-PathFor $name)) { Export-PathFor $name } } 8 'the new playlist was exported'
    Check 'A new playlist is exported within 5 s' (((Get-Date) - $createdAt).TotalSeconds -le 6) "seen after $([int]((Get-Date) - $createdAt).TotalMilliseconds) ms, polled every 200 ms; the log below gives the exact figure"
    Check 'The playlist page offers Export playlist' ($null -ne (Find-Named $window 'Export playlist')) 'button present (the save picker it opens is not driven here)'

    # ---- add an album -------------------------------------------------------------------------------------------------
    if (-not (Find-Id $window 'Scrubber').Current.IsEnabled) {
        Select-Element (Wait-Until { Find-Named $window 'Albums' } 10 'the sidebar showed Albums')
        Start-Sleep -Milliseconds 1200
        $tile = Wait-Until {
            $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.Name -like 'Album * by *' } | Select-Object -First 1
        } 15 'an album tile appeared'
        Invoke-Element $tile
        $startedPlayback = $true
        Wait-Until { (Find-Id $window 'Scrubber').Current.IsEnabled } 10 'a track loaded' | Out-Null
    }
    Invoke-Element (Wait-Until { $l = Find-Named $window 'Go to album'; if ($l -and $l.Current.IsEnabled) { $l } } 10 'Now Playing offered Go to album')
    $more = Wait-Until { Find-Named $window 'More' } 10 'album detail opened'
    $more.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Invoke-Element (Wait-Until { Find-Named $A::RootElement 'Add to playlist' } 5 'the More menu offered Add to playlist')
    $add = Get-Dialog $window 'Add * to a playlist'
    $choice = Wait-Until {
        $add.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name -eq $name } | Select-Object -First 1
    } 5 "the dialog listed '$name'"
    Select-Element $choice
    Start-Sleep -Milliseconds 300
    Invoke-Element (Find-Named $add 'Add')
    $addedAt = Get-Date
    $tracks = Wait-Until {
        $paths = @(Get-Content $file -ErrorAction SilentlyContinue | Where-Object { $_ -and -not $_.StartsWith('#') })
        if ($paths.Count -gt 0) { , $paths }
    } 8 'the added album reached the export'
    Check 'Adding an album re-exports the playlist within 5 s' (((Get-Date) - $addedAt).TotalSeconds -le 6) "$($tracks.Count) track line(s) after $([int]((Get-Date) - $addedAt).TotalMilliseconds) ms"
    $missing = @($tracks | Where-Object { -not (Test-Path -LiteralPath $_) })
    $infos = @(Get-Content $file | Where-Object { $_ -like '#EXTINF:*' }).Count
    Check 'Every exported entry is a file that exists, with an #EXTINF line' ($missing.Count -eq 0 -and $infos -eq $tracks.Count -and (Get-Content $file -TotalCount 1) -eq '#EXTM3U') "$($tracks.Count) paths, $($missing.Count) missing, $infos #EXTINF"

    # ---- rename -------------------------------------------------------------------------------------------------------
    Open-Playlists $window
    Invoke-Element (Wait-Until { Find-PlaylistRow $window $name } 10 "Library > Playlists listed '$name'")
    Wait-Until { Find-Id $window 'PlaylistSummary' } 10 'the playlist opened' | Out-Null
    Invoke-Element (Wait-Until { Find-Named $window 'Rename playlist' } 5 'the page offered Rename')
    $rename = Get-Dialog $window 'Rename playlist'
    Set-Text (Wait-Until { Find-Named $rename 'Playlist name' } 5 'the name box appeared') $renamed
    Invoke-Element (Find-Named $rename 'Rename')
    $name = $renamed
    Wait-Until { (Test-Path (Export-PathFor $renamed)) -and -not (Test-Path (Export-PathFor 'Tunqio export check')) } 8 'the rename moved the export' | Out-Null
    Check 'A rename writes the new name and removes the old file' $true "$(Export-PathFor $renamed)"

    # ---- delete -------------------------------------------------------------------------------------------------------
    Delete-OpenPlaylist $window
    $playlistExists = $false
    Wait-Until { -not (Test-Path (Export-PathFor $renamed)) } 8 'the deleted playlist''s export went' | Out-Null
    Check 'Deleting the playlist removes its export' $true 'file gone'

    # ---- Settings > Library -------------------------------------------------------------------------------------------
    $settings = Wait-Until { Find-SidebarItem $window 'Settings' } 10 'the sidebar showed Settings'
    Select-Element $settings
    Wait-Until { Find-Named $window 'Library settings' } 10 'Settings > Library opened' | Out-Null
    Start-Sleep -Milliseconds 800
    $offered = @(
        (Find-Named $window 'Import playlists from exports'),
        (Find-Like $window 'Import playlist files*'),
        (Find-Like $window 'Export playlists*')) | Where-Object { $null -ne $_ }
    Check 'Settings > Library offers import from exports, import files and export' ($offered.Count -eq 3) "$($offered.Count) of 3 buttons found"
    $reachedEnd = $true
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
                foreach ($candidate in @('Tunqio export check', 'Tunqio export check renamed')) {
                    $row = Find-PlaylistRow $window $candidate
                    if ($row) { Invoke-Element $row; Start-Sleep -Milliseconds 1200; Delete-OpenPlaylist $window; Write-Output "cleanup: deleted '$candidate'" }
                }
            }
            if ($startedPlayback) { $p = Find-Id $window 'PlayPauseButton'; if ($p -and $p.Current.Name -like 'Pause*') { Invoke-Element $p } }
            if ($mutedAtStart -eq $false) { (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        }
        catch { Write-Output "WARNING: cleanup did not finish: $($_.Exception.Message). Check Library > Playlists for 'Tunqio export check'." }
        try { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    }
    if ($process -and -not $process.WaitForExit(15000)) { $process.Kill() }
}

# ---- change-to-file times, from the log --------------------------------------------------------------------------------
# Read after the app has exited: the file sink buffers. Each "Auto-exported playlist N" is paired with the earliest
# "Playlist N changed" since that playlist's previous export: the change that opened the window.
if ($reachedEnd) {
    $log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
    $pattern = '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \S+ \[DBG\] \[[^\]]*\] Tunqio\.Library\.Playlists\.PlaylistFiles: (Playlist (\d+) changed.*|Auto-exported playlist (\d+) .* to (.+)|Removed the auto-export of playlist (\d+) at (.+))$'
    $opened = @{}
    $worst = 0
    $pairs = 0
    foreach ($text in @(if ($log) { Get-Content $log.FullName })) {
        if ($text -notmatch $pattern) { continue }
        $at = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
        if ($at -lt $startedAt) { continue }
        if ($Matches[3]) { if (-not $opened.ContainsKey($Matches[3])) { $opened[$Matches[3]] = $at } }
        elseif ($Matches[4] -and $Matches[5] -like '*Tunqio export check*') {
            if ($opened.ContainsKey($Matches[4])) {
                $ms = ($at - $opened[$Matches[4]]).TotalMilliseconds
                $pairs++
                if ($ms -gt $worst) { $worst = $ms }
                $opened.Remove($Matches[4])
            }
        }
    }
    Check 'The log times every change of the check playlist to its export at 5 s or less' ($pairs -gt 0 -and $worst -le 5000) "$pairs change-to-export pair(s), slowest $([int]$worst) ms$(if (-not $log) { ", no log in $logDir" })"
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-playlist-files: PASS'
    exit 0
}
Write-Output "check-playlist-files: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
