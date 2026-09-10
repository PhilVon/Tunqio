<#
.SYNOPSIS
  E2-S6 AC-77 and AC-78, and E2-S8's overlay toggle: the shell's shortcuts reach the transport from wherever focus is, and get out of the way
  where the keystroke belongs to something else.

  The table itself is asserted in Tunqio.App.Tests. What cannot be asserted there is the thing the table is *for*:
  whether a key pressed while a button or a list has focus reaches the shell at all. That depends on WinUI's event
  routing, so it is checked by pressing real keys at a real window and reading the answer off the automation tree -
  the transport's names carry their state ("Shuffle off", "Repeat all"), which makes the effect of a keystroke
  something Narrator could have told you.

  Each case is written so that the wrong routing gives a different answer from the right one. Pressing Space with
  the shuffle button focused is the clearest: if the shell does not take Space first, the focused ToggleButton does,
  and shuffle flips. Every case here runs with an empty queue, which is why none of them assert on playing.
.PARAMETER Exe
  The built shell. Defaults to the Debug x64 output.
.PARAMETER Seconds
  How long to give the window before driving it.
#>
[CmdletBinding()]
param(
    [string]$Exe = "$PSScriptRoot\..\artifacts\bin\Tunqio.App\debug_win-x64\Tunqio.exe",
    [int]$Seconds = 9
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, Microsoft.VisualBasic

if (-not (Test-Path $Exe)) { throw "$Exe not found; build the app first." }

$script:window = $null
$script:processId = 0

function Get-Elements {
    $script:window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

# The full name of the first Button whose name starts with $prefix, or $null. Prefixes, because the names that
# matter here are the ones carrying state ("Shuffle off", "Repeat all"), and which state the app is in when the
# script runs is not the point. Buttons only: a tooltip is an element too, and "Shuffle (S)" is not a state.
function Get-NameStarting([string]$prefix) {
    foreach ($element in Get-Elements) {
        if ($element.Current.Name -notlike "$prefix*") { continue }
        if (($element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') -eq 'Button') {
            return $element.Current.Name
        }
    }
    return $null
}

# By name and, where it is given, control type: a text box sits inside a group of the same name with a label of the
# same name beside it, and only one of the three has anything to say about what was typed into it.
function Get-ElementNamed([string]$name, [string]$type) {
    foreach ($element in Get-Elements) {
        if ($element.Current.Name -ne $name) { continue }
        if (-not $type) { return $element }
        if (($element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') -eq $type) { return $element }
    }
    return $null
}

# A slider's position, for the cases that check an arrow key was left to the control that has focus.
function Get-Range([string]$name) {
    (Get-ElementNamed $name 'Slider').GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
}

# What a text box holds, for the cases that check a key was left to the person typing.
function Get-Value([string]$name) {
    (Get-ElementNamed $name 'Edit').GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Set-Foreground {
    [Microsoft.VisualBasic.Interaction]::AppActivate($script:processId)
    Start-Sleep -Milliseconds 300
}

# Focus is moved by pressing Tab until it lands, rather than by AutomationElement.SetFocus, which WinUI's provider
# refuses here with "Target element cannot receive focus". Tab is also nearer the thing being tested: this script
# is about what happens to a key the user pressed, and a user gets focus there by pressing Tab too.
function Set-FocusTo([string]$name) {
    Set-Foreground
    for ($i = 0; $i -lt 40; $i++) {
        if ([System.Windows.Automation.AutomationElement]::FocusedElement.Current.Name -eq $name) { return }
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
        Start-Sleep -Milliseconds 120
    }

    throw "tabbed 40 times and focus never reached '$name'"
}

function Send-Keys([string]$keys) {
    Set-Foreground
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds 600
}

$failures = @()

function Test-Case([string]$what, [scriptblock]$check) {
    $problem = & $check
    if ($problem) {
        $script:failures += "$what - $problem"
        Write-Output "  FAIL  $what"
        Write-Output "        $problem"
    }
    else {
        Write-Output "  ok    $what"
    }
}

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

    # ---- the bare keys reach the shell from a control that would otherwise eat them --------------------------------

    # A closed ComboBox does type-ahead on a letter, exactly as a list does, and unlike the pane's items it is
    # always on screen and can be focused programmatically.
    $typeAhead = 'Sort albums by'

    Test-Case 'S shuffles with a control that does type-ahead focused' {
        Set-FocusTo $typeAhead
        $before = Get-NameStarting 'Shuffle'
        Send-Keys 's'
        $after = Get-NameStarting 'Shuffle'
        if ($before -eq $after) { "shuffle stayed '$before'; the focused control took the key" }
    }

    Test-Case 'R cycles repeat with the same control focused' {
        Set-FocusTo $typeAhead
        $before = Get-NameStarting 'Repeat'
        Send-Keys 'r'
        $after = Get-NameStarting 'Repeat'
        if ($before -eq $after) { "repeat stayed '$before'" }
    }

    Test-Case 'Space is the shell''s even when a ToggleButton has focus' {
        $shuffle = Get-NameStarting 'Shuffle'
        Set-FocusTo $shuffle
        Send-Keys ' '
        $after = Get-NameStarting 'Shuffle'
        if ($shuffle -ne $after) { "shuffle went '$shuffle' to '$after'; the focused button pressed itself" }
    }

    # ---- AC-78's other half: the arrows still belong to whatever is navigating with them ----------------------------

    Test-Case 'Left still moves the focused slider rather than seeking' {
        Set-FocusTo 'Volume'
        $before = Get-Range 'Volume'
        Send-Keys '{LEFT}'
        $after = Get-Range 'Volume'
        if ($after -ge $before) { "the volume stayed at $before; the shell took the arrow key" }
    }

    # ---- AC-77: and none of them fire while someone is typing ------------------------------------------------------

    Test-Case 'S in the search box types an s and does not shuffle' {
        Set-FocusTo 'Search library'
        $before = Get-NameStarting 'Shuffle'
        Send-Keys 's'
        $after = Get-NameStarting 'Shuffle'
        if ($before -ne $after) { "shuffle went '$before' to '$after' while someone was typing" }
        elseif ((Get-Value 'Search library') -notlike '*s*') { 'the s never reached the search box either' }
    }

    Test-Case 'Space in the search box is a space' {
        Set-FocusTo 'Search library'
        Send-Keys '^a{DEL}the mi'
        $typed = Get-Value 'Search library'
        if ($typed -ne 'the mi') { "the search box holds '$typed'; the space did not reach it" }
        Send-Keys '{ESC}'
    }

    # ---- the queue, which is the one shortcut that is not the transport's ------------------------------------------

    Test-Case 'Q opens the queue panel' {
        Set-FocusTo $typeAhead
        Send-Keys 'q'
        if (-not (Get-ElementNamed 'Upcoming tracks' 'List')) { 'the queue panel did not open' }
        Send-Keys '{ESC}'
    }

    # ---- E2-S8: the diagnostics overlay, which is the one chord that still works while typing --------------------

    Test-Case 'Ctrl+Shift+D opens the diagnostics overlay' {
        Set-FocusTo $typeAhead
        Send-Keys '^+d'
        if (-not (Get-ElementNamed 'Copy diagnostics' 'Button')) { 'the overlay did not open' }
    }

    Test-Case 'the overlay is showing live numbers rather than an empty frame' {
        $panel = Get-ElementNamed 'Diagnostics' 'Group'
        if (-not $panel) { return 'no overlay in the tree' }
        $labels = @()
        foreach ($e in $panel.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($e.Current.Name) { $labels += $e.Current.Name }
        }

        foreach ($want in @('Playback', 'Output', 'Renderer', 'Frame rate', 'Underruns')) {
            if ($labels -notcontains $want) { return "the overlay has no '$want' row" }
        }
    }

    Test-Case 'Copy puts the whole report on the clipboard' {
        $button = Get-ElementNamed 'Copy diagnostics' 'Button'
        if (-not $button) { return 'no Copy button to press' }
        $before = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        try {
            $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 600
            $copied = Get-Clipboard -Raw
            foreach ($want in '[Playback]', '[Output]', '[Renderer]', '[Build]') {
                if ($copied -notlike "*$want*") { return "the clipboard has no $want section" }
            }
        }
        finally {
            # The clipboard is the user's, not the test's.
            if ($before) { Set-Clipboard -Value $before } else { Set-Clipboard -Value '' }
        }
    }

    Test-Case 'Ctrl+Shift+D closes it again' {
        Send-Keys '^+d'
        if (Get-ElementNamed 'Copy diagnostics' 'Button') { 'the overlay stayed open' }
    }

    Write-Output ''
    if ($failures.Count -eq 0) {
        Write-Output 'PASS: the shell takes the keys it must take and leaves the ones it must not'
        exit 0
    }

    foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
    exit 1
}
finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 3 }
    if (-not $process.HasExited) { $process.Kill() }
}
