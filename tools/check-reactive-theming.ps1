<#
.SYNOPSIS
  T-155, T-156, T-147: the diagnostics overlay tells you whether audio-reactive theming is running, why it is
  not, what colours it is painting, whether those colours are reaching the visualizer's presets, and which
  album the glow's colours came from.

  WHY THIS EXISTS. E4-S6 shipped a feature that is close to unfalsifiable by eye, and was then checked by eye.
  The gradient shows through two LayerFillColorDefaultBrush panels, in colours held to 4.5:1 against the theme's
  foregrounds, eased over a 0.5 to 4 s time constant; the one surface a person actually watches - the
  visualizer - was the one surface it provably could not reach, because nothing handed the shell's renderer to
  ReactiveThemeController. AC-266 asked someone to watch a gradient stop, named no surface, and got the only
  honest answer available: "i toggled the switch but saw no observable difference". A number that stops moving
  is falsifiable. That is what this reads.

  WHAT IT PROVES, and what it cannot. It proves the readout exists and says the right things, and it proves the
  palette is reaching the presets right now by watching the count grow between two reads and dividing by the
  elapsed time. It does NOT throw the Windows animation switch - that writes a machine-wide user setting under
  whoever is at the keyboard (Q-34 on T-57 settled that), so AC-288 stays a human criterion. What it does
  instead is drive the app's own reactive theming switch, which stops the theming by the same path and proves
  the readout moves between the two states.

  A WinUI window captures BLACK in a screenshot, so the tree and the app's own copy-to-clipboard text are the
  evidence, the way T-116 and check-visualization-settings.ps1 established.

  EXIT CODES. 0 every case ran and passed; 1 a case failed; 3 everything that could run passed but some cases
  could NOT be run, each one named, because Windows animation effects are off on this machine. 3 is not a pass:
  a check that could not be made must never look like one that was, which is the whole lesson of AC-266.

  WHAT IT TOUCHES. Nothing of the user's music, and nothing in the library database. It writes
  %LocalAppData%\Tunqio\settings.json - the theming switch is the thing under test - and copies the file aside
  first, putting it back AFTER the app has exited so the shutdown flush cannot land on top of the restore. It
  starts its own Tunqio process and only ever acts on that process id: an app already running is never
  activated, never read and never closed.
.PARAMETER Exe
  The built shell. Defaults to the Debug x64 output.
.PARAMETER Seconds
  How long to give the window before reading. The renderer is created when the SwapChainPanel loads, and the
  theming is started after the audio engine comes up, which is deliberately after the first frame.
.PARAMETER SampleSeconds
  The gap between the two readings of the palette count. Longer is a tighter rate estimate.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 12,
    [int]$SampleSeconds = 4
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, Microsoft.VisualBasic

if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\artifacts\bin\Tunqio.App\debug_win-x64\Tunqio.exe' }
$Exe = (Resolve-Path $Exe -ErrorAction SilentlyContinue).Path
if (-not $Exe) { throw 'The shell is not built; run msbuild Tunqio.sln -p:Configuration=Debug -p:Platform=x64 first.' }

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

if (-not ('TunqioForeground' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TunqioForeground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    public static int ForegroundProcess() {
        int pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid;
    }
}
"@
}

# Activated and then CHECKED. AppActivate can return having done nothing while the window is still coming up,
# and a SendKeys after that goes to whichever window does have focus - which, with a second Tunqio possibly
# running, would mean driving somebody else's app.
function Set-Foreground {
    # Patient, because the commonest reason this fails is not the app: it is somebody using the machine. This
    # script drives the keyboard, so it can only run on a desktop nobody else is holding.
    for ($i = 0; $i -lt 60; $i++) {
        try { [Microsoft.VisualBasic.Interaction]::AppActivate($script:processId) } catch { }
        Start-Sleep -Milliseconds 500
        if ([TunqioForeground]::ForegroundProcess() -eq $script:processId) { return }
    }

    $holder = 'unknown'
    try { $holder = (Get-Process -Id ([TunqioForeground]::ForegroundProcess()) -ErrorAction Stop).ProcessName } catch { }
    throw ("the shell window (process $script:processId) never came to the foreground after 30 s; '$holder' is " +
        'holding it. This script types into the focused window, so it cannot run while the desktop is in use.')
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

function Test-OverlayOpen { return $null -ne (Get-ElementNamed 'Diagnostics' 'Group') }

function Open-Overlay {
    for ($i = 0; $i -lt 3 -and -not (Test-OverlayOpen); $i++) { Send-Keys '^+d' }
    if (-not (Test-OverlayOpen)) { throw 'the diagnostics overlay would not open on Ctrl+Shift+D' }
}

function Close-Overlay {
    for ($i = 0; $i -lt 3 -and (Test-OverlayOpen); $i++) { Send-Keys '^+d' }
}

# The overlay's own Copy button, which is the app saying what it says rather than us reassembling it from
# TextBlocks - and which proves the readout reaches a bug report, not only the screen. Falls back to the tree
# when the clipboard is unavailable (an RDP session, a locked desktop, a run that is not STA), reassembling the
# same shape: alternating label and value under a section heading, which is exactly how the overlay is built.
function Get-Report {
    if (-not (Test-OverlayOpen)) { throw 'the overlay is not open' }
    try {
        Set-Clipboard -Value '-'
        Invoke-Named 'Copy diagnostics'
        $text = Get-Clipboard -Raw
        if ($text -and $text -ne '-' -and $text -match '\[Playback\]') { return $text }
    }
    catch { }

    $panel = Get-ElementNamed 'Diagnostics' 'Group'
    $names = @()
    foreach ($e in $panel.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name) { $names += $e.Current.Name }
    }

    # The headings, in the order Diagnostics.Describe emits them. Everything between two of them is label/value
    # pairs, so the flattened tree turns back into the same tab-separated report.
    $headings = 'Playback', 'Output', 'Renderer', 'Reactive theming', 'Build'
    $lines = @()
    $i = 0
    while ($i -lt $names.Count -and $names[$i] -notin $headings) { $i++ }
    while ($i -lt $names.Count) {
        if ($names[$i] -in $headings) {
            $lines += "[$($names[$i])]"
            $i++
            continue
        }
        if ($i + 1 -lt $names.Count) { $lines += "$($names[$i])`t$($names[$i + 1])" }
        $i += 2
    }

    return ($lines -join "`n")
}

# One labelled row out of the report. The report is tab-separated, one row a line, sections in [brackets]; when
# a label appears in more than one section - "State" is Playback's as well as the theming's - the section is
# what disambiguates, and a caller that does not say which gets the first.
function Get-Row([string]$report, [string]$label, [string]$section) {
    $inSection = -not $section
    foreach ($line in ($report -split "`r?`n")) {
        if ($line -match '^\[(.+)\]$') { $inSection = (-not $section) -or ($Matches[1] -eq $section); continue }
        if (-not $inSection) { continue }
        $parts = $line -split "`t", 2
        if ($parts.Length -eq 2 -and $parts[0] -eq $label) { return $parts[1].Trim() }
    }
    return $null
}

function Get-Theming([string]$report, [string]$label) { return Get-Row $report $label 'Reactive theming' }

# Read, never written. Whether Windows is asking for reduced motion decides which half of this script can run,
# and turning it on would be writing a machine-wide user setting under whoever is at the keyboard - which Q-34
# on T-57 refused for the test suite, for reasons that apply here word for word.
if (-not ('TunqioSpi' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TunqioSpi {
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);
    public static bool ClientAreaAnimation() { bool v = false; SystemParametersInfo(0x1042, 0, ref v, 0); return v; }
}
"@
}

$animations = [TunqioSpi]::ClientAreaAnimation()

$failures = @()
$notRun = @()

function Test-Case([string]$what, [scriptblock]$check) {
    # A throw inside a case is that case failing, not the run ending.
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

$settingsFile = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
$settingsBackup = Join-Path $env:TEMP 'tunqio-check-reactive-settings.json'
$hadSettings = Test-Path $settingsFile
if ($hadSettings) { Copy-Item $settingsFile $settingsBackup -Force }

Write-Output 'Audio-reactive theming, read off the diagnostics overlay of a live window'
Write-Output "  exe        $Exe"
Write-Output "  settings   $settingsFile  (copied aside, restored after the app exits)"
Write-Output "  Windows animation effects  $(if ($animations) { 'on' } else { 'OFF - the running-path cases cannot be run' })"
Write-Output ''

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
    Open-Overlay

    $report = Get-Report

    # ---- AC-286 / AC-287: there is a readout at all --------------------------------------------------------------

    Test-Case 'the overlay has a Reactive theming section, with every row it promises' {
        if ($report -notmatch '\[Reactive theming\]') {
            Write-Host "        report was:`n$report"
            return 'no [Reactive theming] section in the report'
        }
        foreach ($label in 'State', 'Palette', 'Ticks', 'Visualizer', 'Album art') {
            if ($null -eq (Get-Theming $report $label)) { return "no '$label' row" }
        }
        # Before this section existed, DiagnosticsOverlay.xaml and DiagnosticsViewModel.cs mentioned reactive
        # theming nowhere, while MainWindow.ReactiveTheming and ReactiveThemeLayer.Painted were each documented
        # as existing "for the diagnostics overlay". Two properties with a consumer that was never built.
        Write-Host "        Ticks: $(Get-Theming $report 'Ticks')"
    }

    # ---- AC-286: the state, and the reason when there is one -----------------------------------------------------

    Test-Case 'the State row agrees with the switches Windows and the app are actually set to' {
        $state = Get-Theming $report 'State'
        Write-Host "        State: $state"
        if ($animations) {
            if ($state -ne 'running') { return "Windows animations are on and the app's switch is on, but the readout says '$state'" }
        }
        elseif ($state -ne 'stopped: Windows is asking for reduced motion') {
            return "Windows animations are off, so the readout should name reduced motion; it says '$state'"
        }
    }

    Test-Case 'the Palette row shows what is painted, or says plainly that nothing is' {
        $palette = Get-Theming $report 'Palette'
        Write-Host "        Palette: $palette"
        if ($animations) {
            if ($palette -notmatch 'primary #[0-9a-f]{6} . secondary #[0-9a-f]{6} . accent #[0-9a-f]{6} . background #[0-9a-f]{6}') {
                return "the Palette row reads '$palette'"
            }
        }
        elseif ($palette -notlike 'none*') {
            return "nothing is being painted, but the Palette row reads '$palette'"
        }
    }

    Test-Case 'the readout never asserts a renderer state it has not looked at' {
        # It said "the renderer is not attached" beside a Renderer section reporting 144 fps on this harness's
        # first live run, which is the readout being wrong in the way this task exists to stop.
        $told = Get-Theming $report 'Visualizer'
        $rendererMissing = Get-Row $report 'Renderer' 'Renderer'
        Write-Host "        Visualizer: $told"
        if (-not $rendererMissing -and $told -like '*renderer is not attached*') {
            return "the theming says '$told' while the Renderer section reports $(Get-Row $report 'Frame rate' 'Renderer')"
        }
    }

    # ---- T-156: the colours are reaching the presets, now ---------------------------------------------------------

    $runningPath = {
        $report = Get-Report
        $first = Get-Theming $report 'Visualizer'
        Write-Host "        Visualizer: $first"
        if ($first -notmatch '^(\d+) palettes sent$') {
            return "the visualizer is not being told: '$first'"
        }
        $before = [int]$Matches[1]
        if ($before -le 0) { return 'nothing has ever been sent' }

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $SampleSeconds
        $later = Get-Theming (Get-Report) 'Visualizer'
        $watch.Stop()
        if ($later -notmatch '^(\d+) palettes sent$') { return "the visualizer stopped being told: '$later'" }
        $after = [int]$Matches[1]
        $rate = ($after - $before) / $watch.Elapsed.TotalSeconds
        Write-Host ("        {0} -> {1} in {2:F1} s = {3:F1} palettes/s (the poll is 30 Hz)" -f $before, $after, $watch.Elapsed.TotalSeconds, $rate)
        if ($after -le $before) { return "the count did not move in $($watch.Elapsed.TotalSeconds) s" }
        # Generous either side of 30: this is "it is running at about the poll rate", not a perf gate.
        if ($rate -lt 15 -or $rate -gt 45) { return ("the rate is {0:F1}/s, nowhere near the 30 Hz poll" -f $rate) }
    }

    if ($animations) {
        Test-Case 'the theme is reaching the visualizer, and the count is still climbing' $runningPath
    }
    else {
        $notRun += 'the theme is reaching the visualizer, and the count is still climbing'
    }

    Test-Case 'the visualizer that would be themed is actually drawing' {
        # Otherwise "the theme reached the presets" could be true of a renderer nobody can see.
        $now = Get-Report
        $fps = Get-Row $now 'Frame rate' 'Renderer'
        $missing = Get-Row $now 'Renderer' 'Renderer'
        Write-Host "        Frame rate: $fps   Renderer: $missing"
        if ($missing) { return "the renderer is $missing, so nothing is on screen to be themed" }
        if ($fps -notmatch '^([\d.]+) fps$') { return "no frame rate row; got '$fps'" }
        if ([double]$Matches[1] -le 0) { return "the visualizer is drawing at $fps" }
    }

    # ---- T-147: the glow's colours come from the album -----------------------------------------------------------

    Test-Case 'the album art row says which album the colours came from' {
        $art = Get-Theming $report 'Album art'
        Write-Host "        Album art: $art"
        # Nothing is playing in this harness, so "none" is the correct and expected answer; what is under test is
        # that the row exists and is specific rather than blank. The colours themselves are asserted headless in
        # VisualizerArtLinkTests, which can put a track with known art in front of it.
        if (-not $art) { return 'the Album art row is empty' }
        if ($art -notmatch '^(none \(this track has no art\)|[0-9a-f]{1,8} . )') { return "the row reads '$art'" }
    }

    # ---- AC-288's automatable half: the readout moves between the two states ---------------------------------------
    #
    # AC-288 itself is a person throwing the WINDOWS switch, which this script deliberately does not touch. What
    # it can do is throw the app's own switch, which stops the theming by the same path through the same
    # controller, and show the readout move - so what is left to the human is the OS leg and nothing else.

    $toggleOffCase = {
        Close-Overlay
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            Send-Keys '^{,}'
            if (Get-ElementNamed 'Visualization settings' 'Button') { break }
            Start-Sleep -Milliseconds 800
        }
        if (-not (Get-ElementNamed 'Visualization settings' 'Button')) { return 'Settings would not open' }
        Invoke-Named 'Visualization settings'
        $toggle = Get-ElementNamed 'Let the theme follow the music'
        if (-not $toggle) { return 'no reactive theming switch on the Visualization page' }
        $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 900

        Open-Overlay
        $off = Get-Report
        $state = Get-Theming $off 'State'
        $palette = Get-Theming $off 'Palette'
        Write-Host "        State: $state"
        Write-Host "        Palette: $palette"
        if ($state -ne 'stopped: ui.reactiveTheming is off') { return "the readout still says '$state'" }
        if ($palette -notlike 'none*') { return "the palette row still shows colours: '$palette'" }
    }

    $toggleOnCase = {
        Close-Overlay
        $toggle = Get-ElementNamed 'Let the theme follow the music'
        if (-not $toggle) { return 'the switch is no longer on screen' }
        $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 900

        Open-Overlay
        $on = Get-Report
        $state = Get-Theming $on 'State'
        $told = Get-Theming $on 'Visualizer'
        Write-Host "        State: $state"
        Write-Host "        Visualizer: $told"
        if ($state -ne 'running') { return "the readout says '$state'" }
        if ($told -notmatch '^\d+ palettes sent$') { return "the visualizer is not being told again: '$told'" }
    }

    if ($animations) {
        Test-Case 'turning the app''s theming switch off moves the readout from running to stopped, with the reason' $toggleOffCase
        Test-Case 'and turning it back on moves it back, with the visualizer told again' $toggleOnCase
    }
    else {
        $notRun += 'turning the app''s theming switch off moves the readout from running to stopped, with the reason'
        $notRun += 'and turning it back on moves it back, with the visualizer told again'
    }

    Write-Output ''
    foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
    if ($failures.Count -gt 0) { exit 1 }

    if ($notRun.Count -gt 0) {
        # Never a silent skip. AC-266 failed because a check that could not be made looked like one that had
        # been; a green exit with cases unrun would be the same mistake in a script.
        Write-Output 'PARTIAL: everything that could be checked passed, but these cases were NOT RUN:'
        foreach ($case in $notRun) { Write-Output "  not run: $case" }
        Write-Output ''
        Write-Output 'Windows animation effects are off on this machine, so reactive theming is stopped and there'
        Write-Output 'is nothing to measure reaching the presets. Turn on Settings > Accessibility > Visual effects'
        Write-Output '> Animation effects and run this again. This script will not turn it on for you: it is a'
        Write-Output 'machine-wide user setting, and Q-34 on T-57 settled that a check may not write one.'
        exit 3
    }

    Write-Output 'PASS: the theming is running, it is reaching the presets, and the readout moves when it stops'
    exit 0
}
finally {
    # Only ever this process. A Tunqio that was already running is none of this script's business.
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 3 }
    if (-not $process.HasExited) { $process.Kill() }
    Start-Sleep -Milliseconds 500
    if ($hadSettings) { Copy-Item $settingsBackup $settingsFile -Force; Remove-Item $settingsBackup -Force }
    elseif (Test-Path $settingsFile) { Remove-Item $settingsFile -Force }
}
