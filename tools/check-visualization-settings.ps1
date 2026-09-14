<#
.SYNOPSIS
  E4-S9 (T-60, T-142, T-151): Settings > Visualization is reachable, lists every preset with a readable name,
  switches what the visualizer draws, offers exactly the controls the chosen preset declares and no others,
  picks up a preset dropped into the user preset directory after a Refresh, and carries the two audio-reactive
  theming settings E4-S6 shipped with no UI.

  Read off the running window's UIA tree, the way Narrator reads it, rather than asked of the app. The view model
  is already asserted headless in Tunqio.App.Tests; what cannot be asserted there is every wiring question -
  whether the page is reachable at all, whether the controls the view model computes ever become controls, what
  their automation names say, and whether a folder appearing on disk turns into a row. A wiring question is only
  answered from outside the process. T-116 exists because the tag editor tried to settle its on-screen criteria
  without a harness like this one, and a WinUI window captures BLACK in a screenshot, so a screenshot is not an
  alternative.

  WHAT IT TOUCHES. Nothing of the user's music and nothing in the library database: this page reads neither. It
  does write one directory - a scratch preset named below, inside the real user preset root
  (%LocalAppData%\Tunqio\presets), because that literal path is what AC-133 is about - and deletes it in the
  finally. Nothing else in the data root is touched; the launch count and the log grow, as they would for any
  launch. If the script is killed between those points, delete the named folder by hand.
.PARAMETER Exe
  The built shell. Defaults to the Debug x64 output.
.PARAMETER Seconds
  How long to give the window before reading the tree. The renderer is created when the SwapChainPanel loads.
  T-157 adds a first phase that needs neither the keyboard nor the real profile: on a scratch data root
  (artifacts\check-visualization-settings\<stamp>\data, passed as --data-root on every launch) it moves a slider
  through UIA RangeValue, closes the app, relaunches, and reads the same value back; then Reset, another relaunch,
  and the default read back. -PersistenceOnly runs that phase alone. Like check-first-run.ps1, the script refuses
  to start while any Tunqio process is running, checking once a minute for at most -WaitMinutes.
.PARAMETER KeepScratch
  Leave the scratch preset (and the T-157 scratch data root) behind, for looking at what the page did with it.
.PARAMETER PersistenceOnly
  Run only the T-157 relaunch phase: keystroke-free, and nothing outside the scratch data root is touched.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else opened to go away, retrying once a minute, before refusing (exit 2).
#>
[CmdletBinding()]
param(
    # T-161: drive a build that is older than the source on purpose (comparing against an old shell).
    [switch]$SkipFreshnessCheck,
    [string]$Exe,
    [int]$Seconds = 10,
    [switch]$KeepScratch,
    [switch]$PersistenceOnly,
    [int]$WaitMinutes = 10,
    [switch]$DumpGeometry,
    # Comma-separated. A string because under powershell.exe -File an [int[]] of "1600,1000" becomes one integer.
    [string]$Widths = '1600,1200,1000'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, Microsoft.VisualBasic

# Resolved in the body rather than in the param default: $PSScriptRoot is not reliably bound there under
# powershell.exe -File with a relative script path.
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\artifacts\bin\Tunqio.App\debug_win-x64\Tunqio.exe' }
$Exe = (Resolve-Path $Exe -ErrorAction SilentlyContinue).Path
if (-not $Exe) { throw 'The shell is not built; run msbuild Tunqio.sln -restore -p:Configuration=Debug -p:Platform=x64 first (a project-scoped build leaves a stale native core beside the app -- T-161).' }

# T-161: a harness driving a build that predates its own source reports the OLD binary's behaviour, and every
# symptom of that reads as a product bug. Refuse up front and say which binary is behind.
. (Join-Path $PSScriptRoot 'assert-fresh-build.ps1')
if (-not $SkipFreshnessCheck) { Assert-FreshBuild -AppDir (Split-Path $Exe) }

# T-163: the rectangle reader, the resizer and the checked foreground are shared with the other harnesses.
. (Join-Path $PSScriptRoot 'uia-geometry.ps1')
$widthList = @($Widths -split ',' | Where-Object { $_.Trim() } | ForEach-Object { [int]$_.Trim() })
if ($widthList.Count -eq 0) { throw "-Widths '$Widths' names no width" }

# The scratch preset AC-133 is about. A name nothing else could be, so a folder left behind by a killed run is
# unmistakable and safe to delete.
$scratchId = 'tunqio-check-e4s9'
$userPresets = Join-Path $env:LOCALAPPDATA 'Tunqio\presets'
$scratchDir = Join-Path $userPresets $scratchId

$scratchShader = @'
struct VSOut { float4 pos : SV_Position; };
VSOut VSMain(uint vid : SV_VertexID) {
    float2 corners[3] = { float2(-1.0, -3.0), float2(-1.0, 1.0), float2(3.0, 1.0) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    return o;
}
float4 PSMain(VSOut i) : SV_Target { return float4(0.0, 0.0, 1.0, 1.0); }
'@

# Declares one of every kind of metadata mp_preset_param_info carries, including a hidden one, so what the page
# does with a preset it has never seen is a statement with a list behind it.
$scratchManifest = @"
{
  "schema": 1,
  "id": "$scratchId",
  "name": "Check Harness Preset",
  "shader": "solid.hlsl",
  "vertex_count": 3,
  "instance_count": 1,
  "parameters": [
    { "name": "count", "label": "Harness count", "default": 20.0, "min": 4.0, "max": 40.0, "step": 1.0 },
    { "name": "width", "label": "Harness width", "unit": "px", "default": 2.5, "min": 1.0, "max": 8.0 },
    { "name": "mode", "label": "Harness mode", "default": 0.0, "min": 0.0, "max": 2.0,
      "choices": ["Alpha", "Beta", "Gamma"] },
    { "name": "art_primary", "default": -1.0, "min": -1.0, "max": 16777215.0, "hidden": true }
  ]
}
"@

$script:window = $null
$script:processId = 0

function Get-Elements {
    $script:window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Get-ElementNamed([string]$name, [string]$type) {
    foreach ($element in Get-Elements) {
        if ($element.Current.Name -ne $name) { continue }
        if (-not $type) { return $element }
        if (($element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') -eq $type) { return $element }
    }
    return $null
}

function Get-NamesOfType([string]$type) {
    $names = @()
    foreach ($element in Get-Elements) {
        if (($element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') -ne $type) { continue }
        if ($element.Current.Name) { $names += $element.Current.Name }
    }
    return $names
}

# Every name under the named list, which is how "what does Narrator read for a preset row" is answered.
function Get-ListRowNames([string]$listName) {
    $list = Get-ElementNamed $listName 'List'
    if (-not $list) { return @() }
    $names = @()
    foreach ($item in $list.FindAll(
            [System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.Name) { $names += $item.Current.Name }
    }
    return $names
}

# Activated and then CHECKED, rather than activated and hoped for. AppActivate can return having done nothing
# while the window is still coming up, and a SendKeys after that goes to whichever window does have focus -
# which is how a run of this script failed every case at once with the app perfectly healthy in the log. This
# script had its own copy of that check; since T-163 it is the one in tools/uia-geometry.ps1.
function Set-Foreground {
    Assert-UiaForeground -ProcessId $script:processId
}

function Send-Keys([string]$keys) {
    Set-Foreground
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds 600
}

function Invoke-Named([string]$name) {
    $button = Get-ElementNamed $name 'Button'
    if (-not $button) { throw "no button named '$name' in the tree" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800
}

function Select-ListRow([string]$listName, [string]$rowName) {
    $list = Get-ElementNamed $listName 'List'
    if (-not $list) { throw "no list named '$listName'" }
    foreach ($item in $list.FindAll(
            [System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.Name -ne $rowName) { continue }
        # Scrolled into view first: the list has a MaxHeight, and a row past it is realised but not reachable.
        try {
            $item.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
            Start-Sleep -Milliseconds 250
        }
        catch { }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 1200
        return
    }

    throw "no row named '$rowName' in '$listName'"
}

function Get-SelectedRow([string]$listName) {
    $list = Get-ElementNamed $listName 'List'
    if (-not $list) { return $null }
    $selected = $list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selected.Length -eq 0) { return $null }
    return $selected[0].Current.Name
}

# ---- geometry ---------------------------------------------------------------------------------------------
#
# Everything above this line asks whether a control EXISTS, is named, carries its unit, moves and reaches
# settings.json. All of that was true of the build Phil rejected: the sliders were in the tree, correct in
# every property, and hanging off the right of the panel and under the music controls. A tree-walking harness
# cannot see that unless it reads the rectangles, and UIA has been offering them all along
# (AutomationElement.Current.BoundingRectangle).
#
# The same class has now cost this project four stories - T-137 (a verdict column that existed and could not be
# seen), T-138 (a list that scrolled its parent before itself), T-139 (a row overflowing a dialog) and this one
# - each found by a person rather than by a check.
#
# THE INVARIANT, stated so it can be argued with: no interactive control on the page may extend horizontally
# beyond the CONTENT COLUMN that lays the page out. Horizontally and not vertically, because the surface is a
# ScrollViewer and content below the fold is meant to be outside it; the horizontal axis has no scrollbar and
# nothing there is meant to be off the edge.
#
# The column and NOT the ScrollViewer, which is the first thing tried and does not work. UIA clips a control's
# BoundingRectangle to what is visible, so a control hanging off the right reports a rectangle that stops
# neatly at the panel edge and is trivially "contained"; ScrollPattern is no help either, because the viewer's
# horizontal scrolling is disabled so it reports HorizontalViewSize = 100 whatever the content does. Measured,
# not assumed: with the bug in place, containment-in-the-ScrollViewer passed at 2400, 1600, 1200, 1000 and
# 900 px. What DID show it is the column - at a 1000 px window the ComboBox spanned 789..1029 while the column
# ran 789..908, so it stood 121 px outside the thing laying it out and reached the panel's outer edge exactly.
#
# Checked at several widths and at several scroll positions. Widths because a fixed width is invisible at a
# wide one; scroll positions because a control below the fold is IsOffscreen with an infinite rectangle, and
# the first version of this check silently examined six of the page's fourteen controls - the reactive-theming
# slider, which had the same fixed width, was never looked at.

# Both from tools/uia-geometry.ps1 (T-163). Get-UiaRect's Offscreen covers what this script used to call Empty:
# a rectangle with no area, or one UIA reports IsOffscreen.
function Set-WindowSize([int]$width, [int]$height) {
    Set-UiaWindowSize -ProcessId $script:processId -Width $width -Height $height
}

function Get-Rect($element) {
    Get-UiaRect $element
}

# Every control a person can point at or type into, on the page. Buttons of the shell outside the settings
# surface (the transport, the queue) are excluded by taking only what is inside the surface's own subtree.
function Get-PageControls($surface) {
    $wanted = 'Slider', 'ComboBox', 'Button', 'CheckBox', 'List', 'Edit'
    $found = @()
    foreach ($e in $surface.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $type = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if ($wanted -notcontains $type) { continue }
        if ($e.Current.IsOffscreen) { continue } # scrolled out of view is not overflowing
        $found += [pscustomobject]@{ Name = $e.Current.Name; Type = $type; Rect = Get-Rect $e }
    }
    return $found
}

function Set-ScrollTo($surface, [double]$percent) {
    try {
        $surface.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, $percent)
        Start-Sleep -Milliseconds 500
    }
    catch { }
}

# The one case behind the geometry criterion. Returns a problem string, or $null.
function Test-Geometry([int]$width) {
    Set-WindowSize $width 900
    $surface = Get-ElementNamed 'Visualization settings surface'
    if (-not $surface) { return "no settings surface in the tree at ${width}px" }
    $column = Get-ElementNamed 'Visualization settings content'
    if (-not $column) { return "no settings content column in the tree at ${width}px" }
    $panel = Get-Rect $column
    if ($DumpGeometry) {
        try {
            $sp = $surface.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current
            Write-Host ("        ScrollPattern: HorizontallyScrollable={0} HorizontalViewSize={1} HorizontalScrollPercent={2} VerticalViewSize={3}" -f `
                $sp.HorizontallyScrollable, $sp.HorizontalViewSize, $sp.HorizontalScrollPercent, $sp.VerticalViewSize)
        }
        catch { Write-Host "        ScrollPattern: not supported ($($_.Exception.Message))" }
        Write-Host "        --- everything under the surface at ${width}px (surface $($panel.Left)..$($panel.Right)) ---"
        foreach ($e in $surface.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $r = Get-Rect $e
            $t = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            Write-Host ("        {0,-12} off={1,-5} L={2,6} R={3,6} W={4,5} '{5}'" -f $t, $e.Current.IsOffscreen, $r.Left, $r.Right, $r.Width, $e.Current.Name)
        }
    }

    # Every scroll position, because a control below the fold has an infinite rectangle and would be skipped.
    $seen = @{}
    $over = @()
    $invisible = @()
    $narrow = @()
    foreach ($percent in 0, 50, 100) {
        Set-ScrollTo $surface $percent
        $column = Get-ElementNamed 'Visualization settings content'
        if (-not $column) { continue }
        $panel = Get-Rect $column
        foreach ($c in (Get-PageControls $column)) {
            $seen[$c.Name] = $true
            # 1 px of slack for the rounding between a DIP layout and an integer screen rectangle.
            if ($c.Rect.Right -gt ($panel.Right + 1) -or $c.Rect.Left -lt ($panel.Left - 1)) {
                $over += "$($c.Type) '$($c.Name)' spans $($c.Rect.Left)..$($c.Rect.Right) against a column of $($panel.Left)..$($panel.Right), $([math]::Round($c.Rect.Right - $panel.Right)) px past its right edge"
            }
            # T-163. The boundary check above cannot see ONE fixed-width control inside the parameter template: UIA
            # trims it at the column edge and nothing moves. Measured with the per-parameter Slider back at Width=360
            # HorizontalAlignment=Left: at a 1000px window every slider read 789..1036, exactly the column, and the
            # surface stayed put, so the page passed while each slider was cut off. What it cannot hide is the wide
            # window, where the same sliders read 360 inside a 553 px column. So the page's own promise is asserted
            # (T-60's AC-307, "everything in it stretches"): a slider or list box spans its column at every width.
            if ($c.Type -in 'Slider', 'ComboBox' -and $c.Rect.Width -lt ($panel.Width - 2)) {
                $narrow += "$($c.Type) '$($c.Name)' is $($c.Rect.Width) px wide in a column of $($panel.Width): it is not stretching, which is what a fixed Width looks like"
            }
            # T-137's shape: present in the tree, on screen, and nothing to see.
            if ($c.Rect.Offscreen) { $invisible += "$($c.Type) '$($c.Name)' has an empty rectangle" }
        }
    }

    Set-ScrollTo $surface 0
    $outer = Get-Rect $surface
    $controlsPanel = Get-ElementNamed 'Playback controls panel'
    $neighbour = if ($controlsPanel) { Get-Rect $controlsPanel } else { $null }
    Write-Host ("        ${width}px window: surface $($outer.Left)..$($outer.Right) ($($outer.Width) px), column $($panel.Left)..$($panel.Right) ($($panel.Width) px), controls bar ends at $(if ($neighbour) { $neighbour.Right } else { '?' }), $($seen.Count) controls measured across three scroll positions")

    # THE ONE PHIL FOUND. Over-wide content does not merely overflow: it pushes the ScrollViewer itself past the
    # column the shell gave the sidebar. Measured on the rejected markup at a 1200 px window, when the controls
    # were a third column to the RIGHT of the sidebar: the surface reached 1162 while that panel started at 1080,
    # 82 px underneath it, which is exactly what Phil described. With the fix the surface stopped at 1057.
    #
    # T-182 moved the controls to a bar under Now Playing, so the sidebar is now the right-hand column and that
    # sibling boundary no longer exists on its right. Two things replace it, both measured on the new layout. On
    # the LEFT the sidebar's neighbour is the Now Playing column the bar spans, so the surface must start at or
    # right of the bar's right edge (at a 1600 px window the bar spans 68..1018 since the sidebar went to 60/40). On the RIGHT
    # the neighbour is the window's edge, and UIA clips a rectangle there: the surface's right edge reads as the
    # client's right edge whatever its content does, so "does not cross the edge" cannot fail. What clipping does
    # leave visible is the content column losing the padding it sits in: at every column width it is 16 px inside
    # the surface on both sides, and over-wide content would push it out to the clipped edge.
    if (-not $neighbour) { return "the playback controls panel is not in the tree at ${width}px, so the boundary cannot be checked" }
    # The left edge is shared only in a column shape, where the bar spans Now Playing and stops well short of the
    # window's right edge (at a 1600 px window it ends at 1018 in a window ending at 1660). Stacked, the bar spans the
    # client (852 in a window ending at 860) and sits beneath the sidebar, so it is not beside it at all.
    $frame = Get-Rect $script:window
    $barBesideSidebar = $neighbour.Right -lt ($frame.Right - 40)
    if ($barBesideSidebar -and $outer.Left -lt ($neighbour.Right - 1)) {
        return "at ${width}px the settings surface starts at $($outer.Left) but the controls bar under Now Playing ends at $($neighbour.Right): the page reaches $([math]::Round($neighbour.Right - $outer.Left)) px over the column beside it"
    }
    if ($panel.Right -gt ($outer.Right - 8)) {
        return "at ${width}px the settings content column reaches $($panel.Right) inside a surface ending at $($outer.Right): it has lost its right padding, which is what over-wide content clipped at the window edge looks like"
    }
    # The column must also USE the panel it is in, or the page is correct and half empty - which is how the
    # fixed widths hid: everything was laid out against a column narrower than the room available.
    if ($panel.Width -lt ($outer.Width - 40)) {
        return "at ${width}px the content column is $($panel.Width) px inside a $($outer.Width) px panel, so the page is using less than the room it has"
    }
    # A check that measured nothing passes for the wrong reason. Fourteen is what this page has; eight is a
    # floor low enough to survive a preset with few parameters and high enough to catch a broken walk.
    if ($seen.Count -lt 8) { return "only $($seen.Count) control(s) measured at ${width}px: " + (($seen.Keys | Sort-Object) -join ', ') }
    if ($over.Count -gt 0) {
        $unique = $over | Sort-Object -Unique
        return "at ${width}px, $($unique.Count) control(s) overflow the settings column: " + ($unique -join '; ')
    }
    if ($invisible.Count -gt 0) { return "at ${width}px: " + (($invisible | Sort-Object -Unique) -join '; ') }
    if ($narrow.Count -gt 0) { return "at ${width}px, $(@($narrow | Sort-Object -Unique).Count) control(s) do not fill the settings column: " + (($narrow | Sort-Object -Unique) -join '; ') }
    return $null
}

$failures = @()

function Test-Case([string]$what, [scriptblock]$check) {
    # A throw inside a case is that case failing, not the run ending: one unreachable control must not hide
    # every answer after it.
    try { $problem = & $check }
    catch { $problem = "threw: $($_.Exception.Message)" }
    if ($problem) {
        $script:failures += "$what - $problem"
        Write-Output "  FAIL  $what"
        Write-Output "        $problem"
    }
    else {
        Write-Output "  ok    $what"
    }
}

# ---- T-157: a moved slider survives a relaunch, on a scratch data root ------------------------------------------
#
# Keystroke-free (UIA patterns only, so it runs while somebody else is using the machine) and on its own profile: every
# launch passes --data-root, so the real settings.json is never opened. Three launches: move Bars on Spectrum Bars and
# Thickness on Waveform; relaunch and read both back without touching anything, then Reset Spectrum Bars; relaunch and
# read the default back. The log is read only after the last launch has exited, because the file sink buffers.

function Wait-For([scriptblock]$condition, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $found = & $condition
        if ($found) { return $found }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Start-ScratchShell([string]$root) {
    $full = [System.IO.Path]::GetFullPath($root)
    $real = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Tunqio'))
    if ($full.StartsWith($real, [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    $p = Start-Process $Exe -ArgumentList @('--data-root', "`"$full`"") -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $w = Wait-For { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid) } 30
    if (-not $w) {
        if (-not $p.HasExited) { $p.Kill() }
        throw 'the shell window never appeared in the automation tree'
    }
    $script:window = $w
    $script:processId = $p.Id
    return $p
}

function Stop-ScratchShell($p) {
    if (-not $p -or $p.HasExited) { return }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $p $script:window 20
    if ($closeProblem) { $script:failures += $closeProblem }
}

# Settings > Visualization without a key: the controls bar's button, then the section. Returns a problem or $null.
function Open-VisualizationPage {
    $button = Wait-For { Get-ElementNamed 'Open settings' 'Button' } 20
    if (-not $button) { return 'no Open settings button' }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $item = Wait-For { Get-ElementNamed 'Visualization settings' 'ListItem' } 10
    if (-not $item) { return 'Settings did not open, or it has no Visualization section' }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if (-not (Wait-For { Get-ElementNamed 'Presets' 'List' } 10)) { return 'the visualization page has no preset list' }
    return $null
}

function Read-ScratchSettings([string]$root) {
    $file = Join-Path $root 'settings.json'
    if (-not (Test-Path $file)) { return $null }
    return Get-Content $file -Raw | ConvertFrom-Json
}

function Get-VizParamKeys($json) {
    if (-not $json) { return @() }
    return @($json.PSObject.Properties | Where-Object { $_.Name -like 'viz.params.*' } | ForEach-Object { "$($_.Name)=$($_.Value)" })
}

function Get-SliderList { return (Get-NamesOfType 'Slider') -join ' | ' }

# Through UIA RangeValue, which is what a drag ends in. Returns a problem or $null.
function Set-SliderNamed([string]$sliderName, [double]$to) {
    $slider = Wait-For { Get-ElementNamed $sliderName 'Slider' } 15
    if (-not $slider) { return "no slider named '$sliderName'; sliders are [$(Get-SliderList)]" }
    $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($to)
    Start-Sleep -Milliseconds 500
    return $null
}

function Invoke-PersistencePhase {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $scratchRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\artifacts\check-visualization-settings\$stamp"))
    $root = Join-Path $scratchRoot 'data'
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    # A profile that has settled its welcome (E6-S6) and starts on Spectrum Bars. WriteAllText: no BOM.
    [System.IO.File]::WriteAllText((Join-Path $root 'settings.json'), '{ "ui.welcomeShown": false, "viz.preset": "spectrum-bars" }')
    Write-Output ''
    Write-Output 'Preset parameters survive a relaunch (T-157), keystroke-free, on a scratch data root'
    Write-Output "  data root       $root"

    $p = $null
    try {
        # ---- launch 1: move a slider on two presets
        $p = Start-ScratchShell $root
        Start-Sleep -Seconds $Seconds
        Test-Case 'launch 1: Settings > Visualization opens by UIA alone' { Open-VisualizationPage }
        Test-Case 'launch 1: Bars moved from 64 to 96 on Spectrum Bars' {
            $problem = Set-SliderNamed 'Bars, 64' 96
            if ($problem) { return $problem }
            if (-not (Wait-For { Get-ElementNamed 'Bars, 96' 'Slider' } 5)) { return "the sliders read [$(Get-SliderList)]" }
        }
        Test-Case 'launch 1: Thickness moved from 2.5 px to 4 px on Waveform' {
            Select-ListRow 'Presets' 'Waveform'
            $problem = Set-SliderNamed 'Thickness, 2.5 px' 4
            if ($problem) { return $problem }
            if (-not (Wait-For { Get-ElementNamed 'Thickness, 4 px' 'Slider' } 5)) { return "the sliders read [$(Get-SliderList)]" }
        }
        Test-Case 'launch 1: back on Spectrum Bars the slider reads Bars, 96 again' {
            Select-ListRow 'Presets' 'Spectrum Bars'
            if (-not (Wait-For { Get-ElementNamed 'Bars, 96' 'Slider' } 5)) { return "after the switch back the sliders read [$(Get-SliderList)]" }
        }
        Test-Case 'launch 1: settings.json holds viz.params.<preset>.<name> as the sliders moved, and no art_ key' {
            $json = Wait-For { $j = Read-ScratchSettings $root; if ($j -and $j.'viz.params.waveform.thickness' -eq 4) { $j } } 10
            $keys = Get-VizParamKeys (Read-ScratchSettings $root)
            Write-Host "        $($keys -join '  ')"
            if (-not $json) { return "viz.params keys while the app runs: [$($keys -join ', ')]" }
            if ($json.'viz.params.spectrum-bars.bars' -ne 96) { return "viz.params.spectrum-bars.bars is '$($json.'viz.params.spectrum-bars.bars')'" }
            if ($keys | Where-Object { $_ -like '*art_*' }) { return "a hidden parameter was stored: $($keys -join ', ')" }
        }
        Stop-ScratchShell $p

        # ---- launch 2: read both back, then Reset one preset
        $p = Start-ScratchShell $root
        Start-Sleep -Seconds $Seconds
        Test-Case 'launch 2: the page opens on Spectrum Bars' {
            $problem = Open-VisualizationPage
            if ($problem) { return $problem }
            $selected = Get-SelectedRow 'Presets'
            if ($selected -ne 'Spectrum Bars') { return "the selected preset is '$selected'" }
        }
        Test-Case 'launch 2: Bars reads 96 with nothing touched (reapplied after the preset loaded)' {
            if (-not (Wait-For { Get-ElementNamed 'Bars, 96' 'Slider' } 10)) { return "the sliders read [$(Get-SliderList)]" }
        }
        Test-Case 'launch 2: Waveform reads Thickness, 4 px, and Spectrum Bars reads Bars, 96 after switching back' {
            Select-ListRow 'Presets' 'Waveform'
            if (-not (Wait-For { Get-ElementNamed 'Thickness, 4 px' 'Slider' } 5)) { return "on Waveform the sliders read [$(Get-SliderList)]" }
            Select-ListRow 'Presets' 'Spectrum Bars'
            if (-not (Wait-For { Get-ElementNamed 'Bars, 96' 'Slider' } 5)) { return "back on Spectrum Bars the sliders read [$(Get-SliderList)]" }
        }
        Test-Case 'launch 2: Reset puts Bars back to 64 and removes the Spectrum Bars keys, and only those' {
            Invoke-Named 'Reset to defaults'
            if (-not (Wait-For { Get-ElementNamed 'Bars, 64' 'Slider' } 5)) { return "after Reset the sliders read [$(Get-SliderList)]" }
            $gone = Wait-For { $k = Get-VizParamKeys (Read-ScratchSettings $root); if (-not ($k | Where-Object { $_ -like 'viz.params.spectrum-bars.*' })) { 'gone' } } 10
            $keys = Get-VizParamKeys (Read-ScratchSettings $root)
            Write-Host "        $($keys -join '  ')"
            if (-not $gone) { return "Spectrum Bars keys are still stored: [$($keys -join ', ')]" }
            if (-not ($keys | Where-Object { $_ -eq 'viz.params.waveform.thickness=4' })) { return "Reset took the Waveform key too: [$($keys -join ', ')]" }
        }
        Stop-ScratchShell $p

        # ---- launch 3: the reset survives a relaunch too
        $p = Start-ScratchShell $root
        Start-Sleep -Seconds $Seconds
        Test-Case 'launch 3: Bars reads 64 after the reset and a relaunch, and Waveform still reads 4 px' {
            $problem = Open-VisualizationPage
            if ($problem) { return $problem }
            if (-not (Wait-For { Get-ElementNamed 'Bars, 64' 'Slider' } 10)) { return "the sliders read [$(Get-SliderList)]" }
            Select-ListRow 'Presets' 'Waveform'
            if (-not (Wait-For { Get-ElementNamed 'Thickness, 4 px' 'Slider' } 5)) { return "on Waveform the sliders read [$(Get-SliderList)]" }
        }
        Stop-ScratchShell $p
        $p = $null

        Test-Case 'the log shows stored values reapplied after a preset started' {
            $lines = @()
            foreach ($log in @(Get-ChildItem (Join-Path $root 'logs') -Filter '*.log' -ErrorAction SilentlyContinue)) { $lines += @(Get-Content $log.FullName) }
            $reapplied = @($lines | Where-Object { $_ -match 'Reapplied [1-9][0-9]* stored parameter' })
            foreach ($line in $reapplied) { Write-Host "        $($line.Substring($line.IndexOf('Reapplied')))" }
            if ($reapplied.Count -eq 0) { return "no 'Reapplied N stored parameter(s)' line with N above 0 in $($lines.Count) log line(s)" }
        }
    }
    finally {
        Stop-ScratchShell $p
        if (-not $KeepScratch) { Remove-Item $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---- refuse while somebody's Tunqio is open: once a minute, for at most -WaitMinutes (T-174: every wait has an end) ----
$refuseDeadline = (Get-Date).AddMinutes($WaitMinutes)
while (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    if ((Get-Date) -ge $refuseDeadline) {
        Write-Output "check-visualization-settings: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (until $($refuseDeadline.ToString('HH:mm')))"
    Start-Sleep -Seconds 60
}

Invoke-PersistencePhase
if ($PersistenceOnly) {
    Write-Output ''
    if ($failures.Count -eq 0) {
        Write-Output 'PASS: preset parameters survive a relaunch'
        exit 0
    }

    foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
    exit 1
}

Write-Output ''
Write-Output "Settings > Visualization (E4-S9), read off the live automation tree"
Write-Output "  exe             $Exe"
Write-Output "  user presets    $userPresets"
Write-Output "  scratch preset  $scratchDir  (written during the run, removed afterwards)"
Write-Output ''

# Removed before the run too: a folder left by a killed run would make "it appeared after Refresh" untrue.
if (Test-Path $scratchDir) { Remove-Item $scratchDir -Recurse -Force }

# The settings file is the user's, and this script writes to it: choosing a preset stores viz.preset and the two
# theming keys are toggled below. So it is copied aside now and put back once the app has exited - after, so the
# app's own shutdown flush cannot land on top of the restore.
$settingsFile = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
$settingsBackup = Join-Path $env:TEMP 'tunqio-check-e4s9-settings.json'
$hadSettings = Test-Path $settingsFile
if ($hadSettings) { Copy-Item $settingsFile $settingsBackup -Force }

$process = Start-Process $Exe -PassThru
try {
    Start-Sleep -Seconds $Seconds
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $script:window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $script:window) { throw 'The shell window never appeared in the automation tree.' }
    $script:processId = $process.Id
    Set-Foreground

    # ---- reachable at all ------------------------------------------------------------------------------------

    # Since E6-S3 Settings is an overlay whose sections are NavigationViewItems, which answer SelectionItem, not Invoke.
    $selectSection = {
        param([string]$name)
        $item = Get-ElementNamed $name 'ListItem'
        if (-not $item) { return $false }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 800
        return $true
    }

    Test-Case 'Ctrl+, then Visualization opens the page' {
        # Retried, because the keystroke is the thing under test and a keystroke lost to a window that was not
        # yet ready would be reported as a missing feature. Three presses is still "Ctrl+, opens Settings".
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            Send-Keys '^{,}'
            if (Get-ElementNamed 'Visualization settings' 'ListItem') { break }
            Start-Sleep -Milliseconds 800
        }

        if (-not (& $selectSection 'Visualization settings')) {
            return 'Settings did not open, or it has no Visualization section'
        }
        if (-not (Get-ElementNamed 'Presets' 'List')) { 'the visualization page has no preset list' }
    }

    Test-Case 'the page carries the sections it promises' {
        foreach ($want in 'Refresh presets', 'Open preset folder', 'Reset to defaults') {
            if (-not (Get-ElementNamed $want 'Button')) { return "no '$want' button" }
        }
    }

    # ---- the preset list -------------------------------------------------------------------------------------

    Test-Case 'every shipped preset is in the list, by its display name' {
        $rows = Get-ListRowNames 'Presets'
        foreach ($want in 'Spectrum Bars', 'Waveform', 'Radial Spectrum', 'Ambient Glow') {
            if ($rows -notcontains $want) { return "the list is [$($rows -join ', ')]; '$want' is missing" }
        }
    }

    Test-Case 'a row reads as the preset name and not as a DTO (T-122)' {
        # The id is on screen under the name and deliberately out of the automation tree: Narrator saying
        # "Spectrum Bars spectrum-bars" is the Tracks-row mistake in a smaller frame.
        $rows = Get-ListRowNames 'Presets'
        foreach ($row in $rows) {
            if ($row -match '[a-z]+-[a-z]+' -and $row -cmatch '^[a-z-]+$') {
                return "a row reads '$row', which is an id rather than a name"
            }
        }
    }

    # ---- the controls come from the preset ---------------------------------------------------------------------

    Test-Case 'Spectrum Bars offers exactly the four parameters it declares' {
        Select-ListRow 'Presets' 'Spectrum Bars'
        $sliders = Get-NamesOfType 'Slider'
        $combos = Get-NamesOfType 'ComboBox'
        foreach ($want in 'Bars, 64', 'Spread, 0.35', 'Gain, 1') {
            if (-not ($sliders | Where-Object { $_ -eq $want })) {
                return "no slider named '$want'; sliders are [$($sliders -join ' | ')]"
            }
        }
        if (-not ($combos | Where-Object { $_ -eq 'Colour source, Position' })) {
            return "the colour source is not a named list; combo boxes are [$($combos -join ' | ')]"
        }
    }

    Test-Case 'Ambient Glow offers no art_ parameter (T-142''s hidden flag)' {
        Select-ListRow 'Presets' 'Ambient Glow'
        $controls = @(Get-NamesOfType 'Slider') + @(Get-NamesOfType 'ComboBox')
        foreach ($control in $controls) {
            if ($control -like '*art_*') {
                return "'$control' is on the page; the album art palette is set by code and carries packed sRGB integers"
            }
        }
        # And what it does offer is there, or the check above would pass on an empty page.
        if (-not ($controls | Where-Object { $_ -like 'Glow,*' })) {
            return "Ambient Glow's own parameters are missing too; controls are [$($controls -join ' | ')]"
        }
    }

    Test-Case 'Waveform''s thickness carries its unit' {
        Select-ListRow 'Presets' 'Waveform'
        $sliders = Get-NamesOfType 'Slider'
        if (-not ($sliders | Where-Object { $_ -eq 'Thickness, 2.5 px' })) {
            return "no 'Thickness, 2.5 px' slider; sliders are [$($sliders -join ' | ')]"
        }
    }

    Test-Case 'moving a slider changes what it says it is' {
        Select-ListRow 'Presets' 'Spectrum Bars'
        $slider = Get-ElementNamed 'Bars, 64' 'Slider'
        if (-not $slider) { return 'the Bars slider is not at its default, so this case has nothing to move' }
        $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(96)
        Start-Sleep -Milliseconds 500
        if (-not (Get-ElementNamed 'Bars, 96' 'Slider')) {
            return "the slider still reads [$((Get-NamesOfType 'Slider') -join ' | ')]"
        }
    }

    Test-Case 'Reset puts it back to the default the manifest declares' {
        Invoke-Named 'Reset to defaults'
        if (-not (Get-ElementNamed 'Bars, 64' 'Slider')) {
            return "after Reset the sliders read [$((Get-NamesOfType 'Slider') -join ' | ')]"
        }
    }

    # ---- AC-133: a preset dropped in while the app is running ----------------------------------------------------

    Test-Case 'a preset dropped into the user directory is not there until Refresh' {
        New-Item -ItemType Directory -Path $scratchDir -Force | Out-Null
        # WriteAllText, not Set-Content -Encoding utf8: Windows PowerShell writes a BOM, and a BOM at the head
        # of the HLSL makes D3DCompile refuse the shader while the manifest still parses - so the preset would
        # appear in the list and then fail to load, which is a confusing way for a harness to be wrong.
        [System.IO.File]::WriteAllText((Join-Path $scratchDir 'solid.hlsl'), $scratchShader)
        [System.IO.File]::WriteAllText((Join-Path $scratchDir 'preset.json'), $scratchManifest)
        Start-Sleep -Milliseconds 500
        $rows = Get-ListRowNames 'Presets'
        if ($rows -contains 'Check Harness Preset, your preset') {
            'it appeared without a refresh, so the catalogue is being watched rather than reread on request'
        }
    }

    Test-Case 'Refresh finds it, and says it did' {
        Invoke-Named 'Refresh presets'
        $rows = Get-ListRowNames 'Presets'
        if ($rows -notcontains 'Check Harness Preset, your preset') {
            return "after Refresh the list is [$($rows -join ', ')]"
        }
        # The row says whose preset it is, which is the question a person with their own presets will ask.
        if (-not (Get-ElementNamed 'Refresh presets' 'Button')) { 'the page went away' }
    }

    Test-Case 'the harness preset gets the controls its own manifest declares' {
        Select-ListRow 'Presets' 'Check Harness Preset, your preset'
        $selected = Get-SelectedRow 'Presets'
        if ($selected -ne 'Check Harness Preset, your preset') {
            # Almost always a shader that would not compile, in which case the page has put the selection back
            # and the compiler's own words are in the notice bar - which is worth printing rather than guessing.
            $notice = (Get-NamesOfType 'Text') | Where-Object { $_ -like '*error X*' }
            return "the selection went back to '$selected'; notice: $($notice -join ' / ')"
        }

        $sliders = Get-NamesOfType 'Slider'
        $combos = Get-NamesOfType 'ComboBox'
        # Nothing about this preset exists anywhere in the app: the label, the range, the unit and the three
        # mode names all came off a file written three seconds ago, through mp_renderer_enum_preset_params.
        if (-not ($sliders | Where-Object { $_ -eq 'Harness count, 20' })) {
            return "no 'Harness count, 20' slider; sliders are [$($sliders -join ' | ')]"
        }
        if (-not ($sliders | Where-Object { $_ -eq 'Harness width, 2.5 px' })) {
            return "no 'Harness width, 2.5 px' slider; sliders are [$($sliders -join ' | ')]"
        }
        if (-not ($combos | Where-Object { $_ -eq 'Harness mode, Alpha' })) {
            return "no 'Harness mode, Alpha' list; combo boxes are [$($combos -join ' | ')]"
        }
        foreach ($control in @($sliders) + @($combos)) {
            if ($control -like '*art_*') { return "'$control' was offered; the manifest marks it hidden" }
        }
    }

    Test-Case 'and the visualizer kept drawing through all of it' {
        Send-Keys '^+d'
        $panel = Get-ElementNamed 'Diagnostics' 'Group'
        if (-not $panel) { return 'the diagnostics overlay did not open' }
        $rows = @()
        foreach ($e in $panel.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($e.Current.Name) { $rows += $e.Current.Name }
        }
        $renderer = $rows | Where-Object { $_ -like '*fps*' -or $_ -like '*unavailable*' }
        # Write-Host, not Write-Output: a check block's pipeline output IS its verdict.
        Write-Host "        renderer rows: $($renderer -join ' | ')"
        if ($rows -join ' ' -like '*unavailable*') { 'the renderer is unavailable, so nothing above switched a picture' }
        Send-Keys '^+d'
    }

    # ---- T-151: the two settings E4-S6 shipped with no UI, on Settings > Appearance since E6-S3 --------------------

    Test-Case 'the smoothing slider says what the person is choosing, in seconds' {
        if (-not (& $selectSection 'Appearance settings')) { return 'the settings overlay has no Appearance section' }
        $slider = Get-ElementNamed 'Reactive theming smoothing' 'Slider'
        if (-not $slider) { return 'no smoothing slider' }
        $range = $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $range.SetValue(1.0)
        Start-Sleep -Milliseconds 400
        $texts = @()
        foreach ($e in Get-Elements) { if ($e.Current.Name) { $texts += $e.Current.Name } }
        if (-not ($texts | Where-Object { $_ -like '4.0 s to move 63*' })) {
            return "at 1.0 the label does not name the 4 s time constant"
        }
        $range.SetValue(0.0)
        Start-Sleep -Milliseconds 400
        $texts = @()
        foreach ($e in Get-Elements) { if ($e.Current.Name) { $texts += $e.Current.Name } }
        if (-not ($texts | Where-Object { $_ -like '0.5 s to move 63*' })) {
            return 'at 0 the label does not name the 0.5 s time constant'
        }
    }

    Test-Case 'the theming switch and the smoothing reach settings.json' {
        $toggle = Get-ElementNamed 'Let the theme follow the music'
        if (-not $toggle) { return 'no reactive theming switch' }
        $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 700
        $settingsPath = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
        if (-not (Test-Path $settingsPath)) { return "no settings.json at $settingsPath" }
        $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($null -eq $json.'ui.reactiveTheming') { return 'ui.reactiveTheming was not written' }
        if ($null -eq $json.'ui.reactiveSmoothing') { return 'ui.reactiveSmoothing was not written' }
        Write-Host "        ui.reactiveTheming=$($json.'ui.reactiveTheming') ui.reactiveSmoothing=$($json.'ui.reactiveSmoothing')"
        # Put the switch back where it was found; this is the user's own settings file.
        $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 500
    }

    Test-Case 'the chosen preset is written to viz.preset' {
        if (-not (& $selectSection 'Visualization settings')) { return 'could not return to the Visualization section' }
        Select-ListRow 'Presets' 'Radial Spectrum'
        $settingsPath = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
        $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($json.'viz.preset' -ne 'radial-spectrum') {
            return "viz.preset is '$($json.'viz.preset')' after choosing Radial Spectrum"
        }
    }

    # ---- geometry: where the controls ARE, not only that they exist ----------------------------------------

    Test-Case "the page stays inside the sidebar, clear of the controls bar and of the window edge, at $($widthList -join 'px, ')px" {
        Select-ListRow 'Presets' 'Spectrum Bars'
        foreach ($w in $widthList) {
            $problem = Test-Geometry $w
            if ($problem) { return $problem }
        }
    }

    Test-Case 'nor with the preset that declares the most controls' {
        Select-ListRow 'Presets' 'Ambient Glow'
        Test-Geometry ($widthList | Measure-Object -Minimum).Minimum
    }

    # The same invariant pointed the other way, at the panel on the other side of the boundary. Here because the
    # boundary is only meaningful if both sides hold it, and because the controls panel has a fixed-width control
    # of its own (TransportControls' 90 px volume slider). Until T-182 that panel was a column with a 120 px floor
    # and this case would not have caught its clipping, since UIA clips a control to its panel; since T-182 it is a
    # bar as wide as Now Playing, and check-transport-automation.ps1 measures its controls for clipping properly.
    Test-Case 'and the controls panel keeps its own controls inside itself' {
        Set-WindowSize ($widthList | Measure-Object -Minimum).Minimum 900
        $controlsPanel = Get-ElementNamed 'Playback controls panel'
        if (-not $controlsPanel) { return 'the playback controls panel is not in the tree' }
        $box = Get-Rect $controlsPanel
        $over = @()
        foreach ($c in (Get-PageControls $controlsPanel)) {
            if ($c.Rect.Right -gt ($box.Right + 1) -or $c.Rect.Left -lt ($box.Left - 1)) {
                $over += "$($c.Type) '$($c.Name)' spans $($c.Rect.Left)..$($c.Rect.Right) against a panel of $($box.Left)..$($box.Right)"
            }
        }

        Write-Host "        controls panel $($box.Left)..$($box.Right) ($($box.Width) px)"
        if ($over.Count -gt 0) { "the controls panel's own controls overflow it: " + ($over -join '; ') }
    }

    Test-Case 'and on to Settings > Library, another section of the same overlay' {
        Set-WindowSize 1600 900
        if (-not (& $selectSection 'Library settings')) { return 'the settings overlay has no Library section' }
        if (-not (Get-ElementNamed 'Rescan all' 'Button')) { 'the library settings page did not open' }
    }

    Write-Output ''
    if ($failures.Count -eq 0) {
        Write-Output 'PASS: Settings > Visualization lists, switches, describes and refreshes'
        exit 0
    }

    foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
    exit 1
}
finally {
    # An app that does not exit, or exits with a crash code, fails the run (T-188); exit here overrides the try's exit code.
    $closeProblem = Close-TunqioShell $process $null 20
    if (-not $KeepScratch -and (Test-Path $scratchDir)) { Remove-Item $scratchDir -Recurse -Force }
    if ($hadSettings) { Copy-Item $settingsBackup $settingsFile -Force; Remove-Item $settingsBackup -Force }
    elseif (Test-Path $settingsFile) { Remove-Item $settingsFile -Force }
    if ($closeProblem) { Write-Output "FAIL: $closeProblem"; exit 1 }
}
