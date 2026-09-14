<#
.SYNOPSIS
  E5-S1: the three modes in a running shell. The switcher changes the mode and shows it, Focus gives Now Playing the
  whole width and hides the sidebar, Curation gives the library side the larger share (Q-67), leaving Focus shows the
  sidebar page that was there, playback keeps going across every switch, and ui.mode is written and read back by a
  relaunch.

  Keystroke-free by default: every action is a UIA pattern (SelectionItem, Invoke, Toggle, Window), so nothing typed
  can land in another window. -Keys adds Ctrl+1/2/3, F11 and Esc pressed at the window, each only after the shell is
  confirmed to hold the foreground; use it only on a machine nobody is typing on.

  WHAT IT CHANGES. It launches the app over the user's own library, mutes it, and switches modes, which writes
  ui.mode. When nothing is loaded it plays the first album tile, which replaces the saved queue with that album. At the end it pauses, restores the mute state it found, and leaves the app
  in Discovery, which is also the default ui.mode.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER Seconds
  How long to give the window to settle after launch.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10,
    # Also press Ctrl+1/2/3, F11 and Esc at the window. Off by default: keystrokes go to whatever holds the
    # foreground, so this is for a machine nobody is using (T-163's refusal guards each key regardless).
    [switch]$Keys,
    # T-196: how long to wait for a Tunqio somebody else started to go away, checking every 30 s, before refusing.
    [int]$WaitMinutes = 10
)

$ErrorActionPreference = 'Stop'
# Not $PSScriptRoot in the param default: under powershell.exe -File that default resolved against the drive root.
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'uia-geometry.ps1') # Wait-TunqioExited (T-196)

if (-not (Wait-TunqioExited -WaitMinutes $WaitMinutes)) {
    throw "Tunqio is still running after $WaitMinutes minute(s). This script switches the mode of the instance it launches and plays through it, so it will not touch one somebody is using."
}

$A = [System.Windows.Automation.AutomationElement]
$settingsPath = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
$script:failures = @()

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "waited ${seconds}s and $what never happened"
}

function Find-By($scope, $property, [string]$value) {
    $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($property, $value)))
}

function Find-Named($scope, [string]$name) { Find-By $scope $A::NameProperty $name }
function Find-Id($scope, [string]$id) { Find-By $scope $A::AutomationIdProperty $id }

function Get-Selected($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
}

function Select-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Get-Position($scrubber) {
    $scrubber.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
}

function Get-StoredMode {
    for ($i = 0; $i -lt 10; $i++) {
        try { return ((Get-Content $settingsPath -Raw | ConvertFrom-Json).'ui.mode') }
        catch { Start-Sleep -Milliseconds 200 }
    }
    return $null
}

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

function Start-Shell {
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    Wait-Until { Find-Named $window 'Discovery mode' } 20 'the mode switcher appeared' | Out-Null
    return @{ Process = $process; Window = $window }
}

function Stop-Shell($shell) {
    if (-not $shell) { return }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    . (Join-Path $PSScriptRoot 'uia-geometry.ps1')
    $closeProblem = Close-TunqioShell $shell.Process $shell.Window 15
    if ($closeProblem) { $script:failures += $closeProblem }
}

function Select-Mode($window, [string]$mode) {
    Select-Element (Find-Named $window "$mode mode")
    Start-Sleep -Milliseconds 1200
}

$shell = $null
$mutedAtStart = $null
$startedPlayback = $false
try {
    Write-Output "shell: $Exe"
    $shell = Start-Shell
    $window = $shell.Window
    if (-not (Get-Selected (Find-Named $window 'Discovery mode'))) { Select-Mode $window 'Discovery' }

    $client = $window.Current.BoundingRectangle.Width
    $controls = Find-Named $window 'Playback controls panel'
    $discoveryWidth = $controls.Current.BoundingRectangle.Width
    Write-Output "window $([math]::Round($client)) px; controls bar in Discovery $([math]::Round($discoveryWidth)) px"

    # Playback, muted, so the check is heard by nobody.
    $mute = Find-Id $window 'MuteButton'
    $toggle = $mute.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }
    $playPause = Find-Id $window 'PlayPauseButton'
    $scrubber = Find-Id $window 'Scrubber'
    $playing = $false
    try {
        # The play button is enabled whenever there is a session, loaded track or not (IsReady, not HasTrack), so
        # the scrubber is what says a track is loaded. The first run pressed Play on an empty queue and waited.
        if (-not $scrubber.Current.IsEnabled) {
            $tile = Wait-Until {
                $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                    Where-Object { $_.Current.Name -like 'Album * by *' } | Select-Object -First 1
            } 20 'an album tile appeared'
            Write-Output "loading a track from: $($tile.Current.Name)"
            $tile.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1500
            # A tile may open album detail rather than play; that page has a Play album button.
            if (-not (Find-Id $window 'Scrubber').Current.IsEnabled) {
                $playAlbum = Wait-Until { Find-Named $window 'Play album' } 10 'album detail offered Play album'
                $playAlbum.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                $startedPlayback = $true
            }
            Wait-Until { (Find-Id $window 'Scrubber').Current.IsEnabled } 10 'a track loaded' | Out-Null
        }

        $playPause = Find-Id $window 'PlayPauseButton'
        if ($playPause.Current.Name -notlike 'Pause*') {
            $playPause.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $startedPlayback = $true
        }
        $scrubber = Find-Id $window 'Scrubber'
        $before = Get-Position $scrubber
        $playing = [bool](Wait-Until { (Get-Position $scrubber) -gt $before } 10 'the position advanced')
        $startedPlayback = $true
    }
    catch {
        Write-Output "  note  playback did not start: $($_.Exception.Message)"
    }
    if (-not $playing) {
        $script:failures += 'playback'
        Write-Output '  FAIL  nothing could be played, so AC-134 is not shown'
    }

    # A sidebar page that is not the default, so "not reset" means something.
    $artists = Wait-Until { Find-Named $window 'Artists' } 20 'the sidebar showed its Artists view'
    Select-Element $artists
    Start-Sleep -Milliseconds 800
    Check 'the sidebar is on Artists before any switch' (Get-Selected (Find-Named $window 'Artists')) 'selected'

    foreach ($step in @(
            @{ Mode = 'Focus'; SidebarShown = $false },
            @{ Mode = 'Curation'; SidebarShown = $true },
            @{ Mode = 'Discovery'; SidebarShown = $true })) {
        $position = if ($playing) { Get-Position $scrubber } else { 0 }
        Select-Mode $window $step.Mode
        Check "$($step.Mode) is the selected mode" (Get-Selected (Find-Named $window "$($step.Mode) mode")) 'switcher'
        Check "ui.mode says $($step.Mode.ToLowerInvariant())" ((Get-StoredMode) -eq $step.Mode.ToLowerInvariant()) "stored '$(Get-StoredMode)'"

        $width = (Find-Named $window 'Playback controls panel').Current.BoundingRectangle.Width
        $artistsNow = Find-Named $window 'Artists'
        switch ($step.Mode) {
            'Focus' {
                Check 'Focus gives Now Playing the whole width' ($width -ge $client * 0.9) "controls bar $([math]::Round($width)) of $([math]::Round($client)) px"
                Check 'Focus hides the sidebar' ($null -eq $artistsNow -or $artistsNow.Current.BoundingRectangle.Width -eq 0) 'the Artists view is not on screen'
            }
            'Curation' {
                Check 'Curation gives the library side the larger share' ($width -lt $discoveryWidth - 20) "controls bar $([math]::Round($width)) px against Discovery's $([math]::Round($discoveryWidth))"
                Check 'leaving Focus shows the sidebar page that was there' ($artistsNow -and (Get-Selected $artistsNow)) 'Artists still selected'
            }
            'Discovery' {
                Check 'Discovery is back to its shares' ([math]::Abs($width - $discoveryWidth) -le 4) "controls bar $([math]::Round($width)) px, $([math]::Round($discoveryWidth)) before"
                Check 'the sidebar page survived every switch' ($artistsNow -and (Get-Selected $artistsNow)) 'Artists still selected'
            }
        }

        if ($playing) {
            $after = Get-Position $scrubber
            Check "playback kept going into $($step.Mode)" ($after -gt $position -and (Find-Id $window 'PlayPauseButton').Current.Name -like 'Pause*') "position $position -> $after s"
        }
    }

    # AC-408: a relaunch opens in the mode the app was closed in.
    Select-Mode $window 'Curation'
    if ($startedPlayback) { (Find-Id $window 'PlayPauseButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $startedPlayback = $false }
    if (-not $mutedAtStart) { (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); $mutedAtStart = $true }
    Stop-Shell $shell
    $shell = Start-Shell
    Check 'a relaunch opens in the mode the app was closed in' (Get-Selected (Find-Named $shell.Window 'Curation mode')) 'Curation selected after relaunch'
    if ($Keys) {
        # The keys themselves (AC-410, AC-135), on the relaunched window, which opens in Curation. Each key is sent
        # only once the shell is confirmed to hold the foreground, so none can land in another window.
        . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'uia-geometry.ps1')
        Add-Type -AssemblyName System.Windows.Forms
        $keyWindow = $shell.Window
        $keyPid = $shell.Process.Id
        $keyClient = $keyWindow.Current.BoundingRectangle.Width
        $curationWidth = (Find-Named $keyWindow 'Playback controls panel').Current.BoundingRectangle.Width

        foreach ($press in @(
                @{ Keys = '^1'; Label = 'Ctrl+1'; Want = 'Discovery' },
                @{ Keys = '^3'; Label = 'Ctrl+3'; Want = 'Curation' },
                @{ Keys = '{F11}'; Label = 'F11 from Curation'; Want = 'Focus' },
                @{ Keys = '{ESC}'; Label = 'Esc from Focus'; Want = 'Curation'; RestoredWidth = $curationWidth },
                @{ Keys = '^2'; Label = 'Ctrl+2'; Want = 'Focus' },
                @{ Keys = '{F11}'; Label = 'F11 from Focus'; Want = 'Curation' },
                @{ Keys = '{ESC}'; Label = 'Esc outside Focus'; Want = 'Curation' })) {
            Assert-UiaForeground -ProcessId $keyPid
            [System.Windows.Forms.SendKeys]::SendWait($press.Keys)
            Start-Sleep -Milliseconds 1200
            $selected = Get-Selected (Find-Named $keyWindow "$($press.Want) mode")
            Check "$($press.Label) leaves the shell in $($press.Want)" $selected 'switcher'
            $width = (Find-Named $keyWindow 'Playback controls panel').Current.BoundingRectangle.Width
            if ($press.Want -eq 'Focus') {
                Check "$($press.Label) gives Now Playing the whole width" ($width -ge $keyClient * 0.9) "controls bar $([math]::Round($width)) of $([math]::Round($keyClient)) px"
            }
            if ($press.ContainsKey('RestoredWidth')) {
                Check 'Esc restores the layout of the mode Focus was entered from' ([math]::Abs($width - $press.RestoredWidth) -le 4) "controls bar $([math]::Round($width)) px, $([math]::Round($press.RestoredWidth)) before Focus"
            }
        }
    }

    Select-Mode $shell.Window 'Discovery'
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    if ($shell) {
        try {
            if ($startedPlayback) { (Find-Id $shell.Window 'PlayPauseButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
            if ($mutedAtStart -eq $false) { (Find-Id $shell.Window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        }
        catch { Write-Output "note: could not restore play or mute state: $($_.Exception.Message)" }
        Stop-Shell $shell
    }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-modes: PASS'
    exit 0
}
Write-Output "check-modes: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
