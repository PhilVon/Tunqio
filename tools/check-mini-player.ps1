<#
.SYNOPSIS
  E5-S6: the mini player in a running shell. The Mini player button opens a window of about 360 x 120 with the main
  window hidden; Keep on top starts on; its transport reaches the session; moved near a corner of the screen it snaps
  into the corner; and Return to main window closes it and brings the main window back.

  UIA only: no keystrokes and no pointer movement. The move is the window's own TransformPattern, which is what makes
  the check possible without taking the mouse; if the window does not offer it, the snap is reported as not shown.

  WHAT IT CHANGES. It launches the app over the user's library, muted, and presses Play in the mini player, which
  plays the restored queue. It pauses and unmutes before closing.
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
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
. (Join-Path $here 'uia-geometry.ps1') # Close-TunqioShell (T-188)

if (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Tunqio is already running. This script opens the mini player of the instance it launches, so it will not touch one somebody is using.'
}

$A = [System.Windows.Automation.AutomationElement]
$log = Join-Path $env:LOCALAPPDATA ('Tunqio\logs\tunqio-' + (Get-Date -Format 'yyyyMMdd') + '.log')
$script:failures = @()

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    }
    throw "waited ${seconds}s and $what never happened"
}

function Find-By($scope, $property, [string]$value) {
    $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($property, $value)))
}
function Find-Named($scope, [string]$name) { Find-By $scope $A::NameProperty $name }
function Find-Id($scope, [string]$id) { Find-By $scope $A::AutomationIdProperty $id }
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

# The app's top-level windows, by title, as the desktop sees them: a hidden window is not among the root's children.
function Get-TopWindows([int]$processId) {
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId)
    @($A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $byPid))
}

function Get-LogTimes([string]$text, [DateTimeOffset]$after) {
    $stream = New-Object System.IO.FileStream($log, 'Open', 'Read', 'ReadWrite')
    try { $content = (New-Object System.IO.StreamReader($stream)).ReadToEnd() }
    finally { $stream.Dispose() }
    $times = @()
    foreach ($line in $content -split "`n") {
        if ($line -notlike "*$text*") { continue }
        if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) ') {
            $t = [DateTimeOffset]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture)
            if ($t -gt $after) { $times += $t }
        }
    }
    return , $times
}

$process = $null
$main = $null
$mini = $null
$mutedAtStart = $null
$played = $false

try {
    Write-Output "shell: $Exe"
    $process = Start-Process $Exe -PassThru
    $main = Wait-Until { Get-TopWindows $process.Id | Where-Object { $_.Current.Name -notlike '*mini player*' } | Select-Object -First 1 } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    $mainTitle = $main.Current.Name

    $toggle = (Find-Id $main 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }

    # ---- opening (AC-423) ----------------------------------------------------------------------------------------
    $opened = [DateTimeOffset]::Now
    Invoke-Element (Wait-Until { Find-Named $main 'Mini player' } 10 'the Mini player button appeared')
    $mini = Wait-Until { Get-TopWindows $process.Id | Where-Object { $_.Current.Name -eq 'Tunqio mini player' } | Select-Object -First 1 } 10 'the mini player window appeared'
    Start-Sleep -Milliseconds 800
    $r = $mini.Current.BoundingRectangle
    $scale = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width / [System.Windows.Forms.SystemInformation]::PrimaryMonitorSize.Width
    Check 'the Mini player button opens the mini player' ($null -ne $mini) "window '$($mini.Current.Name)'"
    Check 'it is about 360 x 120' ($r.Width -ge 300 -and $r.Width -le 1100 -and $r.Height -ge 100 -and $r.Height -le 380 -and [math]::Abs($r.Width / $r.Height - 3) -le 0.35) "$([math]::Round($r.Width)) x $([math]::Round($r.Height)) px"
    $mainVisible = @(Get-TopWindows $process.Id | Where-Object { $_.Current.Name -eq $mainTitle }).Count -gt 0
    Check 'the main window is hidden while it is open' (-not $mainVisible) "main window among the desktop's windows: $mainVisible"
    Check 'the mini player shows what is playing' ($null -ne (Find-Named $mini 'Title') -and $null -ne (Find-Named $mini 'Track progress')) 'title and progress present'
    Check 'the open is logged' ((Get-LogTimes 'Mini player opened' $opened).Count -ge 1) 'log line present'

    # ---- keep on top (AC-424) -----------------------------------------------------------------------------------
    $pin = Find-Named $mini 'Keep on top'
    $pinState = $pin.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Check 'Keep on top starts on' ($pinState -eq [System.Windows.Automation.ToggleState]::On) "toggle $pinState"

    # ---- its transport reaches the session (AC-143) ---------------------------------------------------------------
    $playButton = Find-Named $mini 'Play'
    if ($playButton -and $playButton.Current.IsEnabled) {
        Invoke-Element $playButton
        $played = $true
        $pause = Wait-Until { Find-Named $mini 'Pause' } 8 'the mini player button read Pause'
        Check 'Play in the mini player plays, and the button says so' ($null -ne $pause) 'button renamed Pause'
    }
    else {
        Write-Output '  note  the restored queue is empty, so Play has nothing to play; transport not shown'
    }

    # ---- the snap (AC-425) ----------------------------------------------------------------------------------------
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $transform = $null
    if ($mini.TryGetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern, [ref]$transform) -and $transform.Current.CanMove) {
        $moved = [DateTimeOffset]::Now
        # Near the bottom-right corner but not in it: 30 px short on both axes.
        $transform.Move($work.Right - $r.Width - 40, $work.Bottom - $r.Height - 40)
        Start-Sleep -Milliseconds 1200
        $snaps = Get-LogTimes 'Mini player snapped' $moved
        $after = $mini.Current.BoundingRectangle
        Check 'moved near a corner, it snaps into it' ($snaps.Count -ge 1 -and [math]::Abs($work.Right - $after.Right) -le 40 -and [math]::Abs($work.Bottom - $after.Bottom) -le 40) "$($snaps.Count) snap logged; right edge $([math]::Round($work.Right - $after.Right)) px and bottom edge $([math]::Round($work.Bottom - $after.Bottom)) px in from the work area"
    }
    else {
        $script:failures += 'snap not shown'
        Write-Output '  FAIL  the mini player window does not offer a UIA move, so the snap could not be shown without the mouse'
    }

    # ---- return (AC-426) ------------------------------------------------------------------------------------------
    if ($played) { Invoke-Element (Find-Named $mini 'Pause'); $played = $false; Start-Sleep -Milliseconds 400 }
    $returned = [DateTimeOffset]::Now
    Invoke-Element (Find-Named $mini 'Return to main window')
    $back = Wait-Until { Get-TopWindows $process.Id | Where-Object { $_.Current.Name -eq $mainTitle } | Select-Object -First 1 } 10 'the main window came back'
    Start-Sleep -Milliseconds 500
    $miniGone = @(Get-TopWindows $process.Id | Where-Object { $_.Current.Name -eq 'Tunqio mini player' }).Count -eq 0
    Check 'Return to main window closes the mini player and shows the main window' ($null -ne $back -and $miniGone) "main back: $($null -ne $back); mini gone: $miniGone"
    Check 'the return is logged' ((Get-LogTimes 'Mini player closed; main window shown' $returned).Count -ge 1) 'log line present'
    $main = $back
    $mini = $null
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    try {
        if ($mini) {
            if ($played) { $p = Find-Named $mini 'Pause'; if ($p) { Invoke-Element $p } }
            $ret = Find-Named $mini 'Return to main window'; if ($ret) { Invoke-Element $ret; Start-Sleep -Milliseconds 800 }
        }
        if ($process -and -not $process.HasExited) {
            $main = Get-TopWindows $process.Id | Where-Object { $_.Current.Name -notlike '*mini player*' } | Select-Object -First 1
            if ($main -and $mutedAtStart -eq $false) {
                $m = Find-Id $main 'MuteButton'
                if ($m) { $m.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
            }
        }
    }
    catch { Write-Output "note: could not restore play or mute: $($_.Exception.Message)" }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $main 15
    if ($closeProblem) { $script:failures += $closeProblem }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-mini-player: PASS'
    exit 0
}
Write-Output "check-mini-player: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
