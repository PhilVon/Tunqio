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
    public static int ForegroundProcess() {
        int pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid;
    }
}
"@
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
