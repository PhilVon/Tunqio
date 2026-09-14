<#
.SYNOPSIS
  E7-S4 (T-77; AC-157, AC-495, AC-496): the now-playing toast on a scratch profile, proven from the app's log, the system
  media session and a read-only look at the registry. No keystrokes and no pointer.

  One step after another:
    1. reads the registry (read only) for Tunqio's app notification registration; generates four tagged FLAC tones (the
       second 14 s long), seeds ui.toastOnTrackChange and ui.closeToTray on, launches the Release build on the four files,
       mutes the output and waits for Playing and for the log to say it registered; reads the registration it created;
    2. with Tunqio's window in the foreground, presses the transport's Next through UIA: Toast Two plays and the log says no
       toast was shown because a Tunqio window was in the foreground;
    3. closes the window through UIA, which hides it to the tray, and waits for the 14 s track to end: the log says a toast
       was shown for Toast Three, and posts its payload; the notification history (read through WinRT, when Windows allows it)
       holds one toast with the now-playing tag;
    4. presses the payload's Next, Play/Pause and Previous buttons the way the notification platform does: CoCreateInstance of
       the COM activator the registration names and INotificationActivationCallback.Activate with the button's own arguments.
       The media session follows (Toast Four, Paused, Playing), the window stays hidden, and the log routes each press
       through CommandRouter;
    5. brings the window back with tunqio://show, turns the toast switch and close-to-tray off in Settings through UIA, and
       presses Play/Pause again: with no Tunqio registered, COM starts a second Tunqio.exe with ----AppNotificationActivated:,
       which reads the press, redirects it to the running instance (the data root travels in the arguments) and exits with
       code 0, and the media session says Paused;
    6. closes the window through Close-TunqioShell (T-188), which fails the run unless the app exits with code 0, and reads the
       log for every line above, the shutdown order (toasts before the tray icon) and no [FTL] line;
    7. runs Tunqio.exe --unregister-notifications (the SDK's UnregisterAll), which must exit 0, and reads the registry again:
       every key the registration created is gone, and the AppUserModelId and CLSID key lists are what they were in step 1.

  How the toast looks and a real click are AC-497, for a person.

  WHAT IT CHANGES. Files under artifacts\check-toasts\<stamp>, deleted at the end unless -Keep. In the registry, only what
  Windows App SDK's own Register writes for this Tunqio.exe under HKEY_CURRENT_USER (Q-110), removed again in step 7 through the
  SDK; this script writes no registry value itself. It refuses to start while any Tunqio is running (checking once a minute for
  at most -WaitMinutes, at most 10), and it only closes processes it launched or that its own press started.

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

for ($attempt = 0; @(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0; $attempt++) {
    if ($attempt -ge $WaitMinutes) {
        Write-Output "check-toasts: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (attempt $($attempt + 1) of $WaitMinutes)"
    Start-Sleep -Seconds 60
}

if (-not ('TunqioToastPress' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationActivationCallback {
    void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs, IntPtr data, int count);
}
public static class TunqioToastPress {
    // What the notification platform does for a press: activate the app's COM activator and hand it the button's arguments.
    public static void Press(Guid clsid, string appUserModelId, string arguments) {
        Type type = Type.GetTypeFromCLSID(clsid, true);
        object activator = Activator.CreateInstance(type);
        try { ((INotificationActivationCallback)activator).Activate(appUserModelId, arguments, IntPtr.Zero, 0); }
        finally { Marshal.ReleaseComObject(activator); }
    }
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    public static int VisibleWindows(int processId) {
        int visible = 0;
        EnumWindows((h, l) => {
            int pid; GetWindowThreadProcessId(h, out pid);
            if (pid == processId && IsWindowVisible(h) && GetWindowTextLength(h) > 0) { visible++; }
            return true;
        }, IntPtr.Zero);
        return visible;
    }
}
"@
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-toasts\$stamp"))
$dataRoot = Join-Path $scratch 'data'
$music = Join-Path $scratch 'music'
$artist = 'Toast Artist'
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'

if (-not $Ffmpeg) {
    $fetched = Join-Path $here '..\artifacts\ffmpeg\bin\ffmpeg.exe'
    if (Test-Path $fetched) { $Ffmpeg = [System.IO.Path]::GetFullPath($fetched) }
    elseif (Get-Command ffmpeg -ErrorAction SilentlyContinue) { $Ffmpeg = (Get-Command ffmpeg).Source }
    else { throw 'ffmpeg was not found: run tools/fetch-ffmpeg.ps1, put ffmpeg on PATH, or pass -Ffmpeg.' }
}

function New-Tone([string]$out, [int]$hz, [int]$seconds, [string]$title) {
    $argumentLine = ('-nostdin -hide_banner -loglevel error -y -f lavfi -i "sine=frequency={0}:sample_rate=44100:duration={1}" ' +
        '-c:a flac -ac 2 -metadata "title={2}" -metadata "artist={3}" -metadata "album=Toast Check" "{4}"') -f $hz, $seconds, $title, $artist, $out
    $encoder = Start-Process -FilePath $Ffmpeg -ArgumentList $argumentLine -NoNewWindow -PassThru
    $null = $encoder.Handle
    if (-not $encoder.WaitForExit(60000)) { $encoder.Kill(); throw "ffmpeg did not finish $out within 60 s" }
    if ($encoder.ExitCode -ne 0 -or -not (Test-Path $out)) { throw "ffmpeg could not write $out (exit $($encoder.ExitCode))" }
}

# ---- WinRT from Windows PowerShell 5.1: the media session (tools/check-smtc.ps1) and the notification history ------------
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
function Get-SessionTitle {
    $s = Find-TunqioSession
    if (-not $s) { return $null }
    $props = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    return $props.Title
}
function Get-SessionStatus {
    $s = Find-TunqioSession
    if (-not $s) { return 'no session' }
    return "$($s.GetPlaybackInfo().PlaybackStatus)"
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
    $null = $p.Handle
    return $p
}
function Get-UiaWindow([int]$processId) {
    $A::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId)))
}
function Read-Log {
    $lines = @()
    foreach ($file in @(Get-ChildItem (Join-Path $dataRoot 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) {
        try {
            $stream = New-Object System.IO.FileStream($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $reader = New-Object System.IO.StreamReader($stream)
            try { $lines += @($reader.ReadToEnd() -split "`r?`n") } finally { $reader.Dispose() }
        }
        catch { }
    }
    return $lines
}
function Log-Count([string]$pattern) { @(Read-Log | Where-Object { $_ -match $pattern }).Count }
function Read-Setting([string]$key) {
    for ($i = 0; $i -lt 20; $i++) {
        try { return (Get-Content (Join-Path $dataRoot 'settings.json') -Raw -ErrorAction Stop | ConvertFrom-Json).$key }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return '(unreadable)'
}

# ---- the registry, read only ----------------------------------------------------------------------------------------------
$aumidRoot = 'HKCU:\Software\Classes\AppUserModelId'
$clsidRoot = 'HKCU:\Software\Classes\CLSID'
function Get-KeyNames([string]$path) { @(Get-ChildItem $path -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName } | Sort-Object) }
# Tunqio's registration for this Tunqio.exe: the per-path key holding NotificationGUID, the AUMID key it names, and the CLSID
# whose LocalServer32 starts this executable. Every key found is returned by name so step 7 can prove each one gone.
function Get-Registration {
    $found = [ordered]@{ PathKey = $null; Aumid = $null; Clsid = $null; ClsidKey = $null; LocalServer = $null }
    foreach ($key in @(Get-ChildItem $aumidRoot -ErrorAction SilentlyContinue)) {
        $guid = $key.GetValue('NotificationGUID')
        if ($guid -and ($key.PSChildName -match 'Tunqio\.exe')) {
            $found.PathKey = $key.PSChildName
            $found.Aumid = "$guid"
        }
    }
    if ($found.Aumid) {
        $aumidKey = Get-Item (Join-Path $aumidRoot $found.Aumid) -ErrorAction SilentlyContinue
        if ($aumidKey) { $found.Clsid = "$($aumidKey.GetValue('CustomActivator'))" }
    }
    if ($found.Clsid) {
        $server = Get-Item (Join-Path $clsidRoot "$($found.Clsid)\LocalServer32") -ErrorAction SilentlyContinue
        if ($server) { $found.ClsidKey = $found.Clsid; $found.LocalServer = "$($server.GetValue(''))" }
    }
    return New-Object PSObject -Property $found
}
function Show-RegQuery([string]$label, $registration) {
    Write-Output "  reg   $label"
    foreach ($key in @("HKCU\Software\Classes\AppUserModelId\$($registration.PathKey)", "HKCU\Software\Classes\AppUserModelId\$($registration.Aumid)", "HKCU\Software\Classes\CLSID\$($registration.Clsid)", "HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\$($registration.Aumid)")) {
        if ($key -match '\\$') { continue }
        # Through cmd, so reg.exe's "unable to find" on stderr is a line of output rather than a terminating error under Stop.
        $out = & cmd.exe /c "reg query `"$key`" /s 2>&1" | ForEach-Object { "$_" } | Where-Object { $_ -ne '' }
        Write-Output "        > reg query $key /s"
        foreach ($line in $out) { Write-Output "          $line" }
    }
}

# The window is handed back in $script:shown, not returned: Check writes to the pipeline, and a function's return value is
# everything it wrote (check-tray.ps1 learned this first).
function Invoke-Show([string]$label) {
    $script:shown = $null
    $second = Start-Shell ("--data-root {0} tunqio://show" -f (Quote $dataRoot))
    $script:launched += $second
    $exited = $second.WaitForExit(15000)
    Check "$label - the tunqio://show process exits with code 0" ($exited -and $second.ExitCode -eq 0) "exited $exited, code $($second.ExitCode)"
    $null = Try-Until { if ([TunqioToastPress]::VisibleWindows($script:app.Id) -ge 1) { $true } } 10
    $script:shown = Try-Until { Get-UiaWindow $script:app.Id } 10
}

# The button's own arguments from the posted payload, as the platform hands them to the activator.
function Get-ButtonArguments([string]$payload, [string]$label) {
    $pattern = "<action[^>]*content=[""']" + [regex]::Escape($label) + "[""'][^>]*arguments=[""']([^""']*)[""']"
    $m = [regex]::Match($payload, $pattern)
    if (-not $m.Success) {
        $pattern = "<action[^>]*arguments=[""']([^""']*)[""'][^>]*content=[""']" + [regex]::Escape($label) + "[""']"
        $m = [regex]::Match($payload, $pattern)
    }
    if (-not $m.Success) { return $null }
    return [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
}
function Press([string]$label, [string]$arguments) {
    try {
        [TunqioToastPress]::Press([Guid]$script:registration.Clsid, $script:registration.Aumid, $arguments)
        Write-Output "  note  pressed $label through the COM activator ($arguments)"
        return $true
    }
    catch {
        Write-Output "  note  pressing $label raised $($_.Exception.GetType().Name): $($_.Exception.Message)"
        return $false
    }
}

$script:launched = @()
$script:app = $null
$script:registration = $null
$registryBefore = $null
try {
    # ---- 1. the registry before, the audio, the scratch profile, the launch -----------------------------------------------
    $before = Get-Registration
    $aumidKeysBefore = Get-KeyNames $aumidRoot
    $clsidKeysBefore = Get-KeyNames $clsidRoot
    Write-Output "  note  before: $($aumidKeysBefore.Count) AppUserModelId key(s), $($clsidKeysBefore.Count) CLSID key(s); Tunqio.exe registration $(if ($before.Aumid) { "ALREADY PRESENT ($($before.PathKey))" } else { 'none' })"
    Check 'No app notification registration for Tunqio.exe exists before the run' ($null -eq $before.Aumid) "$(if ($before.Aumid) { $before.PathKey } else { 'none' })"

    New-Item -ItemType Directory -Force -Path $dataRoot, $music | Out-Null
    $full = [System.IO.Path]::GetFullPath($dataRoot)
    if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    $tracks = @(
        @{ File = (Join-Path $music '01 - Toast One.flac'); Hz = 330; Seconds = 120; Title = 'Toast One' },
        @{ File = (Join-Path $music '02 - Toast Two.flac'); Hz = 392; Seconds = 14; Title = 'Toast Two' },
        @{ File = (Join-Path $music '03 - Toast Three.flac'); Hz = 440; Seconds = 120; Title = 'Toast Three' },
        @{ File = (Join-Path $music '04 - Toast Four.flac'); Hz = 523; Seconds = 120; Title = 'Toast Four' })
    foreach ($t in $tracks) { New-Tone $t.File $t.Hz $t.Seconds $t.Title }
    [System.IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'), '{ "ui.welcomeShown": false, "ui.toastOnTrackChange": true, "ui.closeToTray": true }')
    Write-Output "  note  generated four tracks (the second 14 s) with $Ffmpeg; seeded ui.toastOnTrackChange and ui.closeToTray on"
    Write-Output "shell: $Exe"
    Write-Output "scratch data root: $dataRoot"

    $fileArgs = ($tracks | ForEach-Object { Quote $_.File }) -join ' '
    $script:app = Start-Shell ("--data-root {0} {1}" -f (Quote $dataRoot), $fileArgs)
    $script:launched += $script:app
    $window = Wait-Until { Get-UiaWindow $script:app.Id } 30 'the shell window appeared'
    $mute = Wait-Until { Find-ById $window 'MuteButton' } 20 'the Mute button appeared'
    $toggle = $mute.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    $playing = Try-Until { if ((Get-SessionTitle) -eq 'Toast One' -and (Get-SessionStatus) -eq 'Playing') { $true } } 30
    Check 'The launch plays Toast One' ($playing -eq $true) "title '$(Get-SessionTitle)', $(Get-SessionStatus)"
    $registered = Try-Until { if ((Log-Count 'Toasts: AppNotificationManager registered') -ge 1) { $true } } 15
    Check 'With ui.toastOnTrackChange seeded on, the app registers at start-up' ($registered -eq $true) "$(Log-Count 'Toasts: AppNotificationManager registered') line(s)"
    $script:registration = Try-Until { $r = Get-Registration; if ($r.Aumid -and $r.LocalServer) { $r } } 10
    if (-not $script:registration) { throw 'the SDK registration for Tunqio.exe was not found in HKCU after Register' }
    Check "The registration's COM activator starts this Tunqio.exe" ($script:registration.LocalServer -like "*$Exe*" -and $script:registration.LocalServer -match '----AppNotificationActivated:') $script:registration.LocalServer
    Show-RegQuery 'created by Register' $script:registration

    # ---- 2. no toast while the window is in the foreground ----------------------------------------------------------------
    # Windows may not give a freshly launched window the foreground (the log says "foreground granted false"), and then Toast One
    # rightly gets a toast. tunqio://show from a second process passes the foreground on (T-74), which step 2 needs.
    Write-Output "  note  toasts for Toast One at launch: $(Log-Count 'Toasts: shown for Toast One') (Windows granted the new window the foreground: $((Log-Count 'main window activated \(foreground granted True\)') -ge 1))"
    if ([TunqioUiaGeometry]::ForegroundProcess() -ne $script:app.Id) {
        Invoke-Show 'foreground for step 2'
        if ($script:shown) { $window = $script:shown }
        $null = Try-Until { if ([TunqioUiaGeometry]::ForegroundProcess() -eq $script:app.Id) { $true } } 5
    }
    $front = [TunqioUiaGeometry]::ForegroundProcess() -eq $script:app.Id
    Check 'Tunqio is the foreground process before the track change' $front "foreground pid $([TunqioUiaGeometry]::ForegroundProcess()), Tunqio $($script:app.Id)"
    Invoke-Element (Wait-Until { Find-ById $window 'NextButton' } 10 'the Next button appeared')
    $two = Try-Until { if ((Get-SessionTitle) -eq 'Toast Two') { $true } } 10
    Check 'Next through UIA plays Toast Two' ($two -eq $true) "title '$(Get-SessionTitle)'"
    Check 'Tunqio is still the foreground process after the press' ([TunqioUiaGeometry]::ForegroundProcess() -eq $script:app.Id) "foreground pid $([TunqioUiaGeometry]::ForegroundProcess())"
    $seen = Try-Until { Read-Log | Where-Object { $_ -match 'Toasts: foreground window' } | Select-Object -Last 1 } 10
    Write-Output "  note  what the rule saw for Toast Two: $(if ($seen) { $seen.Substring($seen.IndexOf('Toasts:')) } else { 'no foreground line' })"
    $none = Try-Until { if ((Log-Count 'Toasts: none for Toast Two: a Tunqio window is in the foreground') -ge 1) { $true } } 10
    Check 'No toast for Toast Two: the log names the foreground window' ($none -eq $true) "$(Log-Count 'Toasts: none for Toast Two') line(s)"
    Check 'And none was shown for it' ((Log-Count 'Toasts: shown for Toast Two') -eq 0) "$(Log-Count 'Toasts: shown for Toast Two') line(s)"

    # ---- 3. hidden to the tray, the next track shows a toast -------------------------------------------------------------
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $hidden = Try-Until { if ([TunqioToastPress]::VisibleWindows($script:app.Id) -eq 0) { $true } } 10
    Check 'A close with close-to-tray on hides the window' ($hidden -eq $true) "$([TunqioToastPress]::VisibleWindows($script:app.Id)) visible window(s)"
    $three = Try-Until { if ((Log-Count 'Toasts: shown for Toast Three by Toast Artist') -ge 1) { $true } } 40
    Check 'When Toast Two ends with the window in the tray, a toast is shown for Toast Three' ($three -eq $true) "title '$(Get-SessionTitle)', $(Log-Count 'Toasts: shown for Toast Three') line(s)"
    $payloadLine = Try-Until { Read-Log | Where-Object { $_ -match 'Toasts: posted notification (\d+); payload' } | Select-Object -Last 1 } 10
    $payload = if ($payloadLine) { $payloadLine.Substring($payloadLine.IndexOf('payload ') + 8) } else { '' }
    Check 'The toast was posted with a notification id' ($payloadLine -match 'posted notification [1-9]\d*;') "$(if ($payloadLine) { $payloadLine.Substring(0, [Math]::Min(80, $payloadLine.Length)) } else { 'no posted line' })"
    Check 'Its payload carries the title and artist' ($payload -match 'Toast Three' -and $payload -match 'Toast Artist') 'title and artist text'
    Check 'Its payload has a picture' ($payload -match '<image[^>]*placement=[''"]appLogoOverride[''"]') "$(if ($payload -match '(<image[^>]*>)') { $Matches[1] } else { 'no image element' })"
    Check 'It is silent' ($payload -match '<audio[^>]*silent=[''"]true[''"]') "$(if ($payload -match '(<audio[^>]*>)') { $Matches[1] } else { 'no audio element' })"
    $argsNext = Get-ButtonArguments $payload 'Next'
    $argsToggle = Get-ButtonArguments $payload 'Play/Pause'
    $argsPrevious = Get-ButtonArguments $payload 'Previous'
    Check 'It has Previous, Play/Pause and Next buttons with their commands' ($argsPrevious -match 'action=previous' -and $argsToggle -match 'action=toggle' -and $argsNext -match 'action=next') "previous '$argsPrevious', toggle '$argsToggle', next '$argsNext'"

    try {
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $history = @([Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory($script:registration.Aumid))
        $ours = @($history | Where-Object { $_.Tag -eq 'now-playing' -and $_.Group -eq 'tunqio' })
        Write-Output "  note  notification history for $($script:registration.Aumid): $($history.Count) toast(s), $($ours.Count) with the now-playing tag and group"
    }
    catch { Write-Output "  note  the notification history could not be read from here ($($_.Exception.Message)); replacement is by tag and group in the payload" }

    # ---- 4. the buttons, pressed the way the platform presses them -------------------------------------------------------
    if (-not $argsNext) { throw 'no Next button arguments in the payload, so nothing can be pressed' }
    $pressesBefore = Log-Count 'Toasts: pressed'
    $null = Press 'Next' $argsNext
    $four = Try-Until { if ((Get-SessionTitle) -eq 'Toast Four') { $true } } 10
    Check 'Pressing Next on the toast plays Toast Four' ($four -eq $true) "title '$(Get-SessionTitle)'"
    Check 'The press reached the running Tunqio and was routed as tunqio://next' ((Log-Count 'Toasts: pressed \(.*\), routed as tunqio://next') -ge 1) "$((Log-Count 'Toasts: pressed') - $pressesBefore) press line(s)"
    Check 'No second process was started for it' (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -eq 1) "$(@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count) Tunqio process(es)"
    Check 'The window stays hidden after a button press' ([TunqioToastPress]::VisibleWindows($script:app.Id) -eq 0) "$([TunqioToastPress]::VisibleWindows($script:app.Id)) visible window(s)"
    $fourShown = Try-Until { if ((Log-Count 'Toasts: shown for Toast Four') -ge 1) { $true } } 10
    Check 'Toast Four gets its own toast, replacing Toast Three (same tag and group)' ($fourShown -eq $true -and (Log-Count 'tag now-playing, group tunqio') -ge 2) "$(Log-Count 'tag now-playing, group tunqio') shown line(s)"

    $null = Press 'Play/Pause' $argsToggle
    $paused = Try-Until { if ((Get-SessionStatus) -eq 'Paused') { $true } } 10
    Check 'Pressing Play/Pause pauses' ($paused -eq $true) (Get-SessionStatus)
    $null = Press 'Play/Pause' $argsToggle
    $resumed = Try-Until { if ((Get-SessionStatus) -eq 'Playing') { $true } } 10
    Check 'Pressing Play/Pause again plays' ($resumed -eq $true) (Get-SessionStatus)
    $null = Press 'Previous' $argsPrevious
    $prev = Try-Until { if ((Log-Count 'Activation: Previous') -ge 1) { $true } } 10
    Check 'Pressing Previous runs the router''s Previous' ($prev -eq $true) "title '$(Get-SessionTitle)'"
    Check 'The window is still hidden after all three buttons' ([TunqioToastPress]::VisibleWindows($script:app.Id) -eq 0) "$([TunqioToastPress]::VisibleWindows($script:app.Id)) visible window(s)"

    # ---- 5. toasts off: a press starts a second process, which redirects and exits 0 --------------------------------------
    Invoke-Show 'tunqio://show for Settings'
    $window = $script:shown
    if (-not $window) { throw 'the main window did not come back after tunqio://show' }
    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 10 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    (Wait-Until { Find-Named $overlay 'Appearance settings' } 10 'the overlay listed Appearance').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $toastSwitch = Wait-Until { Find-Named $overlay 'Show a notification when the track changes' } 10 'the Appearance page offered the toast switch'
    $toastToggle = $toastSwitch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Check 'The toast switch shows the seeded setting (On)' ("$($toastToggle.Current.ToggleState)" -eq 'On') "$($toastToggle.Current.ToggleState)"
    $toastToggle.Toggle()
    $stored = Try-Until { if ((Read-Setting 'ui.toastOnTrackChange') -eq $false) { 'false' } } 10
    Check 'Turning it off writes ui.toastOnTrackChange false' ($stored -eq 'false') "ui.toastOnTrackChange = $(Read-Setting 'ui.toastOnTrackChange')"
    $off = Try-Until { if ((Log-Count 'Toasts: turned off') -ge 1) { $true } } 10
    Check 'The app removes the toast and stops receiving presses' ($off -eq $true) "$(Log-Count 'Toasts: turned off') line(s)"
    $closeToggle = (Wait-Until { Find-Named $overlay 'Close to the tray' } 10 'the close switch').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ("$($closeToggle.Current.ToggleState)" -eq 'On') { $closeToggle.Toggle() }
    $null = Try-Until { if ((Read-Setting 'ui.closeToTray') -eq $false) { 'false' } } 10

    $statusBefore = Get-SessionStatus
    $pidsBefore = @(Get-Process Tunqio -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    $redirectsBefore = Log-Count 'Single instance: received a redirected "?Launch"? activation'
    $handedBefore = Log-Count 'Toasts: a press started this process; handed tunqio://toggle'
    $pressJob = Start-Job -ScriptBlock {
        param($clsid, $aumid, $arguments)
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationActivationCallback2 {
    void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs, IntPtr data, int count);
}
public static class TunqioToastPressJob {
    public static void Press(Guid clsid, string aumid, string arguments) {
        object activator = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true));
        try { ((INotificationActivationCallback2)activator).Activate(aumid, arguments, IntPtr.Zero, 0); }
        finally { Marshal.ReleaseComObject(activator); }
    }
}
"@
        try { [TunqioToastPressJob]::Press([Guid]$clsid, $aumid, $arguments); 'pressed' } catch { "raised $($_.Exception.Message)" }
    } -ArgumentList $script:registration.Clsid, $script:registration.Aumid, $argsToggle
    # The second process, found by pid and its handle opened while it lives, so its exit code can be read (5.1).
    $second = Try-Until { Get-Process Tunqio -ErrorAction SilentlyContinue | Where-Object { $pidsBefore -notcontains $_.Id } | Select-Object -First 1 } 20
    if ($second) { try { $null = $second.Handle } catch { } }
    if (-not (Wait-Job $pressJob -Timeout 40)) { Stop-Job $pressJob }
    Write-Output "  note  the press with no Tunqio registered: $(Receive-Job $pressJob)"
    Remove-Job $pressJob -Force
    Check 'With toasts off, the press makes COM start a second Tunqio.exe' ($null -ne $second) "$(if ($second) { "pid $($second.Id)" } else { 'no new process within 20 s' })"
    if ($second) {
        $exited = $second.WaitForExit(20000)
        Check 'The second process exits' $exited "pid $($second.Id)"
        if ($exited) { Check 'With code 0' ($second.ExitCode -eq 0) ("exit code 0x{0:X8}" -f $second.ExitCode) }
        else {
            # Started by this script's own press, on the scratch root: this script's to close.
            $problem = Close-TunqioShell $second $null 10
            if ($problem) { $script:failures += $problem }
        }
    }
    # The press process is a trampoline (Program.DeliverToastPress): it hands tunqio://toggle to an ordinary launch of Tunqio.exe
    # from its true-cased path, and that launch redirects to the running instance through T-74's key and exits 0 too.
    $handed = Try-Until { if ((Log-Count 'Toasts: a press started this process; handed tunqio://toggle') -gt $handedBefore) { $true } } 10
    Check 'The press process handed tunqio://toggle to an ordinary launch' ($handed -eq $true) "$((Log-Count 'Toasts: a press started this process; handed tunqio://toggle') - $handedBefore) line(s)"
    $redirected = Try-Until { if ((Log-Count 'Single instance: received a redirected "?Launch"? activation') -gt $redirectsBefore) { $true } } 15
    Check 'The running Tunqio received it through single-instance redirection' ($redirected -eq $true) "$((Log-Count 'Single instance: received a redirected "?Launch"? activation') - $redirectsBefore) line(s)"
    $toggled = Try-Until { if ((Get-SessionStatus) -ne $statusBefore) { $true } } 10
    Check 'And Play/Pause took effect' ($toggled -eq $true) "$statusBefore then $(Get-SessionStatus)"
    $settled = Try-Until { if (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -eq 1) { $true } } 20
    Check 'Only the running Tunqio is left: no second instance was started' ($settled -eq $true) "$(@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count) Tunqio process(es)"
    Check 'No second session was started on the data root' ((Log-Count 'session .* started: launch #2') -eq 0) "$(Log-Count 'session .* started: launch #2') line(s)"

    # ---- 6. close for real, then the log ----------------------------------------------------------------------------------
    $window = Get-UiaWindow $script:app.Id
    $problem = Close-TunqioShell $script:app $window 20
    Check 'A close exits Tunqio with code 0' ($null -eq $problem) "$(if ($problem) { $problem } else { 'exit code 0' })"
    if ($problem) { $script:failures += $problem }

    $log = @(Read-Log)
    function Index-Of([string]$pattern) { for ($i = 0; $i -lt $log.Count; $i++) { if ($log[$i] -match $pattern) { return $i } }; return -1 }
    $shutdownToasts = Index-Of 'Shutdown: toasts'
    $stopped = Index-Of 'Toasts: stopped'
    $trayStep = Index-Of 'Shutdown: tray icon'
    $media = Index-Of 'Shutdown: media controls'
    Check 'Shutdown stops the toasts first, then the tray icon and media controls' ($shutdownToasts -ge 0 -and $shutdownToasts -lt $stopped -and $stopped -lt $trayStep -and $trayStep -lt $media) "lines $shutdownToasts, $stopped, $trayStep, $media"
    $fatal = @($log | Where-Object { $_ -match '\[FTL\]' })
    Check 'Nothing fatal was logged' ($fatal.Count -eq 0) "$(if ($fatal.Count) { $fatal[0] } else { 'no [FTL] line' })"
    $failedToasts = @($log | Where-Object { $_ -match 'Toasts: .* failed' })
    Check 'No toast operation failed' ($failedToasts.Count -eq 0) "$(if ($failedToasts.Count) { $failedToasts[0] } else { 'none' })"
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
            if (-not $p.HasExited) { try { $p.Kill(); $p.WaitForExit(5000) | Out-Null } catch { } }
        }
    }

    # ---- 7. remove the registration through the SDK and prove it gone ----------------------------------------------------
    $current = Get-Registration
    if ($script:registration) {
        # A clean exit removed the toast; an app this script had to kill did not. Its own test toasts only, by its own AUMID.
        try {
            [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
            $left = @([Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory($script:registration.Aumid))
            Check 'No toast is left in the notification centre after the exit' ($left.Count -eq 0) "$($left.Count) toast(s)"
            if ($left.Count -gt 0) { [Windows.UI.Notifications.ToastNotificationManager]::History.Clear($script:registration.Aumid) }
        }
        catch { Write-Output "  note  the notification history could not be read or cleared ($($_.Exception.Message))" }
    }
    if ($current.Aumid -or $script:registration) {
        $unregister = Start-Shell ("--unregister-notifications --data-root {0}" -f (Quote $dataRoot))
        $done = $unregister.WaitForExit(30000)
        if (-not $done) { try { $unregister.Kill() } catch { } }
        Check 'Tunqio.exe --unregister-notifications exits with code 0' ($done -and $unregister.ExitCode -eq 0) "exited $done, code $($unregister.ExitCode)"
        $after = Get-Registration
        if ($script:registration) {
            Show-RegQuery 'after UnregisterAll' $script:registration
            $leftPath = Test-Path (Join-Path $aumidRoot $script:registration.PathKey)
            $leftAumid = Test-Path (Join-Path $aumidRoot $script:registration.Aumid)
            $leftClsid = Test-Path (Join-Path $clsidRoot $script:registration.Clsid)
            Check "The AppUserModelId key $($script:registration.Aumid) is gone" (-not $leftAumid) "present $leftAumid"
            Check "The CLSID key $($script:registration.Clsid) is gone" (-not $leftClsid) "present $leftClsid"
            # UnregisterAll keeps the per-path NotificationGUID key by design (so the same Tunqio.exe keeps its identity), and Windows
            # keeps its own per-app Notifications\Settings key; neither is this script's to delete (T-77, Q-110). Reported, not failed.
            Write-Output "  note  kept by the SDK: AppUserModelId\$($script:registration.PathKey) present $leftPath; kept by Windows: Notifications\Settings\$($script:registration.Aumid) present $(Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\$($script:registration.Aumid)")"
        }
        $aumidKeysAfter = Get-KeyNames $aumidRoot
        $clsidKeysAfter = Get-KeyNames $clsidRoot
        if ($aumidKeysBefore) {
            $kept = if ($script:registration) { $script:registration.PathKey } else { '' }
            $diffA = @(Compare-Object $aumidKeysBefore $aumidKeysAfter | Where-Object { $_.InputObject -ne $kept } | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
            $diffC = @(Compare-Object $clsidKeysBefore $clsidKeysAfter | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
            Check 'The AppUserModelId key list is what it was before the run, apart from the per-path key the SDK keeps' ($diffA.Count -eq 0) "$(if ($diffA.Count) { $diffA -join '; ' } else { "$($aumidKeysAfter.Count) key(s)" })"
            Check 'The CLSID key list is what it was before the run' ($diffC.Count -eq 0) "$(if ($diffC.Count) { $diffC -join '; ' } else { "$($clsidKeysAfter.Count) key(s), unchanged" })"
        }
    }
    if (-not $Keep) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
    else { Write-Output "  note  scratch kept at $scratch" }
}

if ($script:failures.Count -gt 0) {
    Write-Output "check-toasts: FAIL ($($script:failures.Count) check(s)): $($script:failures -join '; ')"
    exit 1
}
Write-Output 'check-toasts: PASS'
exit 0
