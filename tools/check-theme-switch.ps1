<#
.SYNOPSIS
  T-69 review repro: the theme switched from Settings > Appearance must repaint the whole shell, the controls bar
  included. Phil, with Windows dark: Light repainted, then Dark or Use Windows setting repainted the settings panel
  and left the controls bar light until a second change. UIA cannot see colours and a WinUI window screenshots
  black, so the app logs what each surface paints with after every switch (MainWindow.LogThemeState) and this
  reads the log after the app exits.

  UIA patterns only: no keystrokes and no pointer. It opens Settings with the controls bar's button, goes to
  Appearance, and chooses Light, Dark, Use Windows setting, Light, Use Windows setting, pausing after each, then puts
  the theme back to the value it found.

  WHAT IT CHANGES. ui.theme on a scratch profile, artifacts\check-theme-switch\<stamp>\data, passed as --data-root and
  deleted at the end unless -KeepScratch; the theme log lines are read from the scratch log. The real
  %LOCALAPPDATA%\Tunqio is never opened, and a data root inside it or inside a package's redirected LocalCache is
  refused (tools/scratch-profile.ps1, T-197). The window flashes between light and dark for about ten seconds.
  Nothing is played.
.PARAMETER KeepScratch
  Leave the scratch profile behind for inspection.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else started to go away, checking every 30 s, before refusing (T-196).
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10,
    [int]$WaitMinutes = 10,
    [switch]$KeepScratch
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $here 'uia-geometry.ps1') # Close-TunqioShell (T-188), Wait-TunqioExited (T-196)
. (Join-Path $here 'scratch-profile.ps1') # New-TunqioScratchProfile (T-197)

if (-not (Wait-TunqioExited -WaitMinutes $WaitMinutes)) {
    throw "Tunqio is still running after $WaitMinutes minute(s). This script changes and restores the theme through the instance it launches."
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
# T-197: a scratch profile, never the real one.
$scratch = New-TunqioScratchProfile -Name 'check-theme-switch'
$settingsPath = $scratch.SettingsPath
$logDir = $scratch.LogsDirectory

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
# Retried: the app writes settings.json as the theme changes, and the first run of this repro read it mid-write.
function Get-StoredTheme {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            if (-not (Test-Path $settingsPath)) { return $null }
            return (Get-Content $settingsPath -Raw -ErrorAction Stop | ConvertFrom-Json).'ui.theme'
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return '(unreadable)'
}

$process = $null
$window = $null
$themeBefore = Get-StoredTheme
$startedAt = Get-Date
$stopped = $null

try {
    Write-Output "ui.theme before: $(if ($null -eq $themeBefore) { '(unset)' } else { $themeBefore })"
    $process = Start-TunqioOnScratch $Exe $scratch
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds

    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 10 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    Select-Element (Wait-Until { Find-Named $overlay 'Appearance settings' } 10 'the overlay listed Appearance')
    Wait-Until { Find-Named $overlay 'Dark' } 10 'the theme choices appeared' | Out-Null

    foreach ($choice in 'Light', 'Dark', 'Use Windows setting', 'Light', 'Use Windows setting') {
        Select-Element (Find-Named $overlay $choice)
        Write-Output "  chose $choice (ui.theme now '$(Get-StoredTheme)')"
        Start-Sleep -Milliseconds 1500
    }
}
catch {
    $stopped = $_.Exception.Message
    Write-Output "  the run stopped: $stopped"
}
finally {
    if ($window) {
        try {
            $open = Find-Named $window 'Settings overlay'
            if ($open) {
                $back = switch ($themeBefore) { 'light' { 'Light' } 'dark' { 'Dark' } default { 'Use Windows setting' } }
                $item = Find-Named $open $back
                if ($item) { Select-Element $item; Start-Sleep -Milliseconds 1000 }
                Write-Output "cleanup: theme back to '$back' (ui.theme is '$(Get-StoredTheme)')"
            }
        }
        catch { Write-Output "WARNING: cleanup did not finish: $($_.Exception.Message). Check Settings > Appearance > Theme." }
    }
    # Fails the run on an app that does not exit, or exits with a crash code (T-188), instead of killing it silently.
    $closeProblem = Close-TunqioShell $process $window 15
    if ($closeProblem -and $null -eq $stopped) { $stopped = $closeProblem }
}

# Read after the app has exited: the file sink buffers.
$log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
$lines = @(if ($log) { Get-Content $log.FullName | Where-Object { $_ -match '\] : Theme ' -or $_ -match 'Theme (applied|after a pass)' } })
foreach ($line in $lines) { Write-Output ($line -replace '^\S+ (\S+) \S+ \[DBG\] \[[^\]]*\] [^:]*: ', '$1 ') }
Remove-TunqioScratchProfile $scratch -Keep:$KeepScratch

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($null -eq $stopped -and $lines.Count -gt 0) {
    Write-Output "check-theme-switch: DONE ($($lines.Count) theme log lines)"
    exit 0
}
Write-Output "check-theme-switch: INCOMPLETE ($(if ($stopped) { $stopped } else { 'no theme lines in the log' }))"
exit 1
