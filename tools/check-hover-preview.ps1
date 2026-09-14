<#
.SYNOPSIS
  E5-S5: hover preview in a running shell. The first rest on an album tile in Discovery with previews off brings
  the one-time offer; turning previews on from it (or from the Settings > Library switch, when the offer has already
  been made) lets a 500 ms rest start a preview; moving off the tile stops it; and the switch is on the settings page.

  This MOVES THE MOUSE POINTER, because pointer rest is what is under test. The pointer is put back afterwards. The
  app is muted for the run, so a preview is logged but not heard. Timing is read from the app's own log ("Hover
  preview offered", "starting after the dwell", "stopped").

  WHAT IT CHANGES. It launches the app over the user's library and switches previews on and back off, which leaves
  ui.hoverPreview off and ui.hoverPreviewOffered set: the offer counts as made. -ResetOffer puts
  ui.hoverPreviewOffered back to what it was before the run, by editing settings.json after the app has exited.
  Use it only with the owner's say-so.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER ResetOffer
  After the app exits, restore ui.hoverPreviewOffered to its value before the run.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10,
    [switch]$ResetOffer,
    # T-196: how long to wait for a Tunqio somebody else started to go away, checking every 30 s, before refusing.
    [int]$WaitMinutes = 10
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $here 'uia-geometry.ps1')

if (-not ('TunqioHoverMouse' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TunqioHoverMouse {
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
$settingsPath = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
$log = Join-Path $env:LOCALAPPDATA ('Tunqio\logs\tunqio-' + (Get-Date -Format 'yyyyMMdd') + '.log')
$script:failures = @()

function Get-StoredSetting([string]$key) {
    try { return ((Get-Content $settingsPath -Raw | ConvertFrom-Json).$key) } catch { return $null }
}

$offeredBefore = Get-StoredSetting 'ui.hoverPreviewOffered'
$previewBefore = Get-StoredSetting 'ui.hoverPreview'
Write-Output "before the run: ui.hoverPreview=$previewBefore ui.hoverPreviewOffered=$offeredBefore"

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

# Injected, not SetCursorPos alone: WinUI reads pointer input, and a bare SetCursorPos produces none (T-62).
function Move-Pointer([double]$x, [double]$y) {
    [TunqioHoverMouse]::SetCursorPos([int]$x - 1, [int]$y) | Out-Null
    [TunqioHoverMouse]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
    return [DateTimeOffset]::Now
}

function Move-PointerPath([double]$fromX, [double]$fromY, [double]$toX, [double]$toY, [int]$steps = 12) {
    for ($i = 1; $i -le $steps; $i++) {
        Move-Pointer ($fromX + ($toX - $fromX) * $i / $steps) ($fromY + ($toY - $fromY) * $i / $steps) | Out-Null
        Start-Sleep -Milliseconds 25
    }
    return [DateTimeOffset]::Now
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

function Get-Tile($window) {
    Wait-Until {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'Album * by *' -and $_.Current.BoundingRectangle.Width -gt 50 } | Select-Object -First 1
    } 20 'an album tile appeared on screen'
}

$process = $null
$window = $null
$mutedAtStart = $null
[TunqioHoverMouse+POINT]$pointerAtStart = New-Object TunqioHoverMouse+POINT
[TunqioHoverMouse]::GetCursorPos([ref]$pointerAtStart) | Out-Null

try {
    Write-Output "shell: $Exe"
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    Wait-Until { Find-Named $window 'Discovery mode' } 20 'the mode switcher appeared' | Out-Null
    if (-not (Get-Selected (Find-Named $window 'Discovery mode'))) { Select-Element (Find-Named $window 'Discovery mode'); Start-Sleep -Milliseconds 1200 }

    $toggle = (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $mutedAtStart = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if (-not $mutedAtStart) { $toggle.Toggle() }

    $albums = Wait-Until { Find-Named $window 'Albums' } 20 'the sidebar showed its Albums view'
    Select-Element $albums
    Start-Sleep -Milliseconds 1000
    Assert-UiaForeground -ProcessId $process.Id

    $win = $window.Current.BoundingRectangle
    $awayX = $win.Left + $win.Width * 0.25
    $awayY = $win.Top + $win.Height * 0.3
    Move-Pointer $awayX $awayY | Out-Null
    Start-Sleep -Milliseconds 300

    # ---- the one-time offer (AC-420) -----------------------------------------------------------------------------
    $tile = Get-Tile $window
    $t = $tile.Current.BoundingRectangle
    $tileX = $t.Left + $t.Width / 2
    $tileY = $t.Top + $t.Height * 0.4
    if ($offeredBefore -ne $true -and $previewBefore -ne $true) {
        $rested = Move-PointerPath $awayX $awayY $tileX $tileY
        Start-Sleep -Milliseconds 1500
        $offered = Get-LogTimes 'Hover preview offered' $rested
        Check 'the first rest on a tile with previews off brings the offer' ($offered.Count -eq 1) "$($offered.Count) offer logged"
        $turnOn = Find-Named $window 'Turn on'
        Check 'the offer bar has a Turn on button' ($null -ne $turnOn) 'found by name'
        if ($turnOn) {
            $clicked = [DateTimeOffset]::Now
            Invoke-Element $turnOn
            Start-Sleep -Milliseconds 800
            Check 'Turn on turns previews on' ((Get-LogTimes 'Hover previews turned on from the offer' $clicked).Count -eq 1 -and (Get-StoredSetting 'ui.hoverPreview') -eq $true) "ui.hoverPreview=$(Get-StoredSetting 'ui.hoverPreview')"
        }
        $again = Move-PointerPath $tileX $tileY $awayX $awayY
        Move-PointerPath $awayX $awayY $tileX $tileY | Out-Null
        Start-Sleep -Milliseconds 1500
        Check 'the offer is not made a second time' ((Get-LogTimes 'Hover preview offered' $again).Count -eq 0) 'no second offer logged'
        Move-PointerPath $tileX $tileY $awayX $awayY | Out-Null
    }
    else {
        Write-Output "  note  the offer was already made before this run, so it is not checked; previews are turned on from Settings > Library instead"
    }

    # ---- the switch (AC-421), and turning previews on when the offer did not ------------------------------------
    # Since E6-S3 Settings is an overlay: the sidebar's item opens it, and Library is one of its sections.
    $settingsItem = Wait-Until { Find-Named $window 'Settings' } 10 'the sidebar showed its Settings item'
    try { Select-Element $settingsItem } catch { Invoke-Element $settingsItem }
    Start-Sleep -Milliseconds 1000
    Select-Element (Wait-Until { Find-Named $window 'Library settings' } 10 'the settings overlay listed its Library section')
    Start-Sleep -Milliseconds 1000
    $switch = Wait-Until { Find-Named $window 'Preview albums on hover' } 10 'Settings > Library showed the hover preview switch'
    $switchToggle = $switch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Check 'Settings > Library has the hover preview switch' ($null -ne $switch) 'found by name'
    if ($switchToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $switchToggle.Toggle()
        Start-Sleep -Milliseconds 500
    }
    Check 'the switch reads previews as on' ($switchToggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On -and (Get-StoredSetting 'ui.hoverPreview') -eq $true) "ui.hoverPreview=$(Get-StoredSetting 'ui.hoverPreview')"

    Invoke-Element (Wait-Until { Find-Named $window 'Close settings' } 5 'the settings overlay offered Close')
    Start-Sleep -Milliseconds 600
    Select-Element (Find-Named $window 'Albums')
    Start-Sleep -Milliseconds 1500
    Assert-UiaForeground -ProcessId $process.Id

    # ---- a 500 ms rest previews, leaving stops (AC-141) --------------------------------------------------------
    $tile = Get-Tile $window
    $t = $tile.Current.BoundingRectangle
    $tileX = $t.Left + $t.Width / 2
    $tileY = $t.Top + $t.Height * 0.4
    Move-Pointer $awayX $awayY | Out-Null
    Start-Sleep -Milliseconds 400
    Move-PointerPath $awayX $awayY ($tileX - 30) $tileY | Out-Null
    $arrived = Move-Pointer $tileX $tileY
    Start-Sleep -Milliseconds 1500
    $started = Get-LogTimes 'starting after the dwell' $arrived.AddMilliseconds(-600)
    $dwell = if ($started.Count -gt 0) { [math]::Round(($started[0] - $arrived).TotalMilliseconds) } else { $null }
    Check 'a rest of 500 ms on a tile starts a preview' ($null -ne $dwell -and $dwell -ge 400 -and $dwell -le 900) $(if ($null -ne $dwell) { "started $dwell ms after the pointer came to rest" } else { 'no preview start logged' })

    $left = Move-PointerPath $tileX $tileY $awayX $awayY 6
    Start-Sleep -Milliseconds 600
    $stopped = Get-LogTimes 'Hover preview stopped' $left.AddMilliseconds(-400)
    Check 'moving off the tile stops the preview' ($stopped.Count -ge 1) "$($stopped.Count) stop logged"

    $quick = Move-PointerPath $awayX $awayY $tileX $tileY 6
    Start-Sleep -Milliseconds 250
    Move-PointerPath $tileX $tileY $awayX $awayY 6 | Out-Null
    Start-Sleep -Milliseconds 900
    Check 'a pointer that passes over a tile without resting starts nothing' ((Get-LogTimes 'starting after the dwell' $quick).Count -eq 0) 'no start logged'
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    [TunqioHoverMouse]::SetCursorPos($pointerAtStart.X, $pointerAtStart.Y) | Out-Null
    if ($window) {
        try {
            # Previews back off through the app's own switch, so the settings file is written by the app.
            $settingsItem = Find-Named $window 'Settings'
            if ($settingsItem) { try { Select-Element $settingsItem } catch { Invoke-Element $settingsItem } }
            Start-Sleep -Milliseconds 1000
            $librarySection = Find-Named $window 'Library settings'
            if ($librarySection) { Select-Element $librarySection }
            Start-Sleep -Milliseconds 1000
            $switch = Find-Named $window 'Preview albums on hover'
            if ($switch -and $previewBefore -ne $true) {
                $p = $switch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($p.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $p.Toggle(); Start-Sleep -Milliseconds 500 }
            }
            if ($mutedAtStart -eq $false) { (Find-Id $window 'MuteButton').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        }
        catch { Write-Output "note: could not restore the switch or mute: $($_.Exception.Message)" }
    }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $window 15
    if ($closeProblem) { $script:failures += $closeProblem }

    if ($ResetOffer) {
        try {
            $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
            if ($null -eq $offeredBefore) { $json.PSObject.Properties.Remove('ui.hoverPreviewOffered') }
            else { $json.'ui.hoverPreviewOffered' = $offeredBefore }
            # UTF-8 WITHOUT a byte-order mark. Windows PowerShell's Set-Content -Encoding utf8 writes one, and the first
            # run of this script left settings.json starting EF BB BF: the file the app reads every launch, and throws
            # away as unreadable if its parser ever refuses it.
            [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
            Write-Output "reset ui.hoverPreviewOffered to its value before the run ($offeredBefore)"
        }
        catch { Write-Output "WARNING: could not reset ui.hoverPreviewOffered: $($_.Exception.Message)" }
    }
    Write-Output "after the run: ui.hoverPreview=$(Get-StoredSetting 'ui.hoverPreview') ui.hoverPreviewOffered=$(Get-StoredSetting 'ui.hoverPreviewOffered')"
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-hover-preview: PASS'
    exit 0
}
Write-Output "check-hover-preview: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
