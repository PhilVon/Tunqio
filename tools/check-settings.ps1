<#
.SYNOPSIS
  E6-S3: the settings overlay in a running shell, UIA only. Opens it from the controls bar's Settings button, checks
  it covers the sidebar and not the controls bar, walks its five sections, and on Output reads the device list and
  plays the test tone. On Appearance it switches the theme to the opposite of the one on screen and reads
  ui.theme back, then puts it back. Close shuts the overlay and the sidebar returns.

  UIA patterns only: no keystrokes and no pointer. Ctrl+, and Esc are ShellShortcutsTests and the shortcut harness.

  WHAT IT CHANGES. The test tone is AUDIBLE: a 1.5 s 440 Hz tone at about -12 dBFS through the output device, and it
  is not muted by the app's mute, which is for the music. ui.theme is changed and restored to the value it had. The
  output device, mode and buffer are not touched. Nothing is played.
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
. (Join-Path $here 'uia-geometry.ps1')

if (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Tunqio is already running. This script changes and restores the theme through the instance it launches, so it will not touch one somebody is using.'
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$settingsPath = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
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

function Find-Named($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)))
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

function Get-StoredTheme {
    if (-not (Test-Path $settingsPath)) { return $null }
    return (Get-Content $settingsPath -Raw | ConvertFrom-Json).'ui.theme'
}

function Rect($element) { $element.Current.BoundingRectangle }

$process = $null
$window = $null
$themeBefore = $null
$themeChanged = $false
$reachedEnd = $false
$startedAt = Get-Date

try {
    $themeBefore = Get-StoredTheme
    Write-Output "shell: $Exe"
    Write-Output "ui.theme before: $(if ($null -eq $themeBefore) { '(unset)' } else { $themeBefore })"
    $process = Start-Process $Exe -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds

    # ---- open ---------------------------------------------------------------------------------------------------------
    Check 'The overlay is not in the tree until it is opened' ($null -eq (Find-Named $window 'Settings overlay')) 'collapsed'
    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 10 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    Start-Sleep -Milliseconds 1000
    $controls = Find-Named $window 'Playback controls panel'
    $o = Rect $overlay
    $c = Rect $controls
    Check 'The overlay sits above the controls bar, which stays on screen' ($o.Bottom -le ($c.Top + 1) -and $c.Height -gt 0) ("overlay {0:N0}..{1:N0} tall, controls bar from {2:N0}" -f $o.Top, $o.Bottom, $c.Top)
    Check 'The sidebar is hidden while the overlay is open' ($null -eq (Find-Named $window 'Search library')) 'library search box not in the tree'

    # ---- sections ---------------------------------------------------------------------------------------------------
    foreach ($pair in @(@('Playback settings', 'Gapless playback'), @('Output settings', 'Output device'), @('Library settings', 'Rescan all'), @('Appearance settings', 'Let the theme follow the music'), @('Visualization settings', 'Presets'))) {
        $item = Find-Named $overlay $pair[0]
        if (-not $item) { Check "The overlay lists $($pair[0])" $false 'no such section'; continue }
        Select-Element $item
        $found = $null
        try { $found = Wait-Until { Find-Named $overlay $pair[1] } 8 "$($pair[0]) showed '$($pair[1])'" } catch { }
        Check "$($pair[0]) opens its page" ($null -ne $found) "found '$($pair[1])'"
    }

    # ---- output: devices and the test tone --------------------------------------------------------------------------
    Select-Element (Find-Named $overlay 'Output settings')
    $deviceList = Wait-Until { Find-Named $overlay 'Output device' } 8 'the device list appeared'
    $expand = $deviceList.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 600
    $rows = @($A::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
        Where-Object { $_.Current.ProcessId -eq $process.Id -and $_.Current.Name -like 'System default*' })
    $expand.Collapse()
    Start-Sleep -Milliseconds 300
    Check 'The device list leads with the system default, naming it' ($rows.Count -ge 1) "$(if ($rows.Count) { $rows[0].Current.Name } else { 'no System default row' })"
    $opened = Find-Named $overlay 'OutputOpened'
    if (-not $opened) { $opened = $overlay.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'OutputOpened'))) }
    Check 'The readout says what the output opened at' ($opened -and $opened.Current.Name -match 'Hz .* ch .* ms in flight') "$(if ($opened) { $opened.Current.Name })"
    $toneAt = Get-Date
    Invoke-Element (Find-Named $overlay 'Play test tone')
    Start-Sleep -Milliseconds 2500

    # ---- appearance: the theme is stored and applied ---------------------------------------------------------------
    Select-Element (Find-Named $overlay 'Appearance settings')
    # Searched from the overlay, not from an element named Theme: the section header TextBlock is also named Theme and
    # comes first in the tree, and the first run of this check looked for the radio buttons inside it.
    $target = if ($themeBefore -eq 'dark') { 'Light' } else { 'Dark' }
    Select-Element (Wait-Until { Find-Named $overlay $target } 8 "the theme choices offered $target")
    $themeChanged = $true
    Start-Sleep -Milliseconds 1200
    Check 'Choosing a theme writes ui.theme at once' ((Get-StoredTheme) -eq $target.ToLowerInvariant()) "ui.theme is '$(Get-StoredTheme)'"

    # ---- narrow: the section menu must not cover the page title (T-69 review) ----------------------------------------
    # In LeftMinimal a NavigationView draws its menu button over its content; Phil found it over the settings page
    # titles at a narrow window, as T-182 had found it over the sidebar's. Measured per section at 716 px, where the
    # overlay is about 700 px and its navigation is still the icon strip, and at 560 px, where it goes to the menu
    # button (Minimal, below the 640 px threshold) - the case Phil saw. The log says which mode each width got.
    foreach ($narrow in 716, 560) {
    Set-UiaWindowSize -ProcessId $process.Id -Width $narrow -Height 900
    Start-Sleep -Milliseconds 1200
    $overlay = Find-Named $window 'Settings overlay'
    Write-Output "  note  overlay is $([int](Rect $overlay).Width) px wide at a $narrow px window"
    foreach ($pair in @(@('Playback settings', 'Playback'), @('Output settings', 'Output'), @('Appearance settings', 'Appearance'), @('Visualization settings', 'Visualization'))) {
        $item = Find-Named $overlay $pair[0]
        if (-not $item -or (Get-UiaRect $item).Offscreen) {
            $menu = $overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq 'Open Navigation' } | Select-Object -First 1
            if ($menu) { Invoke-Element $menu; Start-Sleep -Milliseconds 900 }
            $item = Find-Named $overlay $pair[0]
        }
        if (-not $item) { Check "At 716 px $($pair[0]) is reachable" $false 'no section item'; continue }
        Select-Element $item
        Start-Sleep -Milliseconds 1200
        $closeNav = $overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq 'Close Navigation' -and -not (Get-UiaRect $_).Offscreen } | Select-Object -First 1
        if ($closeNav) { Invoke-Element $closeNav; Start-Sleep -Milliseconds 700 }
        $buttons = @($overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match '^(Open Navigation|Close Navigation)$' -and -not (Get-UiaRect $_).Offscreen })
        $title = $overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq $pair[1] -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and -not (Get-UiaRect $_).Offscreen } | Select-Object -First 1
        $overlaps = @()
        foreach ($b in $buttons) {
            $br = Get-UiaRect $b
            $tr = Get-UiaRect $title
            $x = [math]::Min($br.Right, $tr.Right) - [math]::Max($br.Left, $tr.Left)
            $y = [math]::Min($br.Bottom, $tr.Bottom) - [math]::Max($br.Top, $tr.Top)
            if ($x -gt 0 -and $y -gt 0) { $overlaps += "'$($b.Current.Name)' $($br.Describe) covers the title $($tr.Describe) by $x x $y px" }
        }
        Check "At $narrow px the section menu leaves the $($pair[1]) title clear" ($null -ne $title -and $overlaps.Count -eq 0) $(if (-not $title) { 'no title on screen' } elseif ($overlaps.Count) { $overlaps -join '; ' } else { "$($buttons.Count) menu button(s) $(($buttons | ForEach-Object { (Get-UiaRect $_).Describe }) -join ', '), title $((Get-UiaRect $title).Describe)" })
    }
    }
    Set-UiaWindowSize -ProcessId $process.Id -Width 1616 -Height 900
    Start-Sleep -Milliseconds 1000

    # ---- close --------------------------------------------------------------------------------------------------------
    $overlay = Find-Named $window 'Settings overlay'
    Invoke-Element (Find-Named $overlay 'Close settings')
    Start-Sleep -Milliseconds 800
    Check 'Close shuts the overlay and the sidebar comes back' ($null -eq (Find-Named $window 'Settings overlay') -and $null -ne (Find-Named $window 'Search library')) 'overlay gone, library search box back'
    $reachedEnd = $true
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    if ($window) {
        try {
            if ($themeChanged) {
                # Put the theme back through the app, so the settings file is written by the app.
                $open = Find-Named $window 'Settings overlay'
                if (-not $open) { Invoke-Element (Find-Named $window 'Open settings'); Start-Sleep -Milliseconds 800; $open = Find-Named $window 'Settings overlay' }
                Select-Element (Find-Named $open 'Appearance settings')
                Start-Sleep -Milliseconds 800
                $back = switch ($themeBefore) { 'light' { 'Light' } 'dark' { 'Dark' } default { 'Use Windows setting' } }
                Select-Element (Wait-Until { Find-Named $open $back } 8 "the theme choices offered $back")
                Start-Sleep -Milliseconds 1000
                Write-Output "cleanup: theme back to '$back' (ui.theme is '$(Get-StoredTheme)')"
                $closeButton = Find-Named $window 'Close settings'
                if ($closeButton) { Invoke-Element $closeButton }
            }
        }
        catch { Write-Output "WARNING: cleanup did not finish: $($_.Exception.Message). Check Settings > Appearance > Theme." }
    }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $window 15
    if ($closeProblem) { $script:failures += $closeProblem }
}

# ---- the test tone, from the log: read after the app has exited, because the file sink buffers ------------------------
if ($reachedEnd) {
    $log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
    $line = if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'Test tone started on the open output' } | Select-Object -Last 1 }
    Check 'The test tone started on the open output' ($null -ne $line) "$(if ($line) { $line.Trim() } else { "no line in $logDir" })"
    $modes = @(if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'Settings navigation is "?(\w+)"? at (\d+) px' } | ForEach-Object { $Matches[1] + ' at ' + $Matches[2] + ' px' } })
    Write-Output "  note  settings navigation modes seen: $($modes -join '; ')"
    Check 'The narrow step reached the menu-button (Minimal) navigation it is there to measure' ((@($modes -match '^Minimal')).Count -gt 0) "$($modes.Count) mode change(s) logged"
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-settings: PASS'
    exit 0
}
Write-Output "check-settings: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
