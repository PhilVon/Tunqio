<#
.SYNOPSIS
  E6-S4: Settings > Shortcuts in a running shell, UIA only. Opens the overlay from the controls bar's Settings button,
  selects Shortcuts, and checks that every row of the shell's shortcut table is on the page with its default key, that
  every button on the page carries an automation name, that Reset is there, and that the page's controls sit inside
  the settings surface at a wide window and at two narrow ones (the icon strip at 716 px and the menu button at
  560 px, where T-69's review found the menu over the page titles).

  UIA patterns only: no keystrokes and no pointer. Rebinding is the one thing this page does that cannot be driven
  without typing, and a keystroke harness refuses while someone is at the keyboard; the rebinding rules are
  KeyChordTests, ShortcutBindingsTests and ShortcutsSettingsViewModelTests in Tunqio.App.Tests.

  WHAT IT CHANGES. Nothing. No binding is changed, settings.json is not written, and nothing is played. It resizes
  the window it launched and closes it.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER Seconds
  How long to give the window before driving it.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else started to go away, checking every 30 s, before refusing (T-196).
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10,
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

if (-not (Wait-TunqioExited -WaitMinutes $WaitMinutes)) {
    throw "Tunqio is still running after $WaitMinutes minute(s). This script drives the instance it launches and will not touch one somebody is using."
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$logDir = Join-Path $env:LOCALAPPDATA 'Tunqio\logs'
$script:failures = @()

# The rows the check reads by name, with the default key each one is documented with (docs/ui-screens-and-flows.md,
# "Keyboard shortcuts"). A sample, not the table: the table's completeness is checked by counting the Change buttons
# against the row count the page logs, so a command added to the table is counted without being listed here.
$expectedRows = @(
    @('Play / Pause', 'playPause', 'Space'),
    @('Next track', 'next', 'Ctrl+Right'),
    @('Previous track', 'previous', 'Ctrl+Left'),
    @('Seek back 30 s', 'seekBack30', 'Shift+Left'),
    @('Volume up', 'volumeUp', 'Up'),
    @('Mute', 'mute', 'M'),
    @('Mini player', 'miniPlayer', 'Ctrl+M'),
    @('Settings', 'openSettings', 'Ctrl+,'),
    @('Next preset', 'nextPreset', 'Ctrl+V'),
    @('Diagnostics overlay', 'diagnostics', 'Ctrl+Shift+D')
)

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 200
    }
    throw "waited ${seconds}s and $what never happened"
}

function Find-Named($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)))
}
function Find-ById($scope, [string]$id) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}
function Find-All($scope) { @($scope.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) }
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

# Scrolls the settings surface to the top or the bottom; the ScrollViewer answers the Scroll pattern.
function Set-SurfaceScroll($surface, [double]$percent) {
    $scroll = $surface.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $percent)
    Start-Sleep -Milliseconds 600
}

# The page's buttons and key texts that are on screen right now, each with its rect, for the geometry checks.
function Get-PageControls($surface) {
    $items = @()
    foreach ($e in (Find-All $surface)) {
        $type = $e.Current.ControlType.ProgrammaticName
        $name = $e.Current.Name
        $id = $e.Current.AutomationId
        $isButton = $type -eq 'ControlType.Button'
        $isBinding = $type -eq 'ControlType.Text' -and $id -like 'Binding.*'
        if (-not ($isButton -or $isBinding)) { continue }
        $rect = Get-UiaRect $e
        if ($rect.Offscreen) { continue }
        $label = if ($isBinding) { $id } else { $name }
        $items += [pscustomobject]@{ Type = $type; Name = $label; Rect = $rect }
    }
    return $items
}

# Every on-screen control inside the surface's rectangle; a problem string, or $null.
function Test-Inside($surface, [int]$width, [string]$where) {
    $box = Get-UiaRect $surface
    $controls = @(Get-PageControls $surface)
    if ($controls.Count -eq 0) { return "at ${width}px ($where) no control of the page is on screen" }
    $over = @()
    foreach ($c in $controls) {
        $problem = Test-UiaInside $c.Rect $box "'$($c.Name)'" 'the settings surface'
        if ($problem) { $over += $problem }
    }
    if ($over.Count -gt 0) { return "at ${width}px ($where): " + (($over | Sort-Object -Unique) -join '; ') }
    # Write-Host, not Write-Output: this function's output IS its answer, and a note on the pipeline reads as a problem.
    Write-Host "  note  at ${width}px ($where) $($controls.Count) control(s) on screen inside the surface $($box.Describe)"
    return $null
}

$process = $null
$window = $null
$reachedEnd = $false
$changeCount = 0
$startedAt = Get-Date

try {
    Write-Output "shell: $Exe"
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    Set-UiaWindowSize -ProcessId $process.Id -Width 1616 -Height 900

    # ---- open Settings > Shortcuts ------------------------------------------------------------------------------------
    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 10 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    Start-Sleep -Milliseconds 800
    $item = Find-Named $overlay 'Shortcuts settings'
    Check 'The overlay lists a Shortcuts section after Visualization' ($null -ne $item) $(if ($item) { 'found the section item' } else { 'no Shortcuts settings item' })
    if (-not $item) { throw 'no Shortcuts section to open' }
    $items = @(Find-All $overlay | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name -like '* settings' } | ForEach-Object { $_.Current.Name })
    $vizIndex = [array]::IndexOf($items, 'Visualization settings')
    $shortcutsIndex = [array]::IndexOf($items, 'Shortcuts settings')
    Check 'Shortcuts comes straight after Visualization in the section list' ($vizIndex -ge 0 -and $shortcutsIndex -eq $vizIndex + 1) ($items -join ', ')
    Select-Element $item
    $surface = Wait-Until { Find-Named $overlay 'Shortcuts settings surface' } 10 'the Shortcuts page opened'
    Start-Sleep -Milliseconds 800
    $reset = Find-Named $surface 'Reset all shortcuts'
    Check 'The page has its Reset button' ($null -ne $reset) $(if ($reset) { "enabled: $($reset.Current.IsEnabled)" } else { 'no Reset all shortcuts button' })

    # ---- rows -----------------------------------------------------------------------------------------------------------
    $buttons = @(Find-All $surface | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })
    $changeButtons = @($buttons | Where-Object { $_.Current.Name -like 'Change *' })
    $clearButtons = @($buttons | Where-Object { $_.Current.Name -like 'Clear *' })
    $changeCount = $changeButtons.Count
    Check 'Every row has a Change and a Clear button' ($changeCount -ge 20 -and $clearButtons.Count -eq $changeCount) "$changeCount Change, $($clearButtons.Count) Clear"
    $unnamed = @($buttons | Where-Object { -not $_.Current.Name })
    Check 'Every button on the page carries an automation name' ($unnamed.Count -eq 0 -and $buttons.Count -gt 0) "$($buttons.Count) buttons, $($unnamed.Count) without a name"
    foreach ($row in $expectedRows) {
        $name = $row[0]; $id = $row[1]; $key = $row[2]
        $change = Find-Named $surface "Change $name"
        $clear = Find-Named $surface "Clear $name"
        $binding = Find-ById $surface "Binding.$id"
        $shown = if ($binding) { $binding.Current.Name } else { '(no key text)' }
        # Only checked against the default when the row is on it: this check does not change bindings, and Phil's may differ.
        $note = if ($shown -eq $key) { "shows $shown" } else { "shows '$shown' (default is $key; not on its default, or not found)" }
        Check "Row '$name' has its buttons and its key" ($null -ne $change -and $null -ne $clear -and $null -ne $binding) $note
    }

    # ---- geometry: wide ---------------------------------------------------------------------------------------------------
    $problem = Test-Inside $surface 1616 'top'
    Check 'At 1616 px the page controls sit inside the settings surface (top)' ($null -eq $problem) $(if ($problem) { $problem } else { 'inside' })
    Set-SurfaceScroll $surface 100
    $problem = Test-Inside $surface 1616 'bottom'
    $resetRect = Get-UiaRect $reset
    Check 'At 1616 px the page controls sit inside the settings surface (bottom), with Reset on screen' ($null -eq $problem -and -not $resetRect.Offscreen) $(if ($problem) { $problem } else { "Reset at $($resetRect.Describe)" })
    Set-SurfaceScroll $surface 0

    # ---- geometry: narrow (T-69 review: the menu button over the page title) ------------------------------------------
    $readings = @()
    foreach ($narrow in 1616, 716, 560) {
        Set-UiaWindowSize -ProcessId $process.Id -Width $narrow -Height 900
        Start-Sleep -Milliseconds 1200
        $overlay = Find-Named $window 'Settings overlay'
        $surface = Find-Named $overlay 'Shortcuts settings surface'
        if (-not $surface) {
            # In Minimal navigation the section list is behind the menu button; the page stays where it was.
            Check "At $narrow px the Shortcuts page is still showing" $false 'no Shortcuts settings surface'
            continue
        }
        Write-Output "  note  overlay is $((Get-UiaRect $overlay).Width) px wide at a $narrow px window"
        $closeNav = Find-All $overlay | Where-Object { $_.Current.Name -eq 'Close Navigation' -and -not (Get-UiaRect $_).Offscreen } | Select-Object -First 1
        if ($closeNav) { Invoke-Element $closeNav; Start-Sleep -Milliseconds 700 }
        $menuButtons = @(Find-All $overlay | Where-Object { $_.Current.Name -match '^(Open Navigation|Close Navigation)$' -and -not (Get-UiaRect $_).Offscreen })
        $title = Find-All $surface | Where-Object { $_.Current.Name -eq 'Shortcuts' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and -not (Get-UiaRect $_).Offscreen } | Select-Object -First 1
        $overlaps = @()
        foreach ($b in $menuButtons) {
            $br = Get-UiaRect $b
            $tr = Get-UiaRect $title
            $x = [math]::Min($br.Right, $tr.Right) - [math]::Max($br.Left, $tr.Left)
            $y = [math]::Min($br.Bottom, $tr.Bottom) - [math]::Max($br.Top, $tr.Top)
            if ($x -gt 0 -and $y -gt 0) { $overlaps += "'$($b.Current.Name)' $($br.Describe) covers the title $($tr.Describe) by $x x $y px" }
        }
        Check "At $narrow px the section menu leaves the Shortcuts title clear" ($null -ne $title -and $overlaps.Count -eq 0) $(if (-not $title) { 'no title on screen' } elseif ($overlaps.Count) { $overlaps -join '; ' } else { "$($menuButtons.Count) menu button(s), title $((Get-UiaRect $title).Describe)" })
        if ($narrow -ne 1616) {
            $problem = Test-Inside $surface $narrow 'top'
            Check "At $narrow px the page controls sit inside the settings surface" ($null -eq $problem) $(if ($problem) { $problem } else { 'inside' })
        }
        foreach ($c in (Get-PageControls $surface | Where-Object { $_.Type -eq 'ControlType.Button' })) {
            $readings += [pscustomobject]@{ Width = $narrow; Name = $c.Name; Rect = $c.Rect }
        }
    }
    $clipped = @(Get-UiaClippedControls -Readings $readings)
    Check 'No row button is cut off by its column at any width' ($clipped.Count -eq 0 -and $readings.Count -gt 0) $(if ($clipped.Count) { $clipped -join '; ' } else { "$($readings.Count) readings across 1616, 716 and 560 px" })

    Set-UiaWindowSize -ProcessId $process.Id -Width 1616 -Height 900
    Start-Sleep -Milliseconds 1000

    # ---- close ------------------------------------------------------------------------------------------------------------
    $overlay = Find-Named $window 'Settings overlay'
    Invoke-Element (Find-Named $overlay 'Close settings')
    Start-Sleep -Milliseconds 800
    Check 'Close shuts the overlay' ($null -eq (Find-Named $window 'Settings overlay')) 'overlay gone'
    $reachedEnd = $true
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $window 15
    if ($closeProblem) { $script:failures += $closeProblem }
}

# ---- the row count, from the log: read after the app has exited, because the file sink buffers --------------------------
if ($reachedEnd) {
    $log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
    $line = if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'Shortcuts settings page shows (\d+) rows' } | Select-Object -Last 1 }
    $logged = if ($line -and $line -match 'shows (\d+) rows') { [int]$Matches[1] } else { -1 }
    Check 'The page shows one row per row of the shortcut table' ($logged -gt 0 -and $logged -eq $changeCount) "page logged $logged rows, UIA found $changeCount Change buttons"
    $registered = if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'Shell shortcuts registered' } | Select-Object -Last 1 }
    Check 'The shell registered its shortcuts from the bindings' ($null -ne $registered) $(if ($registered) { $registered.Trim() } else { "no line in $logDir" })
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-shortcuts-settings: PASS'
    exit 0
}
Write-Output "check-shortcuts-settings: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
