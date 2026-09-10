<#
.SYNOPSIS
  E2-S2 AC-70: every transport control is reachable by keyboard and named for Narrator.

  Launches the shell and reads its automation tree the way Narrator does, rather than asking the app what it
  believes about itself. A glyph is not a name, and a Border with a Tapped handler is not a button: both of those
  mistakes look right on screen and are invisible until someone tabs through, which is why this is a real UIA walk
  and not an assertion inside the app.

  Names carrying state ("Shuffle off", "Repeat all") are checked as prefixes, since which state the app happens to
  be in when the script runs is not the point.
.PARAMETER Exe
  The built shell. Defaults to the Debug x64 output.
.PARAMETER Seconds
  How long to give the window before reading the tree.
#>
[CmdletBinding()]
param(
    [string]$Exe = "$PSScriptRoot\..\artifacts\bin\Tunqio.App\debug_win-x64\Tunqio.exe",
    [int]$Seconds = 9
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not (Test-Path $Exe)) { throw "$Exe not found; build the app first." }

# Prefix, control type. One row per control the transport panel owes the keyboard and Narrator.
$expected = @(
    @{ Name = 'Previous track'; Type = 'Button' },
    @{ Name = 'Play';           Type = 'Button' },   # or Pause, depending on state
    @{ Name = 'Next track';     Type = 'Button' },
    @{ Name = 'Elapsed time';   Type = 'Button' },
    @{ Name = 'Seek';           Type = 'Slider' },
    @{ Name = 'Shuffle';        Type = 'Button' },
    @{ Name = 'Repeat';         Type = 'Button' },
    @{ Name = 'Mute';           Type = 'Button' },   # or Unmute
    @{ Name = 'Volume';         Type = 'Slider' }
)

$process = Start-Process $Exe -PassThru
try {
    Start-Sleep -Seconds $Seconds
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $window) { throw 'The shell window never appeared in the automation tree.' }

    $found = @{}
    $all = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        $type = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if ($type -notin @('Button', 'Slider')) { continue }
        $found[$element.Current.Name] = [pscustomobject]@{
            Type      = $type
            Enabled   = $element.Current.IsEnabled
            Focusable = $element.Current.IsKeyboardFocusable
        }
    }

    $failures = @()
    foreach ($want in $expected) {
        $match = $found.Keys | Where-Object { $_ -like "$($want.Name)*" } | Select-Object -First 1
        if (-not $match) {
            $failures += "no control named '$($want.Name)...' in the automation tree"
            continue
        }

        $control = $found[$match]
        $state = if ($control.Enabled) { 'enabled' } else { 'disabled' }
        Write-Output ("  {0,-8} '{1}'  {2}, focusable={3}" -f $control.Type, $match, $state, $control.Focusable)
        if ($control.Type -ne $want.Type) {
            $failures += "'$match' is a $($control.Type); Narrator needs a $($want.Type) to announce it correctly"
        }
        # A disabled control is deliberately unreachable — the scrubber is, with nothing loaded — so focusability
        # is only owed by the ones that are actually offered.
        if ($control.Enabled -and -not $control.Focusable) {
            $failures += "'$match' is enabled but cannot be reached by keyboard"
        }
    }

    Write-Output ''
    if ($failures.Count -eq 0) {
        Write-Output "PASS: $($expected.Count) transport controls, each named for Narrator and reachable when enabled"
        exit 0
    }

    foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
    exit 1
}
finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 3 }
    if (-not $process.HasExited) { $process.Kill() }
}
