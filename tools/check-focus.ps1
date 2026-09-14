<#
.SYNOPSIS
  E5-S2: Focus mode in a running shell. Double-clicking the art enters Focus; the controls hide after 3 s without
  input and come back within 100 ms of a pointer move (AC-136); they stay while the pointer is over them; the left
  edge peeks the queue; and the track announcer is a polite live region that speaks on a track change (AC-137).

  This one MOVES THE MOUSE POINTER and clicks with it, because pointer movement is the input under test. The pointer
  is put back where it was at the end. -Keys also presses Tab, to show that keyboard focus inside the hidden bar holds
  it on screen. Run it only on a machine nobody is using.

  Timing is read from the app's own log ("Focus controls hidden" / "shown"), stamped in the same process that
  applied the change, against the moment this script moved the pointer.

  WHAT IT CHANGES. It launches the app over the user's library, mutes it, plays the first album tile when nothing is
  loaded (which replaces the saved queue), skips one track, and leaves the app in Discovery with the mute state it
  found.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER Keys
  Also press Tab into the controls bar.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10,
    [switch]$Keys
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
. (Join-Path $here 'uia-geometry.ps1')

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

if (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Tunqio is already running. This script drives the instance it launches with the mouse, so it will not touch one somebody is using.'
}

$A = [System.Windows.Automation.AutomationElement]
$log = Join-Path $env:LOCALAPPDATA ('Tunqio\logs\tunqio-' + (Get-Date -Format 'yyyyMMdd') + '.log')
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

# The art Image is AccessibilityView Raw, so only the raw view has it.
function Find-RawId($root, [string]$id) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)
    $seen = 0
    while ($stack.Count -gt 0 -and $seen -lt 6000) {
        $element = $stack.Pop()
        $seen++
        if ($element.Current.AutomationId -eq $id) { return $element }
        $child = $walker.GetFirstChild($element)
        while ($child) { $stack.Push($child); $child = $walker.GetNextSibling($child) }
    }
    return $null
}

function Get-Selected($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
}
function Select-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Invoke-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

function Move-Pointer([double]$x, [double]$y) {
    # SetCursorPos alone moves the cursor without producing pointer input, and WinUI reads pointer input: in the second
    # run not one of these moves reached the window, while the mouse_event double-click did. So the cursor is placed a
    # pixel short and an injected relative move carries it the last pixel, through the input stack.
    [TunqioMouse]::SetCursorPos([int]$x - 1, [int]$y) | Out-Null
    [TunqioMouse]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
    return [DateTimeOffset]::Now
}

function Invoke-DoubleClick([double]$x, [double]$y) {
    Move-Pointer $x $y | Out-Null
    Start-Sleep -Milliseconds 150
    foreach ($i in 1..2) {
        [TunqioMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [TunqioMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
}

# Times of log lines containing $text, after $after. The file is shared with the running app, so it is opened for
# reading with write sharing rather than through Get-Content.
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

# Log lines containing $text after $after, with their times, for the ones whose wording carries a number.
function Get-LogLines([string]$text, [DateTimeOffset]$after) {
    $stream = New-Object System.IO.FileStream($log, 'Open', 'Read', 'ReadWrite')
    try { $content = (New-Object System.IO.StreamReader($stream)).ReadToEnd() }
    finally { $stream.Dispose() }
    $found = @()
    foreach ($line in $content -split "`n") {
        if ($line -notlike "*$text*") { continue }
        if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) ') {
            $t = [DateTimeOffset]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture)
            if ($t -gt $after) { $found += [pscustomobject]@{ Time = $t; Line = $line } }
        }
    }
    return , $found
}

# A pointer carried along a path rather than teleported. The third run jumped the pointer off the controls bar in one
# move and the bar never heard the pointer leave, so its pin never let go; a real mouse sweeps across.
function Move-PointerPath([double]$fromX, [double]$fromY, [double]$toX, [double]$toY, [int]$steps = 12) {
    for ($i = 1; $i -le $steps; $i++) {
        Move-Pointer ($fromX + ($toX - $fromX) * $i / $steps) ($fromY + ($toY - $fromY) * $i / $steps) | Out-Null
        Start-Sleep -Milliseconds 25
    }
    return [DateTimeOffset]::Now
}

$process = $null
$window = $null
$mutedAtStart = $null
$startedPlayback = $false
[TunqioMouse+POINT]$pointerAtStart = New-Object TunqioMouse+POINT
[TunqioMouse]::GetCursorPos([ref]$pointerAtStart) | Out-Null

try {
    Write-Output "shell: $Exe"
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    Wait-Until { Find-Named $window 'Discovery mode' } 20 'the mode switcher appeared' | Out-Null
    if (-not (Get-Selected (Find-Named $window 'Discovery mode'))) { Select-Element (Find-Named $window 'Discovery mode'); Start-Sleep -Milliseconds 1200 }

    # Muted, and something loaded, as check-modes.ps1 does it.
    $toggle = (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }
    # Always the first album from its first track, even when a queue was restored. The fourth run inherited a queue
    # sitting on its last track, so Next changed nothing and there was nothing to announce.
    $tile = Wait-Until {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'Album * by *' } | Select-Object -First 1
    } 20 'an album tile appeared'
    Write-Output "playing from the start of: $($tile.Current.Name)"
    Invoke-Element $tile
    # A tile opens album detail rather than playing; its Play album button starts at the first track.
    $playAlbum = $null
    try { $playAlbum = Wait-Until { Find-Named $window 'Play album' } 5 'album detail offered Play album' } catch { }
    if ($playAlbum) { Invoke-Element $playAlbum }
    Wait-Until { (Find-Id $window 'Scrubber').Current.IsEnabled } 10 'a track loaded' | Out-Null
    if ((Find-Id $window 'PlayPauseButton').Current.Name -notlike 'Pause*') { Invoke-Element (Find-Id $window 'PlayPauseButton') }
    $startedPlayback = $true
    Start-Sleep -Seconds 1

    # ---- double-click the art (AC-417) ------------------------------------------------------------------------------
    Assert-UiaForeground -ProcessId $process.Id
    # The art Image is not in the automation tree even in the raw view (the first run looked), so it is found by
    # position: the Now Playing column is the part of the window left of the sidebar, and above the controls bar, and
    # the art sits in the upper middle of it over the metadata block.
    $win = $window.Current.BoundingRectangle
    $barNow = (Find-Named $window 'Playback controls panel').Current.BoundingRectangle
    $artX = $barNow.Left + $barNow.Width / 2
    $artY = $win.Top + ($barNow.Top - $win.Top) * 0.38
    Invoke-DoubleClick $artX $artY
    Start-Sleep -Milliseconds 1200
    Check 'double-clicking the art enters Focus' (Get-Selected (Find-Named $window 'Focus mode')) "double-clicked at $([math]::Round($artX)),$([math]::Round($artY))"
    if (-not (Get-Selected (Find-Named $window 'Focus mode'))) { Select-Element (Find-Named $window 'Focus mode'); Start-Sleep -Milliseconds 1200 }

    $win = $window.Current.BoundingRectangle
    $restX = $win.Left + $win.Width * 0.35
    $restY = $win.Top + $win.Height * 0.3

    # ---- hide after 3 s, back within 100 ms (AC-414, AC-136) ----------------------------------------------------------
    $lastMove = Move-Pointer $restX $restY
    Start-Sleep -Seconds 5
    $hidden = Get-LogLines 'Focus controls hidden' $lastMove
    # No stand-in values: the first run defaulted a missing log line to -1, and -1 sat inside the pass range.
    # Two halves: never earlier than 3 s after this script's last move, and 3 s after the last input the APP saw. The
    # third run hid 4.15 s after the script's move, which is late only if nothing else counted as input in between.
    $hideWorks = $hidden.Count -gt 0
    $afterMove = if ($hideWorks) { [math]::Round(($hidden[0].Time - $lastMove).TotalSeconds, 2) } else { $null }
    $appIdle = if ($hideWorks -and $hidden[0].Line -match 'after ([\d.,]+) s without input') { [double]($Matches[1] -replace ',', '.') } else { $null }
    Check 'in Focus the controls hide after 3 s without input' ($hideWorks -and $afterMove -ge 2.95 -and $null -ne $appIdle -and $appIdle -ge 2.95 -and $appIdle -le 3.6) $(if ($hideWorks) { "hidden $afterMove s after this script's last move; the app measured $appIdle s since the last input it saw" } else { 'no hide logged in 5 s' })

    $moved = Move-Pointer ($restX + 12) $restY
    Start-Sleep -Milliseconds 800
    $shown = Get-LogTimes 'Focus controls shown' $moved.AddMilliseconds(-50)
    $latency = if ($shown.Count -gt 0) { ($shown[0] - $moved).TotalMilliseconds } else { $null }
    Check 'a pointer move brings the controls back within 100 ms' ($hideWorks -and $null -ne $latency -and $latency -le 100) $(if ($null -ne $latency) { "shown $([math]::Round($latency)) ms after the move" } else { 'no show logged after the move' })

    # ---- the pointer over the bar holds it (AC-414) --------------------------------------------------------------------
    # These hold-checks mean something only once hiding has been seen to work; otherwise "no hide" is free.
    $bar = (Find-Named $window 'Playback controls panel').Current.BoundingRectangle
    $overBar = Move-PointerPath ($restX + 12) $restY ($bar.Left + $bar.Width / 2) ($bar.Top + 6)
    Start-Sleep -Seconds 5
    Check 'the controls stay while the pointer is over them' ($hideWorks -and (Get-LogTimes 'Focus controls hidden' $overBar).Count -eq 0) $(if ($hideWorks) { '5 s over the bar, no hide logged' } else { 'not shown: hiding never worked' })
    $away = Move-PointerPath ($bar.Left + $bar.Width / 2) ($bar.Top + 6) $restX $restY
    # 6.5 s, not 4.5: the fourth run's hide came 3 s after the input the app last saw, which Windows delivers about a
    # second after the pointer stops, so it landed at 4.3 s and the 4.5 s window read the log a moment too soon.
    Start-Sleep -Seconds 6.5
    $offBar = Get-LogLines 'Focus controls hidden' $away
    Check 'moving off the bar lets them hide again' ($offBar.Count -gt 0) $(if ($offBar.Count -gt 0) { "hidden $([math]::Round(($offBar[0].Time - $away).TotalSeconds, 2)) s after leaving: $($offBar[0].Line -replace '^.*(Focus controls hidden)', '$1')" } else { 'no hide logged in 6.5 s after leaving' })

    # ---- the left edge peeks the queue (AC-416) -----------------------------------------------------------------------
    # Read from the log, stamped where the peek is opened: the first run could not tell "did not open" from "not in the
    # automation tree".
    # The client's left edge is where the controls bar starts in Focus. The third run went past the window's own left
    # into its resize border, which is outside the window, and the peek closed 128 ms after opening; so the pointer
    # stops 3 px inside the client, and a close while it rests there is a failure of the peek, not of the path.
    $clientLeft = (Find-Named $window 'Playback controls panel').Current.BoundingRectangle.Left
    $midY = $win.Top + $win.Height / 2
    Move-Pointer $restX $midY | Out-Null
    Start-Sleep -Milliseconds 300
    $toEdge = [DateTimeOffset]::Now
    Move-PointerPath $restX $midY ($clientLeft + 3) $midY | Out-Null
    Start-Sleep -Milliseconds 900
    $opened = Get-LogTimes 'Focus queue peek opened' $toEdge
    $closedEarly = Get-LogTimes 'Focus queue peek closed' $toEdge
    Check 'the left edge peeks the queue' ($opened.Count -gt 0 -and $closedEarly.Count -eq 0) "$($opened.Count) open and $($closedEarly.Count) close logged with the pointer resting 3 px inside the client"
    $leaving = [DateTimeOffset]::Now
    Move-PointerPath ($clientLeft + 3) $midY ($clientLeft + 700) $midY | Out-Null
    Start-Sleep -Milliseconds 900
    Check 'moving away puts the peek back' ($opened.Count -gt 0 -and (Get-LogTimes 'Focus queue peek closed' $leaving).Count -gt 0) 'close logged'

    # ---- the announcer (AC-137) ----------------------------------------------------------------------------------------
    # What Narrator hears is a UIA notification event, which the .NET UIA client this script uses cannot subscribe
    # to. So this checks that a track change in Focus reaches the announcement path (log line and announcer text);
    # whether Narrator speaks it is for a person to hear (Q-71 found the first version silent).
    Write-Output '  note  the notification itself cannot be observed from the managed UIA client; Narrator is checked by ear'
    $announcer = Find-Id $window 'TrackAnnouncer'
    $nameBefore = if ($announcer) { $announcer.Current.Name } else { '' }
    # A track change has to actually happen for there to be anything to say. The second run pressed Next on the last
    # item of a restored two-item queue, nothing changed, and nothing was logged about playback at all. So Next first,
    # and Previous if Next went nowhere.
    $skipped = [DateTimeOffset]::Now
    $announced = @()
    $pressed = @()
    foreach ($button in 'NextButton', 'PreviousButton', 'PreviousButton') {
        Invoke-Element (Find-Id $window $button)
        $pressed += $button
        Start-Sleep -Seconds 2
        $announced = Get-LogTimes 'Focus announced a track change' $skipped
        if ($announced.Count -ge 1) { break }
    }
    $nameAfter = (Find-Id $window 'TrackAnnouncer').Current.Name
    Check 'a track change in Focus is announced' ($announced.Count -ge 1 -and $nameAfter -and $nameAfter -ne $nameBefore) "pressed $($pressed -join ', '); said '$nameAfter'"

    # ---- keyboard focus inside the hidden bar holds it (AC-415) -------------------------------------------------------
    if ($Keys) {
        Move-Pointer $restX $restY | Out-Null
        Start-Sleep -Seconds 4
        $inBar = $false
        for ($i = 0; $i -lt 40 -and -not $inBar; $i++) {
            Assert-UiaForeground -ProcessId $process.Id
            [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
            Start-Sleep -Milliseconds 150
            $focused = $A::FocusedElement.Current.BoundingRectangle
            $bar = (Find-Named $window 'Playback controls panel').Current.BoundingRectangle
            $inBar = $focused.Width -gt 0 -and $focused.Left -ge $bar.Left - 1 -and $focused.Right -le $bar.Right + 1 -and $focused.Top -ge $bar.Top - 1 -and $focused.Bottom -le $bar.Bottom + 1
        }
        Check 'Tab reaches the transport while it is hidden' $inBar "after $i Tab presses"
        $tabbed = [DateTimeOffset]::Now
        Start-Sleep -Seconds 5
        Check 'the bar stays while keyboard focus is inside it' ($hideWorks -and (Get-LogTimes 'Focus controls hidden' $tabbed).Count -eq 0) $(if ($hideWorks) { '5 s with focus in the bar, no hide logged' } else { 'not shown: hiding never worked' })
    }
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
            $discovery = Find-Named $window 'Discovery mode'
            if ($discovery -and -not (Get-Selected $discovery)) { Select-Element $discovery; Start-Sleep -Milliseconds 800 }
        }
        catch { Write-Output "note: could not restore play, mute or mode: $($_.Exception.Message)" }
    }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $window 15
    if ($closeProblem) { $script:failures += $closeProblem }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-focus: PASS'
    exit 0
}
Write-Output "check-focus: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
