<#
.SYNOPSIS
  E8-S5 (T-84): crash reporting end to end, against SCRATCH data roots. UIA patterns only: no keystrokes, no pointer.

  With diagnostics.crashReporting ON, for each forced crash (managed on a background thread, an exception on the XAML
  thread, and an access violation on a thread inside mpcore.dll through the test-only mp_debug_crash export) it launches
  the shell with --crash-test <kind> and TUNQIO_CRASH_TEST=1, waits for the process to die, and checks a new report folder
  under <data root>\crashes holds a minidump (MDMP header, non-empty), log.txt with 1..200 lines including the crash test
  line, and report.json naming the right source and exception; and that the app's log states the dump's size. It then
  relaunches normally, finds the crash report dialog by UIA, reads the exception, the dump line (size, location, what a dump
  can hold) and the log lines box, and answers it:
    managed -> Delete report, and the folder is gone;
    xaml    -> Delete report, and the folder is gone;
    native  -> Keep report, and the folder is marked kept; a further launch with --export-diagnostics shows no dialog
               (each report is offered once) and the zip carries crashes/<report>/tunqio.dmp.
  With the setting OFF (a second scratch root) it forces a native crash and checks nothing was captured, and that a relaunch
  offers no report.

  Expect an Application Error event in the Windows log for each forced crash. The shell suppresses its own crash dialog for
  a --crash-test process (SetErrorMode, this process only), so nothing appears on the desktop.

  WHAT IT CHANGES. Nothing outside artifacts\check-crash-report\<stamp>, which is deleted at the end unless -Keep. The real
  profile is never opened: every launch passes --data-root, and the script refuses a data root inside it. The variable
  TUNQIO_CRASH_TEST is set only around each crash launch and removed again. No registry, no system setting.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output of this checkout.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else opened to go away, checking once a minute, before refusing.
.PARAMETER Kinds
  Comma-separated crash kinds for the ON pass. A string because under powershell.exe -File a [string[]] collapses.
.PARAMETER Keep
  Keep the scratch folder for inspection.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$WaitMinutes = 10,
    [string]$Kinds = 'managed,xaml,native',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.IO.Compression.FileSystem
. (Join-Path $here 'uia-geometry.ps1')
$kindList = @($Kinds -split ',' | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })

# ---- refuse while somebody's Tunqio is open: once a minute, for at most -WaitMinutes (T-174: every wait has an end) ----
$refuseDeadline = (Get-Date).AddMinutes($WaitMinutes)
while (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    if ((Get-Date) -ge $refuseDeadline) {
        Write-Output "check-crash-report: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (until $($refuseDeadline.ToString('HH:mm')))"
    Start-Sleep -Seconds 60
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-crash-report\$stamp"))
$onRoot = Join-Path $scratch 'on'
$offRoot = Join-Path $scratch 'off'
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'
$dialogTitle = 'Tunqio closed unexpectedly'
$expected = @{
    managed = @{ Source = 'AppDomain'; Type = 'System.InvalidOperationException'; Answer = 'Delete report' }
    xaml    = @{ Source = 'XAML'; Type = 'System.InvalidOperationException'; Answer = 'Delete report' }
    native  = @{ Source = 'Native'; Type = 'Native exception 0xC0000005 (access violation)'; Answer = 'Keep report' }
}

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "waited ${seconds}s and $what never happened"
}
function Find-Named($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)))
}
function Find-ById($scope, [string]$id) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Get-Value($element) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}
function Read-Log([string]$root) {
    $lines = @()
    foreach ($log in @(Get-ChildItem (Join-Path $root 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $lines += @(Get-Content $log.FullName)
    }
    return $lines
}
function Get-Reports([string]$root) {
    return @(Get-ChildItem (Join-Path $root 'crashes') -Directory -ErrorAction SilentlyContinue | Sort-Object Name)
}
function Assert-Scratch([string]$root) {
    $full = [System.IO.Path]::GetFullPath($root)
    if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    return $full
}
function Write-Seed([string]$root, [bool]$crashReporting) {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $value = if ($crashReporting) { 'true' } else { 'false' }
    # A profile that has launched before and has decided the welcome, so no first-run dialog stands in front of the report.
    Set-Content -Path (Join-Path $root 'settings.json') -Encoding ASCII -Value "{ `"app.launchCount`": 3, `"ui.welcomeShown`": true, `"diagnostics.crashReporting`": $value }"
}

# Launches a crash and waits for the process to end. Returns the exit code, or $null when it did not end (it is then killed).
function Invoke-Crash([string]$root, [string]$kind) {
    $full = Assert-Scratch $root
    $env:TUNQIO_CRASH_TEST = '1'
    try { $p = Start-Process $Exe -ArgumentList @('--data-root', "`"$full`"", '--crash-test', $kind) -PassThru }
    finally { Remove-Item Env:\TUNQIO_CRASH_TEST -ErrorAction SilentlyContinue }
    try { $null = $p.Handle } catch { }
    if (-not $p.WaitForExit(90000)) {
        try { $p.Kill(); $p.WaitForExit(5000) | Out-Null } catch { }
        return $null
    }
    $p.WaitForExit()
    return $p.ExitCode
}
function Start-Shell([string]$root, [string[]]$extra = @()) {
    $full = Assert-Scratch $root
    $p = Start-Process $Exe -ArgumentList (@('--data-root', "`"$full`"") + $extra) -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)
    $w = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 40 'the shell window appeared'
    return @{ Process = $p; Window = $w }
}
function Stop-Shell($shell) {
    if (-not $shell) { return }
    $closeProblem = Close-TunqioShell $shell.Process $shell.Window 20
    if ($closeProblem) { $script:failures += $closeProblem }
}

$shell = $null
try {
    Write-Output "shell: $Exe"
    Write-Seed $onRoot $true
    Write-Seed $offRoot $false
    Write-Output "scratch data roots: $onRoot (crash reporting on), $offRoot (off)"

    # ==== setting ON: each crash leaves a report, and the next launch offers it ======================================
    foreach ($kind in $kindList) {
        $want = $expected[$kind]
        if (-not $want) { throw "unknown crash kind '$kind'" }
        Write-Output "-- $kind crash, crash reporting on"
        $before = @(Get-Reports $onRoot | ForEach-Object { $_.Name })
        $code = Invoke-Crash $onRoot $kind
        Check "The $kind crash ended the process" ($null -ne $code -and $code -ne 0) $(if ($null -eq $code) { 'still running after 90 s, killed' } else { 'exit code 0x{0:X8}' -f $code })
        $new = @(Get-Reports $onRoot | Where-Object { $before -notcontains $_.Name })
        Check "The $kind crash left exactly one new report folder" ($new.Count -eq 1) "$($new.Count) new under $(Join-Path $onRoot 'crashes')"
        if ($new.Count -ne 1) { continue }
        $folder = $new[0].FullName
        $dump = Join-Path $folder 'tunqio.dmp'
        $dumpBytes = if (Test-Path $dump) { (Get-Item $dump).Length } else { 0 }
        $magic = ''
        if ($dumpBytes -ge 4) {
            $fs = [System.IO.File]::OpenRead($dump)
            try { $buf = New-Object byte[] 4; $null = $fs.Read($buf, 0, 4); $magic = [System.Text.Encoding]::ASCII.GetString($buf) } finally { $fs.Dispose() }
        }
        Check "The $kind report holds a minidump" ($dumpBytes -gt 0 -and $magic -eq 'MDMP') "$dumpBytes bytes, header '$magic'"
        $logLines = @(Get-Content (Join-Path $folder 'log.txt') -ErrorAction SilentlyContinue)
        # The kind is an enum logged through Microsoft.Extensions.Logging, which the file renders quoted: forcing a "Managed" crash.
        $testLine = @($logLines | Where-Object { $_ -match "Crash test: forcing a `"?$kind`"? crash" })
        Check "The $kind report holds at most 200 log lines, including the crash test line" ($logLines.Count -ge 1 -and $logLines.Count -le 200 -and $testLine.Count -ge 1) "$($logLines.Count) lines, crash test line $(if ($testLine.Count) { 'present' } else { 'missing' })"
        $info = $null
        try { $info = Get-Content (Join-Path $folder 'report.json') -Raw | ConvertFrom-Json } catch { }
        Check "The $kind report names its source and exception" ($info -and $info.source -eq $want.Source -and $info.exceptionType -eq $want.Type) "source '$($info.source)', type '$($info.exceptionType)', message '$($info.message)'"
        if ($kind -eq 'native') {
            Check 'The native report places the fault in mpcore.dll' ($info -and $info.message -like '*in mpcore.dll') "'$($info.message)'"
        }
        $sizeLine = Read-Log $onRoot | Where-Object { $_ -match 'Crash report: minidump of (\d+) bytes' -and $_ -like "*$($new[0].Name)*" } | Select-Object -Last 1
        Check "The app's log states the $kind dump's size" ($null -ne $sizeLine -and $sizeLine -match "of $dumpBytes bytes") "$(if ($sizeLine) { $sizeLine.Substring($sizeLine.IndexOf('Crash report:')) } else { 'no line' })"

        # ---- the next launch offers it ----
        $shell = Start-Shell $onRoot
        $window = $shell.Window
        $dialog = $null
        try { $dialog = Wait-Until { Find-Named $window $dialogTitle } 45 'the crash report dialog appeared' } catch { }
        Check "The launch after the $kind crash shows the crash report dialog" ($null -ne $dialog) $dialogTitle
        if ($dialog) {
            $exceptionText = (Find-ById $dialog 'CrashReportException').Current.Name
            $dumpText = (Find-ById $dialog 'CrashReportDump').Current.Name
            $summaryText = (Find-ById $dialog 'CrashReportSummary').Current.Name
            $choiceText = (Find-ById $dialog 'CrashReportChoice').Current.Name
            $logBox = Find-Named $dialog 'Crash report log lines'
            $logText = if ($logBox) { Get-Value $logBox } else { '' }
            Check 'The dialog names the exception' ($exceptionText -like "$($want.Type)*") "'$exceptionText'"
            Check 'The dialog gives the dump size and location and says what a dump can hold' ($dumpText -like 'A crash dump of * is saved at *tunqio.dmp*' -and $dumpText -like '*file paths and track names*' -and $dumpText -like "*$($new[0].Name)*") "'$dumpText'"
            # A WinUI TextBox holds its line breaks as a bare CR, so count lines on any of CR, LF or CRLF.
            $boxLines = @($logText -split "`r`n|`r|`n")
            Check 'The dialog shows the log lines readably' ($logText -match 'Crash test: forcing' -and $boxLines.Count -eq $logLines.Count) "$($boxLines.Count) lines in the box, $($logLines.Count) in log.txt"
            Check 'The dialog says nothing has left the PC' ($summaryText -like '*Nothing has been sent anywhere*' -and $choiceText -like 'Nothing leaves this PC unless you send it yourself*') "'$summaryText'"
            Invoke-Element (Find-Named $dialog $want.Answer)
            $gone = $false
            try { $gone = Wait-Until { -not (Find-Named $window $dialogTitle) } 10 'the dialog closed' } catch { }
            Check "$($want.Answer) closes the dialog" ([bool]$gone) 'dialog gone'
            Start-Sleep -Milliseconds 800
            if ($want.Answer -eq 'Delete report') {
                Check "Declining (Delete report) removes the $kind report" (-not (Test-Path $folder)) $folder
            }
            else {
                Check "Keep report marks the $kind report as kept" ((Test-Path $dump) -and (Test-Path (Join-Path $folder 'kept'))) $folder
            }
        }
        Stop-Shell $shell
        $shell = $null

        if ($kind -eq 'native' -and (Test-Path (Join-Path $folder 'kept'))) {
            # ---- a kept report is offered once, and goes into the diagnostics zip ----
            $zip = Join-Path $scratch 'diagnostics.zip'
            $shell = Start-Shell $onRoot @('--export-diagnostics', "`"$zip`"")
            $window = $shell.Window
            $written = $false
            try { $written = Wait-Until { Test-Path $zip } 60 'the diagnostics zip was written' } catch { }
            Start-Sleep -Seconds 2
            Check 'A kept report is not offered again' ($null -eq (Find-Named $window $dialogTitle)) 'no dialog on the next launch'
            Stop-Shell $shell
            $shell = $null
            $entries = @()
            if ($written) {
                $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
                try { $entries = @($archive.Entries | ForEach-Object { $_.FullName }) } finally { $archive.Dispose() }
            }
            $prefix = "crashes/$($new[0].Name)/"
            Check 'Export diagnostics carries the kept report with its dump' (($entries -contains ($prefix + 'tunqio.dmp')) -and ($entries -contains ($prefix + 'log.txt')) -and ($entries -contains ($prefix + 'report.json'))) "$(@($entries | Where-Object { $_ -like 'crashes/*' }) -join ', ')"
        }
    }

    # ==== setting OFF: nothing captured, nothing offered ==============================================================
    Write-Output '-- native crash, crash reporting off'
    $code = Invoke-Crash $offRoot 'native'
    Check 'With crash reporting off the native crash still ends the process' ($null -ne $code -and $code -ne 0) $(if ($null -eq $code) { 'still running after 90 s, killed' } else { 'exit code 0x{0:X8}' -f $code })
    Check 'With crash reporting off nothing is captured' ((Get-Reports $offRoot).Count -eq 0 -and -not (Test-Path (Join-Path $offRoot 'crashes'))) "crashes folder $(if (Test-Path (Join-Path $offRoot 'crashes')) { 'exists' } else { 'absent' })"
    $offLine = Read-Log $offRoot | Where-Object { $_ -match 'Crash reporting: off, nothing is captured' } | Select-Object -First 1
    Check 'The log says crash reporting is off' ($null -ne $offLine) "$(if ($offLine) { 'present' } else { 'missing' })"
    $shell = Start-Shell $offRoot
    Start-Sleep -Seconds 8
    Check 'With crash reporting off the next launch offers no report' ($null -eq (Find-Named $shell.Window $dialogTitle)) 'no dialog after 8 s'
    Stop-Shell $shell
    $shell = $null
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    Remove-Item Env:\TUNQIO_CRASH_TEST -ErrorAction SilentlyContinue
    Stop-Shell $shell
    if (-not $Keep -and (Test-Path $scratch)) {
        try { Remove-Item -Recurse -Force $scratch } catch { Write-Output "  note  scratch folder not removed: $($_.Exception.Message)" }
    }
    elseif ($Keep) { Write-Output "  note  kept $scratch" }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-crash-report: PASS'
    exit 0
}
Write-Output "check-crash-report: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
