<#
.SYNOPSIS
  E7-S3 (T-76; AC-488, AC-489, AC-490, AC-491, AC-492): close-to-tray, minimise-to-tray, show and a clean exit, proven on a
  scratch profile with UI Automation and the system media session. No keystrokes and no pointer.

  One step after another:
    1. seeds a scratch settings.json with ui.closeToTray and ui.minimizeToTray on (and the first-run welcome already
       decided), launches the Release build on it with two generated 120 s FLAC tones, mutes the output, and waits for the
       transport to offer Pause and for Tunqio's media session to say Playing;
    2. closes the main window through its UIA WindowPattern: the process is still running, it has no visible window, and
       its media session still says Playing with the timeline moving between two reads 7 s apart;
    3. launches a second process with tunqio://show on the same data root (T-74): it exits with code 0 and the window is
       visible again and in the foreground;
    4. minimises the window through UIA: the window is hidden, the process running; tunqio://show again restores it, visible
       and not minimised;
    5. opens Settings > Appearance through UIA, turns Close to the tray off and sees ui.closeToTray false in settings.json,
       then closes the window through Close-TunqioShell (T-188), which fails the run unless the app exits with code 0;
    6. reads the log for the tray lines and the shutdown order (tray icon first, then media controls and audio), and no
       [FTL] line; the media session is gone;
    7. reads the same log for the queue and position being captured at exit: the audio step ran, and neither "Could not
       save the queue" nor an audio teardown timeout or failure was logged. (A relaunch cannot show it here: the two
       files are not in a library, and a saved queue with no library tracks is not restored by design.)

  The tray icon's own menu lives in Explorer's notification area and is not driven here: Exit from it is TrayController's
  unit tests plus the same close path as step 5, and how the icon, menu and tooltip look is AC-493, for a person.

  WHAT IT CHANGES. Nothing outside artifacts\check-tray\<stamp>, deleted at the end unless -Keep. The real profile is never
  opened, nothing is installed or registered, and Explorer is never touched. It refuses to start while any Tunqio is
  running (checking again once a minute for at most -WaitMinutes, at most 10), and it only ever closes the processes it
  launched. Launches are sequential.

  ASCII only, Windows PowerShell 5.1, safe under -File.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER Ffmpeg
  ffmpeg.exe for generating the audio. Defaults to artifacts\ffmpeg\bin (tools/fetch-ffmpeg.ps1), then PATH.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else opened to go away, checking once a minute, before refusing. At most 10.
.PARAMETER Keep
  Keep the scratch folder for inspection.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Ffmpeg,
    [int]$WaitMinutes = 10,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'uia-geometry.ps1')
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Runtime.WindowsRuntime
if ($WaitMinutes -gt 10) { $WaitMinutes = 10 }
if ($WaitMinutes -lt 0) { $WaitMinutes = 0 }

# ---- refuse while anybody's Tunqio is open: once a minute, at most -WaitMinutes times (T-174: every wait has an end) ----
for ($attempt = 0; @(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0; $attempt++) {
    if ($attempt -ge $WaitMinutes) {
        Write-Output "check-tray: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (attempt $($attempt + 1) of $WaitMinutes)"
    Start-Sleep -Seconds 60
}

# Visible top-level windows of one process, by EnumWindows: a window hidden to the tray is not WS_VISIBLE.
if (-not ('TunqioTrayWindows' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TunqioTrayWindows {
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    // Visible, titled top-level windows of the process; iconic ones counted separately.
    public static int[] Count(int processId) {
        int visible = 0, iconic = 0;
        EnumWindows((h, l) => {
            int pid; GetWindowThreadProcessId(h, out pid);
            if (pid == processId && IsWindowVisible(h) && GetWindowTextLength(h) > 0) {
                visible++;
                if (IsIconic(h)) { iconic++; }
            }
            return true;
        }, IntPtr.Zero);
        return new int[] { visible, iconic };
    }
}
"@
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-tray\$stamp"))
$dataRoot = Join-Path $scratch 'data'
$music = Join-Path $scratch 'music'
$artist = 'Tray Artist'
$titleOne = 'Tray One'
$titleTwo = 'Tray Two'
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'

if (-not $Ffmpeg) {
    $fetched = Join-Path $here '..\artifacts\ffmpeg\bin\ffmpeg.exe'
    if (Test-Path $fetched) { $Ffmpeg = [System.IO.Path]::GetFullPath($fetched) }
    elseif (Get-Command ffmpeg -ErrorAction SilentlyContinue) { $Ffmpeg = (Get-Command ffmpeg).Source }
    else { throw 'ffmpeg was not found: run tools/fetch-ffmpeg.ps1, put ffmpeg on PATH, or pass -Ffmpeg.' }
}

# One tagged FLAC tone, with a bounded wait so a stuck encoder cannot hold the run (tools/check-smtc.ps1's approach).
function New-Tone([string]$out, [int]$hz, [int]$seconds, [string]$title) {
    $argumentLine = ('-nostdin -hide_banner -loglevel error -y -f lavfi -i "sine=frequency={0}:sample_rate=44100:duration={1}" ' +
        '-c:a flac -ac 2 -metadata "title={2}" -metadata "artist={3}" -metadata "album=Tray Check" "{4}"') -f $hz, $seconds, $title, $artist, $out
    $encoder = Start-Process -FilePath $Ffmpeg -ArgumentList $argumentLine -NoNewWindow -PassThru
    $null = $encoder.Handle
    if (-not $encoder.WaitForExit(60000)) { $encoder.Kill(); throw "ffmpeg did not finish $out within 60 s" }
    if ($encoder.ExitCode -ne 0 -or -not (Test-Path $out)) { throw "ffmpeg could not write $out (exit $($encoder.ExitCode))" }
}

# ---- WinRT from Windows PowerShell 5.1, for the media session (tools/check-smtc.ps1) --------------------------------------
$asTaskGeneric = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await($operation, [type]$resultType, [int]$seconds = 10) {
    $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($operation))
    if (-not $task.Wait([TimeSpan]::FromSeconds($seconds))) { throw "a WinRT call did not finish within $seconds s" }
    return $task.Result
}
[void][Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$ManagerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]
function Find-TunqioSession {
    $manager = Await ($ManagerType::RequestAsync()) $ManagerType
    foreach ($session in @($manager.GetSessions())) {
        if ($session.SourceAppUserModelId -match 'Tunqio') { return $session }
    }
    return $null
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
# Like Wait-Until, but $null instead of throwing, for a check that reports its own failure.
function Try-Until([scriptblock]$condition, [int]$seconds) {
    try { return (Wait-Until $condition $seconds 'x') } catch { return $null }
}
function Find-Named($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)))
}
function Find-ById($scope, [string]$id) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}
function Quote([string]$text) { '"' + $text + '"' }
function Start-Shell([string]$argumentLine) {
    $p = Start-Process $Exe -ArgumentList $argumentLine -PassThru
    $null = $p.Handle   # Windows PowerShell 5.1: ExitCode reads back empty unless the handle was opened while it was alive.
    return $p
}
function Get-UiaWindow([int]$processId) {
    $A::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId)))
}
function Get-Visible([int]$processId) { ([TunqioTrayWindows]::Count($processId))[0] }
function Read-Setting([string]$key) {
    for ($i = 0; $i -lt 20; $i++) {
        try { return (Get-Content (Join-Path $dataRoot 'settings.json') -Raw -ErrorAction Stop | ConvertFrom-Json).$key }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return '(unreadable)'
}
function Get-LivePosition($session) {
    $t = $session.GetTimelineProperties()
    $position = $t.Position
    if ("$($session.GetPlaybackInfo().PlaybackStatus)" -eq 'Playing') { $position = $position + ([DateTimeOffset]::Now - $t.LastUpdatedTime) }
    return $position
}

# A second process with tunqio://show: it must hand over and exit 0, and the window must come back visible and in front.
# The window is handed back in $script:shown, not returned: Check writes to the pipeline, and a function's return value is
# everything it wrote, so a returned element would arrive mixed with the check lines.
function Invoke-Show([string]$label) {
    $script:shown = $null
    $second = Start-Shell ("--data-root {0} tunqio://show" -f (Quote $dataRoot))
    $script:launched += $second
    $exited = $second.WaitForExit(15000)
    Check "$label - the second process exits" $exited "pid $($second.Id)"
    if ($exited) { Check "$label - with code 0" ($second.ExitCode -eq 0) ("exit code 0x{0:X8}" -f $second.ExitCode) }
    else {
        $problem = Close-TunqioShell $second $null 10
        if ($problem) { $script:failures += $problem }
    }
    $visible = Try-Until { if ((Get-Visible $script:app.Id) -ge 1) { $true } } 10
    Check "$label - the main window is visible again" ($visible -eq $true) "$(Get-Visible $script:app.Id) visible window(s)"
    $front = Try-Until { if ([TunqioUiaGeometry]::ForegroundProcess() -eq $script:app.Id) { $true } } 5
    Check "$label - and in the foreground" ($front -eq $true) "foreground process $([TunqioUiaGeometry]::ForegroundProcess()), Tunqio pid $($script:app.Id)"
    $script:shown = Try-Until { Get-UiaWindow $script:app.Id } 10
}

$script:launched = @()
$script:app = $null
try {
    # ---- the audio and the scratch profile ------------------------------------------------------------------------------
    New-Item -ItemType Directory -Force -Path $dataRoot, $music | Out-Null
    $full = [System.IO.Path]::GetFullPath($dataRoot)
    if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    $trackOne = Join-Path $music '01 - Tray One.flac'
    $trackTwo = Join-Path $music '02 - Tray Two.flac'
    New-Tone $trackOne 330 120 $titleOne
    New-Tone $trackTwo 440 120 $titleTwo
    [System.IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'), '{ "ui.welcomeShown": false, "ui.closeToTray": true, "ui.minimizeToTray": true }')
    Write-Output "  note  generated two 120 s tracks with $Ffmpeg; seeded ui.closeToTray and ui.minimizeToTray on"
    Write-Output "shell: $Exe"
    Write-Output "scratch data root: $dataRoot"
    Check 'No Tunqio media session exists before the app starts' ($null -eq (Find-TunqioSession)) 'none'

    # ---- 1. playing -----------------------------------------------------------------------------------------------------
    $script:app = Start-Shell ("--data-root {0} {1} {2}" -f (Quote $dataRoot), (Quote $trackOne), (Quote $trackTwo))
    $script:launched += $script:app
    $window = Wait-Until { Get-UiaWindow $script:app.Id } 30 'the shell window appeared'
    $mute = Wait-Until { Find-ById $window 'MuteButton' } 20 'the Mute button appeared'
    $toggle = $mute.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    $playing = Try-Until { Find-Named $window 'Pause' } 30
    Check 'The launch plays the two files (the transport offers Pause)' ($null -ne $playing) "$(if ($playing) { 'Pause' } else { 'no Pause within 30 s' })"
    $session = Try-Until { $s = Find-TunqioSession; if ($s -and "$($s.GetPlaybackInfo().PlaybackStatus)" -eq 'Playing') { $s } } 15
    Check 'Tunqio has a media session that says Playing' ($null -ne $session) "$(if ($session) { $session.SourceAppUserModelId } else { 'none' })"
    Write-Output "  note  Tunqio is pid $($script:app.Id), output muted"

    # ---- 2. close to the tray -------------------------------------------------------------------------------------------
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $hidden = Try-Until { if ((Get-Visible $script:app.Id) -eq 0) { $true } } 10
    Check 'A close with close-to-tray on hides the window' ($hidden -eq $true) "$(Get-Visible $script:app.Id) visible window(s)"
    Start-Sleep -Seconds 3
    Check 'The process is still running after the close' (-not $script:app.HasExited) "pid $($script:app.Id) $(if ($script:app.HasExited) { 'exited' } else { 'running' })"
    Check 'UIA finds no window for it' ($null -eq (Get-UiaWindow $script:app.Id)) 'none'
    $session = Find-TunqioSession
    $status = if ($session) { "$($session.GetPlaybackInfo().PlaybackStatus)" } else { 'no session' }
    Check 'The media session still says Playing with the window hidden' ($status -eq 'Playing') $status
    if ($session) {
        $first = Get-LivePosition $session
        Start-Sleep -Seconds 7
        $session = Find-TunqioSession
        $second = if ($session) { Get-LivePosition $session } else { [TimeSpan]::Zero }
        Check 'Playback carries on: the timeline moves between two reads 7 s apart' ($second -gt $first.Add([TimeSpan]::FromSeconds(4))) "position $first then $second"
    }

    # ---- 3. tunqio://show brings it back ------------------------------------------------------------------------------
    Invoke-Show 'tunqio://show after close'
    $window = $script:shown
    if (-not $window) { throw 'the main window did not come back after tunqio://show' }
    Check 'The window still plays after it came back' ($null -ne (Try-Until { Find-Named $window 'Pause' } 10)) 'Pause'

    # ---- 4. minimise to the tray ----------------------------------------------------------------------------------------
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    $hidden = Try-Until { if ((Get-Visible $script:app.Id) -eq 0) { $true } } 10
    Check 'A minimise with minimise-to-tray on hides the window' ($hidden -eq $true) "$(Get-Visible $script:app.Id) visible window(s)"
    Check 'The process is still running after the minimise' (-not $script:app.HasExited) "pid $($script:app.Id)"
    Invoke-Show 'tunqio://show after minimise'
    $window = $script:shown
    if (-not $window) { throw 'the main window did not come back after the second tunqio://show' }
    $counts = [TunqioTrayWindows]::Count($script:app.Id)
    $state = "$($window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Current.WindowVisualState)"
    Check 'It comes back restored, not minimised' ($counts[1] -eq 0 -and $state -ne 'Minimized') "visual state $state, $($counts[1]) iconic"

    # ---- 5. turn close-to-tray off in Settings, then close for real -----------------------------------------------------
    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 10 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    (Wait-Until { Find-Named $overlay 'Appearance settings' } 10 'the overlay listed Appearance').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $closeSwitch = Wait-Until { Find-Named $overlay 'Close to the tray' } 10 'the Appearance page offered Close to the tray'
    $minimiseSwitch = Find-Named $overlay 'Minimise to the tray'
    Check 'Appearance has both tray switches' ($null -ne $closeSwitch -and $null -ne $minimiseSwitch) "close $($null -ne $closeSwitch), minimise $($null -ne $minimiseSwitch)"
    $closeToggle = $closeSwitch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Check 'The close switch shows the seeded setting (On)' ("$($closeToggle.Current.ToggleState)" -eq 'On') "$($closeToggle.Current.ToggleState)"
    $closeToggle.Toggle()
    $stored = Try-Until { if ((Read-Setting 'ui.closeToTray') -eq $false) { 'false' } } 10
    Check 'Turning it off writes ui.closeToTray false' ($stored -eq 'false') "ui.closeToTray = $(Read-Setting 'ui.closeToTray')"

    $problem = Close-TunqioShell $script:app $window 20
    Check 'With close-to-tray off, a close exits Tunqio with code 0 (the shutdown T-188 fixed)' ($null -eq $problem) "$(if ($problem) { $problem } else { 'exit code 0' })"
    if ($problem) { $script:failures += $problem }
    $gone = Try-Until { if (-not (Find-TunqioSession)) { 'gone' } } 15
    Check 'The media session goes when the app exits' ($gone -eq 'gone') "$gone"

    # ---- 6. the log -----------------------------------------------------------------------------------------------------
    $log = @()
    foreach ($file in @(Get-ChildItem (Join-Path $dataRoot 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) { $log += @(Get-Content $file.FullName) }
    function Index-Of([string]$pattern) { for ($i = 0; $i -lt $log.Count; $i++) { if ($log[$i] -match $pattern) { return $i } }; return -1 }
    $shown = @($log | Where-Object { $_ -match 'Tray icon: shown in the notification area \(True\)' })
    $hiddenClose = @($log | Where-Object { $_ -match 'Tray: main window hidden to the tray on close' })
    $hiddenMin = @($log | Where-Object { $_ -match 'Tray: main window hidden to the tray on minimise' })
    $back = @($log | Where-Object { $_ -match 'Tray: main window shown from the tray' })
    $fatal = @($log | Where-Object { $_ -match '\[FTL\]' })
    Check 'The log says the tray icon was created' ($shown.Count -eq 1) "$($shown.Count) line(s)"
    Check 'The log records the hide on close and on minimise' ($hiddenClose.Count -eq 1 -and $hiddenMin.Count -eq 1) "close $($hiddenClose.Count), minimise $($hiddenMin.Count)"
    Check 'The log records the window shown from the tray twice' ($back.Count -eq 2) "$($back.Count) line(s)"
    $trayStep = Index-Of 'Shutdown: tray icon'
    $removed = Index-Of 'Tray icon removed'
    $media = Index-Of 'Shutdown: media controls'
    $audio = Index-Of 'Shutdown: audio'
    $hostGone = Index-Of 'Shutdown: host disposed'
    Check 'Shutdown removes the tray icon first, then media controls, audio and the host' ($trayStep -ge 0 -and $trayStep -lt $removed -and $removed -lt $media -and $media -lt $audio -and $audio -lt $hostGone) "lines $trayStep, $removed, $media, $audio, $hostGone"
    Check 'Nothing fatal was logged' ($fatal.Count -eq 0) "$(if ($fatal.Count) { $fatal[0] } else { 'no [FTL] line' })"

    # ---- 7. the queue and position the shutdown captured ---------------------------------------------------------------
    # PlaybackSession.DisposeAsync saves the queue and position and logs only when that fails; AudioStartup logs a teardown
    # that did not finish or failed. None of them, after the audio step ran, is the capture having happened.
    $saveProblems = @($log | Where-Object { $_ -match 'Could not save the queue|Audio teardown did not finish|Audio teardown failed' })
    Check 'The queue and position were captured at exit (the audio step ran; no save or teardown failure logged)' ($audio -ge 0 -and $saveProblems.Count -eq 0) "$(if ($saveProblems.Count) { $saveProblems[0] } else { "audio step at line $audio, no failure line" })"
}
catch {
    $script:failures += "script error: $($_.Exception.Message)"
    Write-Output "  FAIL  script error: $($_.Exception.Message)"
}
finally {
    foreach ($p in $script:launched) {
        if ($p -and -not $p.HasExited) {
            $problem = Close-TunqioShell $p $null 20
            if ($problem) { $script:failures += $problem }
            # A window hidden to the tray ignores a main-window close; it is this harness's own process, so end it.
            if (-not $p.HasExited) { try { $p.Kill(); $p.WaitForExit(5000) | Out-Null } catch { } }
        }
    }
    if (-not $Keep) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
    else { Write-Output "  note  scratch kept at $scratch" }
}

if ($script:failures.Count -gt 0) {
    Write-Output "check-tray: FAIL ($($script:failures.Count) check(s)): $($script:failures -join '; ')"
    exit 1
}
Write-Output 'check-tray: PASS'
exit 0
