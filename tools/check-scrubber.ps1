<#
.SYNOPSIS
  T-208: the transport scrubber seeks. A click on the track seeks there, a drag scrubs and playback carries on from
  the release point without snapping back, and with the slider focused the arrow keys seek. Phil reported the slider
  showing position only (2026-09-14): Slider handles the pointer inside its own template and marks the events handled,
  so the panel's attribute handlers never ran and no drag ever began.

  This one MOVES THE MOUSE POINTER, clicks and drags with it, and presses arrow keys, because pointer input is the
  thing under test. The pointer is put back where it was at the end. Run it only on a machine nobody is using.

  What a seek looks like from outside: the scrubber's UIA RangeValue (the position in seconds) lands near where the
  pointer went and keeps climbing from there, and the app logs "Scrubber released at N s" from the gesture's end.

  WHAT IT CHANGES. Nothing of the user's. It makes a scratch profile, artifacts\check-scrubber\<stamp>, with one
  album of two-minute tones (ffmpeg) seeded into it, launches the app on it with --data-root, mutes it and plays the
  album. The real %LOCALAPPDATA%\Tunqio is never opened (tools/scratch-profile.ps1, T-197), and the stamp folder is
  deleted at the end unless -KeepScratch.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER KeepScratch
  Leave the scratch profile and its tones behind for inspection.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10,
    # T-196: how long to wait for a Tunqio somebody else started to go away, checking every 30 s, before refusing.
    [int]$WaitMinutes = 10,
    [switch]$KeepScratch
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
. (Join-Path $here 'uia-geometry.ps1')
. (Join-Path $here 'scratch-profile.ps1')
. (Join-Path $here 'assert-fresh-build.ps1')
Assert-FreshBuild -AppDir (Split-Path $Exe)

if (-not ('TunqioMouse' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TunqioMouse {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@
}

if (-not (Wait-TunqioExited -WaitMinutes $WaitMinutes)) {
    throw "Tunqio is still running after $WaitMinutes minute(s). This script drives the instance it launches with the mouse, so it will not touch one somebody is using."
}

$A = [System.Windows.Automation.AutomationElement]
$scratch = New-TunqioScratchProfile -Name 'check-scrubber'
$log = Get-TunqioScratchLog $scratch
$script:failures = @()

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 100
    }
    throw "waited ${seconds}s and $what never happened"
}

function Find-By($scope, $property, [string]$value) {
    $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($property, $value)))
}
function Find-Named($scope, [string]$name) { Find-By $scope $A::NameProperty $name }
function Find-Id($scope, [string]$id) { Find-By $scope $A::AutomationIdProperty $id }
function Get-Selected($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

# The scrubber's position and length, in seconds, as a screen reader sees them.
function Get-Range($slider) { $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current }

# SetCursorPos alone moves the cursor without producing pointer input, and WinUI reads pointer input (check-focus.ps1):
# the cursor is placed a pixel short and an injected relative move carries it the last pixel through the input stack.
function Move-Pointer([double]$x, [double]$y) {
    [TunqioMouse]::SetCursorPos([int]$x - 1, [int]$y) | Out-Null
    [TunqioMouse]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
    return [DateTimeOffset]::Now
}

function Press-Left { [TunqioMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) }
function Release-Left { [TunqioMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }

# A pointer carried along a path rather than teleported, so the slider sees a drag and not a jump.
function Move-PointerPath([double]$fromX, [double]$fromY, [double]$toX, [double]$toY, [int]$steps = 16) {
    for ($i = 1; $i -le $steps; $i++) {
        Move-Pointer ($fromX + ($toX - $fromX) * $i / $steps) ($fromY + ($toY - $fromY) * $i / $steps) | Out-Null
        Start-Sleep -Milliseconds 30
    }
}

# Log lines saying "Scrubber released at N s" after $after, as the seconds they name.
function Get-ReleaseLog([DateTimeOffset]$after) {
    $stream = New-Object System.IO.FileStream($log, 'Open', 'Read', 'ReadWrite')
    try { $content = (New-Object System.IO.StreamReader($stream)).ReadToEnd() }
    finally { $stream.Dispose() }
    $found = @()
    foreach ($line in $content -split "`n") {
        if ($line -notlike '*Scrubber released at*') { continue }
        if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) ' ) {
            $t = [DateTimeOffset]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture)
            if ($t -gt $after -and $line -match 'released at ([\d.,]+) s') { $found += [double]($Matches[1] -replace ',', '.') }
        }
    }
    return , $found
}

# Where along the track a fraction of the length sits. The thumb has a half-width of slack at each end of the track,
# so the usable run is inset by that; 12 px is the Fluent thumb's radius at 100% scaling, scaled with the window.
function Get-TrackX($rect, [double]$fraction, [double]$scale) {
    $inset = 12 * $scale
    return $rect.Left + $inset + ($rect.Width - 2 * $inset) * $fraction
}

$process = $null
$window = $null
$mutedAtStart = $null
$startedPlayback = $false
[TunqioMouse+POINT]$pointerAtStart = New-Object TunqioMouse+POINT
[TunqioMouse]::GetCursorPos([ref]$pointerAtStart) | Out-Null

try {
    Write-Output "shell: $Exe"
    New-TunqioScratchTones $scratch -AlbumCount 1 -TracksPerAlbum 2 -Seconds 120 | Out-Null
    Initialize-TunqioScratchLibrary $Exe $scratch -ExpectTracks 2
    $process = Start-TunqioOnScratch $Exe $scratch
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    Set-UiaWindowSize -ProcessId $process.Id -Width 1400 -Height 900
    $scale = Get-UiaWindowScale -ProcessId $process.Id
    Wait-Until { Find-Named $window 'Discovery mode' } 20 'the mode switcher appeared' | Out-Null
    if (-not (Get-Selected (Find-Named $window 'Discovery mode'))) { Select-Element (Find-Named $window 'Discovery mode'); Start-Sleep -Milliseconds 1200 }

    $toggle = (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }
    $tile = Wait-Until {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'Album * by *' } | Select-Object -First 1
    } 20 'an album tile appeared'
    Write-Output "playing from the start of: $($tile.Current.Name)"
    Invoke-Element $tile
    $playAlbum = $null
    try { $playAlbum = Wait-Until { Find-Named $window 'Play album' } 5 'album detail offered Play album' } catch { }
    if ($playAlbum) { Invoke-Element $playAlbum }
    $slider = Wait-Until { $s = Find-Id $window 'Scrubber'; if ($s -and $s.Current.IsEnabled) { $s } } 10 'a track loaded'
    if ((Find-Id $window 'PlayPauseButton').Current.Name -notlike 'Pause*') { Invoke-Element (Find-Id $window 'PlayPauseButton') }
    $startedPlayback = $true
    Start-Sleep -Seconds 2

    $length = (Get-Range $slider).Maximum
    Check 'the scrubber knows the track length' ($length -ge 100 -and $length -le 130) "length $length s for a 120 s tone"
    $rect = Get-UiaRect $slider
    if ($rect.Offscreen) { throw 'the scrubber has no on-screen rectangle' }
    $y = $rect.Top + $rect.Height / 2
    Assert-UiaForeground -ProcessId $process.Id

    # ---- a click seeks (AC-592) -----------------------------------------------------------------------------------------
    $target = 0.75 * $length
    Move-Pointer (Get-TrackX $rect 0.75 $scale) $y | Out-Null
    Start-Sleep -Milliseconds 200
    $clicked = [DateTimeOffset]::Now
    Press-Left; Start-Sleep -Milliseconds 80; Release-Left
    Start-Sleep -Milliseconds 1500
    $after = (Get-Range $slider).Value
    $released = Get-ReleaseLog $clicked
    Check 'a click on the track seeks there' ($released.Count -ge 1 -and [math]::Abs($after - $target) -le 8) "clicked at 75% ($([math]::Round($target)) s); position read $([math]::Round($after, 1)) s 1.5 s later; app logged release at $($released -join ', ') s"
    Start-Sleep -Seconds 2
    $later = (Get-Range $slider).Value
    Check 'playback carries on from the click' ($later -gt $after + 1 -and [math]::Abs($later - $target) -le 12) "position $([math]::Round($later, 1)) s two seconds on"

    # ---- a drag scrubs and does not snap back (AC-593) -----------------------------------------------------------------
    $from = Get-TrackX $rect 0.20 $scale
    $to = Get-TrackX $rect 0.45 $scale
    $dragTarget = 0.45 * $length
    Move-Pointer $from $y | Out-Null
    Start-Sleep -Milliseconds 200
    $dragged = [DateTimeOffset]::Now
    Press-Left
    Start-Sleep -Milliseconds 120
    Move-PointerPath $from $y $to $y
    Start-Sleep -Milliseconds 300
    $duringDrag = (Get-Range $slider).Value
    Release-Left
    Start-Sleep -Milliseconds 1500
    $afterDrag = (Get-Range $slider).Value
    $dragReleased = Get-ReleaseLog $dragged
    Check 'while dragging the thumb follows the pointer' ([math]::Abs($duringDrag - $dragTarget) -le 8) "read $([math]::Round($duringDrag, 1)) s with the pointer held at 45% ($([math]::Round($dragTarget)) s)"
    Check 'releasing the drag seeks there and does not snap back' ($dragReleased.Count -ge 1 -and [math]::Abs($afterDrag - $dragTarget) -le 8) "position $([math]::Round($afterDrag, 1)) s 1.5 s after release; app logged release at $($dragReleased -join ', ') s"
    Start-Sleep -Seconds 2
    $stillThere = (Get-Range $slider).Value
    Check 'playback carries on from the release point' ($stillThere -gt $afterDrag + 1 -and [math]::Abs($stillThere - $dragTarget) -le 12) "position $([math]::Round($stillThere, 1)) s two seconds on"

    # ---- the arrow keys on the focused slider (AC-594) ------------------------------------------------------------------
    # The click above left keyboard focus on the slider; Right moves the thumb a second a press and the seek is committed
    # when the key comes up.
    $before = (Get-Range $slider).Value
    $keyed = [DateTimeOffset]::Now
    Assert-UiaForeground -ProcessId $process.Id
    [System.Windows.Forms.SendKeys]::SendWait('{RIGHT 10}')
    Start-Sleep -Milliseconds 1500
    $afterKeys = (Get-Range $slider).Value
    $focusedName = $A::FocusedElement.Current.AutomationId
    Check 'Right on the focused slider seeks forward' ($focusedName -eq 'Scrubber' -and $afterKeys -ge $before + 8 -and $afterKeys -le $before + 16) "focus on '$focusedName'; $([math]::Round($before, 1)) s to $([math]::Round($afterKeys, 1)) s after ten presses"
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    [TunqioMouse]::SetCursorPos($pointerAtStart.X, $pointerAtStart.Y) | Out-Null
    if ($window) {
        try {
            if ($startedPlayback -and (Find-Id $window 'PlayPauseButton').Current.Name -like 'Pause*') { Invoke-Element (Find-Id $window 'PlayPauseButton') }
            if ($mutedAtStart -eq $false) { (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        }
        catch { Write-Output "note: could not restore play or mute: $($_.Exception.Message)" }
    }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $window 15
    if ($closeProblem) { $script:failures += $closeProblem }
}

Remove-TunqioScratchProfile $scratch -Keep:$KeepScratch

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-scrubber: PASS'
    exit 0
}
Write-Output "check-scrubber: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
