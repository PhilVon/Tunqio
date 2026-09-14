# Shared UIA geometry for the shell's harnesses (T-163, T-168). Dot-source it:
#
#   . (Join-Path $PSScriptRoot 'uia-geometry.ps1')
#
# A harness that only asks whether a control EXISTS passes a window in which that control cannot be seen. Four
# stories shipped exactly that before these checks existed (T-137, T-138, T-139, E4-S9), and T-168's first
# measurement found a fifth in the build it was measuring (T-182: Shuffle clipped to nothing at 1000 px). What
# was learned getting here is in docs/build-test-release.md beside the test strategy; the short version:
#
#   - UIA clips a control's BoundingRectangle to what is visible, so "inside its own panel" is nearly always true
#     and catches nothing. What clipping DOES show is a control narrower than its natural size, or one reported
#     IsOffscreen with an infinite or empty rectangle while its panel is on screen.
#   - A control's natural width is not something the tree states. Get-UiaClippedControls takes it as the widest
#     the control measured across every window width tried, which works as long as one of those widths lays it
#     out unconstrained - include a Compact width, where the controls panel is a full-width bar.
#   - A control below the fold is also IsOffscreen. A caller walking a scrolling surface must scroll, and must
#     fail when it measured fewer controls than the surface has.
#
# ASCII only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not ('TunqioUiaGeometry' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TunqioUiaGeometry {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public static int ForegroundProcess() {
        int pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid;
    }
}
"@
}

# Screen pixels per effective pixel for a process's main window (1 at 96 DPI, 1.5 at 144). UIA rectangles are screen
# pixels and XAML sizes are effective pixels, so a check that compares a measurement with a number from the markup (a
# MaxWidth, a Padding) multiplies by this first (T-203).
function Get-UiaWindowScale([int]$ProcessId) {
    $handle = (Get-Process -Id $ProcessId).MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw "process $ProcessId has no main window handle" }
    $dpi = [TunqioUiaGeometry]::GetDpiForWindow($handle)
    if ($dpi -le 0) { return 1.0 }
    return $dpi / 96.0
}

# An element's screen rectangle, rounded, with Offscreen meaning "nothing of it is on screen": IsOffscreen, or a
# rectangle that is infinite or has no area.
function Get-UiaRect($Element) {
    $r = $Element.Current.BoundingRectangle
    $infinite = [double]::IsInfinity($r.Left) -or [double]::IsInfinity($r.Right) -or [double]::IsNaN($r.Left)
    $empty = $infinite -or $r.Width -le 0 -or $r.Height -le 0
    $rect = [pscustomobject]@{
        Left      = if ($infinite) { 0 } else { [math]::Round($r.Left) }
        Top       = if ($infinite) { 0 } else { [math]::Round($r.Top) }
        Right     = if ($infinite) { 0 } else { [math]::Round($r.Right) }
        Bottom    = if ($infinite) { 0 } else { [math]::Round($r.Bottom) }
        Width     = if ($infinite) { 0 } else { [math]::Round($r.Width) }
        Height    = if ($infinite) { 0 } else { [math]::Round($r.Height) }
        Offscreen = [bool]($Element.Current.IsOffscreen -or $empty)
    }
    $describe = if ($rect.Offscreen) { 'offscreen/empty' } else { '{0}..{1} x {2}..{3}' -f $rect.Left, $rect.Right, $rect.Top, $rect.Bottom }
    $rect | Add-Member -NotePropertyName Describe -NotePropertyValue $describe
    return $rect
}

# Resizes a process's main window and waits for the layout to settle. The position is fixed so readings from
# different runs line up.
function Set-UiaWindowSize([int]$ProcessId, [int]$Width, [int]$Height) {
    $handle = (Get-Process -Id $ProcessId).MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw "process $ProcessId has no main window handle" }
    [TunqioUiaGeometry]::MoveWindow($handle, 60, 40, $Width, $Height, $true) | Out-Null
    Start-Sleep -Milliseconds 1200
}

# Brings a process to the foreground and CHECKS that it got there, which AppActivate alone does not: it can return
# having done nothing, and a SendKeys after that goes to whatever window does have focus - possibly the user's.
# Throws rather than letting a keystroke land elsewhere.
function Assert-UiaForeground([int]$ProcessId) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    for ($i = 0; $i -lt 20; $i++) {
        try { [Microsoft.VisualBasic.Interaction]::AppActivate($ProcessId) } catch { }
        Start-Sleep -Milliseconds 300
        if ([TunqioUiaGeometry]::ForegroundProcess() -eq $ProcessId) { return }
    }
    throw "process $ProcessId never came to the foreground; refusing to send keys to another window"
}

# A problem string when $Inner is not wholly inside $Outer (both rects from Get-UiaRect), or has nothing on
# screen; $null when it is. One pixel of slack for the rounding between DIP layout and integer screen pixels.
function Test-UiaInside($Inner, $Outer, [string]$InnerName, [string]$OuterName, [int]$Slack = 1) {
    if ($Inner.Offscreen) { return "$InnerName has nothing on screen" }
    if ($Inner.Left -lt ($Outer.Left - $Slack) -or $Inner.Right -gt ($Outer.Right + $Slack) -or
        $Inner.Top -lt ($Outer.Top - $Slack) -or $Inner.Bottom -gt ($Outer.Bottom + $Slack)) {
        return "$InnerName spans $($Inner.Describe), outside $OuterName at $($Outer.Describe)"
    }
    return $null
}

# Problems for controls that were clipped, from readings taken at several window widths. Each reading is
# [pscustomobject]@{ Width = <window px>; Name = <control>; Rect = <Get-UiaRect> }.
#
# A control named in $Stretch sizes to the room it is given, so shrinking is its job and it is held to
# $StretchFloor instead. Every other control has a natural width - the widest it measured anywhere - and a
# reading narrower than that has been cut off by its container.
function Get-UiaClippedControls([object[]]$Readings, [string[]]$Stretch = @(), [int]$StretchFloor = 60, [int]$Slack = 1) {
    $problems = @()
    foreach ($group in ($Readings | Group-Object Name)) {
        $onScreen = @($group.Group | Where-Object { -not $_.Rect.Offscreen })
        if ($onScreen.Count -eq 0) { continue } # the caller reports offscreen readings itself
        $natural = ($onScreen | ForEach-Object { $_.Rect.Width } | Measure-Object -Maximum).Maximum
        foreach ($reading in $onScreen) {
            if ($Stretch -contains $group.Name) {
                if ($reading.Rect.Width -lt $StretchFloor) {
                    $problems += "at $($reading.Width)px '$($group.Name)' is $($reading.Rect.Width) px wide, under the $StretchFloor px it needs to be usable"
                }
            }
            elseif ($reading.Rect.Width -lt ($natural - $Slack)) {
                $problems += "at $($reading.Width)px '$($group.Name)' is $($reading.Rect.Width) px wide against its natural $natural px: its container is cutting it off"
            }
        }
    }
    return $problems
}

# Waits for every running Tunqio to exit before a harness launches its own (T-196). A Tunqio already running is
# somebody using it, so a harness never closes, activates or reads it; it checks again every $PollSeconds, for at most
# $WaitMinutes (T-174: every wait has an end, and a count), and returns $true once none is left, or $false if one is
# still running at the deadline, for the caller to refuse with its own message. -WaitMinutes 0 checks once.
#
# Its own lines go to the host, not the pipeline, so the return value is only the verdict: capture it, never pipe it.
function Wait-TunqioExited([int]$WaitMinutes = 10, [int]$PollSeconds = 30) {
    if ($WaitMinutes -lt 0) { $WaitMinutes = 0 }
    if ($PollSeconds -lt 1) { $PollSeconds = 1 }
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    $maxChecks = [int][Math]::Ceiling($WaitMinutes * 60 / $PollSeconds) + 1
    for ($check = 1; $check -le $maxChecks; $check++) {
        $running = @(Get-Process Tunqio -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return $true }
        if ($check -eq $maxChecks -or (Get-Date) -ge $deadline) { break }
        Write-Host ("  wait  Tunqio is running (pid {0}); checking again in {1} s, until {2}" -f
            ($running.Id -join ', '), $PollSeconds, $deadline.ToString('HH:mm'))
        Start-Sleep -Seconds $PollSeconds
    }
    return (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -eq 0)
}

# Closes the shell a harness launched and says whether it exited cleanly (T-188). Closes $Window through its
# WindowPattern when given one, else the process's main window; waits up to $TimeoutSeconds for the process to exit.
# Returns $null on a clean exit, otherwise a one-line problem the caller adds to its failures (it has also been
# written as a FAIL line). Two outcomes are problems:
#   - the process did not exit in time: it is killed, because it is the harness's own, and the run fails. Before
#     T-188 harnesses killed it silently, and 47 crashes on close went unseen.
#   - it exited with a non-zero code: 0xC000027B is a stowed exception, the crash T-188 fixed; the app's log
#     ([FTL] Unhandled exception) says what raised it.
# Only ever pass the process this harness started.
#
# Its own lines go to the host, not the pipeline, so the return value is only the problem: capture it, never pipe it.
function Close-TunqioShell($Process, $Window = $null, [int]$TimeoutSeconds = 20) {
    if (-not $Process) { return $null }
    if (-not $Process.HasExited) {
        # Windows PowerShell 5.1 reads ExitCode back empty unless the handle was opened while the process was alive.
        try { $null = $Process.Handle } catch { }
        $sent = $false
        if ($Window) {
            try {
                $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
                $sent = $true
            }
            catch { Write-Host "  note  closing the shell through UIA failed ($($_.Exception.Message)); closing its main window instead" }
        }
        # T-195: CloseMainWindow is a silent no-op while the process has no main window (MainWindowHandle 0), which is
        # the case until App shows it, and a harness that closes on a log line written before that (library.db created)
        # got here first: the close never reached the app, which then opened its window and ran until it was killed.
        # So wait, within the same timeout, for a window to close, and only count the close as sent when it was.
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (-not $sent -and -not $Process.HasExited -and (Get-Date) -lt $deadline) {
            try {
                $Process.Refresh()
                if ($Process.MainWindowHandle -ne [IntPtr]::Zero -and $Process.CloseMainWindow()) { $sent = $true; break }
            }
            catch { }
            Start-Sleep -Milliseconds 250
        }
        if (-not $sent -and -not $Process.HasExited) {
            try { $Process.Kill(); $Process.WaitForExit(5000) | Out-Null } catch { }
            $problem = "the shell (pid $($Process.Id)) had no main window to close within $TimeoutSeconds s (hidden, or not shown yet) and was killed"
            Write-Host "  FAIL  $problem"
            return $problem
        }
        $remaining = [Math]::Max(1000, [int]($deadline - (Get-Date)).TotalMilliseconds)
        if ($sent -and -not $Process.WaitForExit([Math]::Max($remaining, $TimeoutSeconds * 1000))) {
            try { $Process.Kill(); $Process.WaitForExit(5000) | Out-Null } catch { }
            $problem = "the shell (pid $($Process.Id)) did not exit within $TimeoutSeconds s of Close and was killed"
            Write-Host "  FAIL  $problem"
            return $problem
        }
        $null = $Process.WaitForExit(5000)
    }
    $code = $Process.ExitCode
    if ($null -eq $code) {
        Write-Host '  note  the shell exited but its exit code could not be read, so a crash on close is not ruled out'
        return $null
    }
    if ($code -ne 0) {
        $problem = "the shell (pid $($Process.Id)) exited with code 0x{0:X8} after Close; see [FTL] in its log and event 1000" -f $code
        Write-Host "  FAIL  $problem"
        return $problem
    }
    return $null
}
