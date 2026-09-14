<#
.SYNOPSIS
  E7-S2 (T-75; AC-466, AC-467): the system media transport controls of a running shell, read and pressed from OUTSIDE
  the process through Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager - the same session the
  volume flyout draws and the hardware media keys press. No keystrokes and no pointer.

  It launches the Release build with --data-root on a scratch profile, walks the first-run welcome through UIA to add
  a scratch music folder copied from tests/fixtures/library (one album), mutes the output through the Mute button,
  and invokes the album's tile to start playback. Then, from this PowerShell process:
    - Tunqio's media session exists, found by its source app id (never the machine's current session, which may be a
      browser or another player and is never touched);
    - it names the first track's title, artist and album, says Playing, and has a thumbnail;
    - its timeline moves between two reads a few seconds apart;
    - TryPauseAsync pauses the app (the transport offers Play, the session says Paused) and TryPlayAsync resumes it;
    - TrySkipNextAsync moves the app to the next track (the session's title changes);
    - TryChangePlaybackPositionAsync seeks the app (the timeline jumps).
  After the app exits it reads the log for the bridge's lines.

  WHAT IT CHANGES. Nothing outside the scratch folder under artifacts\check-smtc, deleted at the end unless -Keep. The
  real profile is never opened. It refuses to run while any Tunqio process is open (retrying once a minute for up to
  -WaitMinutes), and it never stops a Tunqio it did not start.

  ASCII only, Windows PowerShell 5.1, safe under -File.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else opened to go away, retrying once a minute, before refusing.
.PARAMETER Keep
  Keep the scratch folder for inspection.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$WaitMinutes = 10,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Runtime.WindowsRuntime

# ---- refuse while somebody's Tunqio is open: once a minute, for at most -WaitMinutes (T-174: every wait has an end) ----
$refuseDeadline = (Get-Date).AddMinutes($WaitMinutes)
while (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    if ((Get-Date) -ge $refuseDeadline) {
        Write-Output "check-smtc: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (until $($refuseDeadline.ToString('HH:mm')))"
    Start-Sleep -Seconds 60
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-smtc\$stamp"))
$dataRoot = Join-Path $scratch 'data'
$music = Join-Path $scratch 'music'
$fixtures = Join-Path $here '..\tests\fixtures\library'
$albumFolder = 'Night Signal - Aurora Lines (2019)'
$albumTile = 'Album Aurora Lines by Night Signal, 2019'
$albumTitle = 'Aurora Lines'
$albumArtist = 'Night Signal'
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'

# ---- WinRT from Windows PowerShell 5.1: IAsyncOperation<T> to a Task, with a timeout on every wait ----------------------
$asTaskGeneric = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await($operation, [type]$resultType, [int]$seconds = 10) {
    $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($operation))
    if (-not $task.Wait([TimeSpan]::FromSeconds($seconds))) { throw "a WinRT call did not finish within $seconds s" }
    return $task.Result
}
[void][Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
[void][Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime]
$ManagerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]
$PropertiesType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties]

function Get-Manager { Await ($ManagerType::RequestAsync()) $ManagerType }
# Tunqio's session by source app id. Unpackaged the id is the executable's name; packaged it is the AUMID, which
# carries the package family name. Either way it names Tunqio, and a browser's or another player's does not.
function Find-TunqioSession($manager) {
    foreach ($session in @($manager.GetSessions())) {
        if ($session.SourceAppUserModelId -match 'Tunqio') { return $session }
    }
    return $null
}
function Get-Properties($session) { Await ($session.TryGetMediaPropertiesAsync()) $PropertiesType }
function Invoke-SessionBool($operation) { [bool](Await $operation ([bool])) }

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
function Set-Value($element, [string]$text) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}
function Read-Log([string]$root) {
    $lines = @()
    foreach ($log in @(Get-ChildItem (Join-Path $root 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) {
        $lines += @(Get-Content $log.FullName)
    }
    return $lines
}
function Find-Tile($window, [string]$name) {
    $tile = Find-Named $window $name
    if ($tile -and $tile.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') { return $tile }
    return $null
}
function Get-Timeline($session) {
    $t = $session.GetTimelineProperties()
    return @{ Position = $t.Position; End = $t.EndTime; Updated = $t.LastUpdatedTime }
}
# The position a reader should show now: the last written position, moved on by the time since it was written while playing.
function Get-LivePosition($session) {
    $t = $session.GetTimelineProperties()
    $position = $t.Position
    if ($session.GetPlaybackInfo().PlaybackStatus -eq 'Playing') {
        $position = $position + ([DateTimeOffset]::Now - $t.LastUpdatedTime)
    }
    return $position
}

$process = $null
try {
    New-Item -ItemType Directory -Force -Path $dataRoot, $music | Out-Null
    Copy-Item -Recurse (Join-Path $fixtures $albumFolder) $music
    $full = [System.IO.Path]::GetFullPath($dataRoot)
    if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    Write-Output "shell: $Exe"
    Write-Output "scratch data root: $dataRoot"

    $manager = Get-Manager
    $others = @($manager.GetSessions() | ForEach-Object { $_.SourceAppUserModelId })
    Write-Output "  note  media sessions before launch: $(if ($others.Count) { $others -join ', ' } else { 'none' })"
    Check 'No Tunqio media session exists before the app starts' ($null -eq (Find-TunqioSession $manager)) 'none'

    $process = Start-Process $Exe -ArgumentList @('--data-root', "`"$full`"") -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'

    # ---- a library of one album, through the first-run welcome --------------------------------------------------------
    $dialog = Wait-Until { Find-Named $window 'Welcome to Tunqio' } 30 'the welcome dialog appeared'
    Set-Value (Find-Named $dialog 'Folder to add') $music
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-Named $dialog 'Add this folder')
    Wait-Until { $n = Find-ById $dialog 'WelcomeFolderNotice'; if ($n -and $n.Current.Name -like 'Added *') { $n } } 15 'the welcome said the folder was added' | Out-Null
    Invoke-Element (Wait-Until { Find-Named $dialog 'Skip all' } 5 'the welcome offered Skip all')
    Wait-Until { -not (Find-Named $window 'Welcome to Tunqio') } 10 'the welcome closed' | Out-Null

    $mute = Wait-Until { Find-ById $window 'MuteButton' } 20 'the Mute button appeared'
    $toggle = $mute.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }

    $tile = Wait-Until { Find-Tile $window $albumTile } 60 "the tile '$albumTile' appeared"
    try { $tile.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
    Invoke-Element $tile
    Wait-Until { Find-Named $window 'Pause' } 15 'the transport offered Pause' | Out-Null
    Write-Output '  note  playback started through the album tile (output muted)'

    # ---- AC-466: the session as Windows sees it ---------------------------------------------------------------------
    $session = Wait-Until { Find-TunqioSession (Get-Manager) } 15 "a media session from Tunqio appeared"
    Write-Output "  note  Tunqio's source app id: '$($session.SourceAppUserModelId)'"
    Check 'Tunqio has a system media session' ($null -ne $session) $session.SourceAppUserModelId

    $properties = Wait-Until { $p = Get-Properties $session; if ($p -and $p.Title) { $p } } 10 'the session named a track'
    Check 'The session names the first track' ($properties.Title -eq 'First Light') "title '$($properties.Title)'"
    Check 'The session names the artist' ($properties.Artist -eq $albumArtist) "artist '$($properties.Artist)'"
    Check 'The session names the album and album artist' ($properties.AlbumTitle -eq $albumTitle -and $properties.AlbumArtist -eq $albumArtist) "album '$($properties.AlbumTitle)', album artist '$($properties.AlbumArtist)'"
    Check 'The session carries the track number' ($properties.TrackNumber -eq 1) "track $($properties.TrackNumber)"
    Check 'The session is music' ("$($properties.PlaybackType)" -eq 'Music') "type '$($properties.PlaybackType)'"
    Check 'The session has a thumbnail' ($null -ne $properties.Thumbnail) "$(if ($properties.Thumbnail) { 'present' } else { 'none' })"

    $info = $session.GetPlaybackInfo()
    Check 'The session says Playing' ("$($info.PlaybackStatus)" -eq 'Playing') "status $($info.PlaybackStatus)"
    $controls = $info.Controls
    Check 'Pause, Next and Previous are offered while an album plays' ($controls.IsPauseEnabled -and $controls.IsNextEnabled -and $controls.IsPreviousEnabled) "pause $($controls.IsPauseEnabled), next $($controls.IsNextEnabled), previous $($controls.IsPreviousEnabled)"

    $first = Get-Timeline $session
    Start-Sleep -Seconds 7
    $second = Get-Timeline $session
    Check 'The timeline has the track length' ($second.End.TotalSeconds -gt 1) "end $($second.End)"
    Check 'The timeline moves between two reads 7 s apart' ($second.Position -gt $first.Position) "position $($first.Position) then $($second.Position)"

    # ---- AC-467: presses from outside reach the session --------------------------------------------------------------
    Check 'TryPauseAsync is accepted' (Invoke-SessionBool ($session.TryPauseAsync())) 'true'
    Wait-Until { Find-Named $window 'Play' } 10 'the transport offered Play after an outside pause' | Out-Null
    Check 'An outside pause pauses the app (the transport offers Play)' ($null -ne (Find-Named $window 'Play')) 'Play'
    $paused = Wait-Until { if ("$($session.GetPlaybackInfo().PlaybackStatus)" -eq 'Paused') { 'Paused' } } 10 'the session said Paused'
    Check 'The session says Paused' ($paused -eq 'Paused') $paused
    Check 'Play is offered while paused' ($session.GetPlaybackInfo().Controls.IsPlayEnabled) "play $($session.GetPlaybackInfo().Controls.IsPlayEnabled)"

    Check 'TryPlayAsync is accepted' (Invoke-SessionBool ($session.TryPlayAsync())) 'true'
    Wait-Until { Find-Named $window 'Pause' } 10 'the transport offered Pause after an outside play' | Out-Null
    $playing = Wait-Until { if ("$($session.GetPlaybackInfo().PlaybackStatus)" -eq 'Playing') { 'Playing' } } 10 'the session said Playing again'
    Check 'An outside play resumes the app' ($playing -eq 'Playing') $playing

    Check 'TrySkipNextAsync is accepted' (Invoke-SessionBool ($session.TrySkipNextAsync())) 'true'
    $next = Wait-Until { $p = Get-Properties $session; if ($p -and $p.Title -and $p.Title -ne 'First Light') { $p } } 10 'the session named another track'
    Check 'An outside Next moves the app to the next track' ($next.Title -eq 'Ion Trail' -and $next.TrackNumber -eq 2) "title '$($next.Title)', track $($next.TrackNumber)"
    $nowPlaying = Wait-Until { Find-Named $window 'Ion Trail' } 10 'the shell showed Ion Trail'
    Check 'The shell shows the track an outside Next moved to' ($null -ne $nowPlaying) "'$($nowPlaying.Current.Name)'"

    $target = [TimeSpan]::FromSeconds(60)
    Check 'TryChangePlaybackPositionAsync is accepted' (Invoke-SessionBool ($session.TryChangePlaybackPositionAsync($target.Ticks))) 'true'
    $seeked = Wait-Until { $p = Get-LivePosition $session; if ($p.TotalSeconds -ge 59 -and $p.TotalSeconds -lt 70) { $p } } 10 'the timeline jumped to about 60 s'
    Check 'An outside seek moves the app to the requested position' ($null -ne $seeked) "live position $seeked"

    # ---- close, then the log ---------------------------------------------------------------------------------------
    try { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    if (-not $process.WaitForExit(20000)) { Write-Output '  note  the shell did not exit within 20 s of Close; killing the one this script started'; $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process = $null
    $gone = Wait-Until { if (-not (Find-TunqioSession (Get-Manager))) { 'gone' } } 15 "Tunqio's media session went away after exit"
    Check 'The media session goes when the app exits' ($gone -eq 'gone') $gone

    $log = Read-Log $dataRoot
    $route = $log | Where-Object { $_ -match 'Media controls: SMTC through SystemMediaTransportControlsInterop.GetForWindow' } | Select-Object -Last 1
    $attached = $log | Where-Object { $_ -match 'Media controls attached to the playback session' } | Select-Object -Last 1
    $presses = @($log | Where-Object { $_ -match 'Media control (Pause|Play|Next) pressed' })
    $seekLine = $log | Where-Object { $_ -match 'Media control seek to' } | Select-Object -Last 1
    $closed = $log | Where-Object { $_ -match 'Media controls closed' } | Select-Object -Last 1
    Check 'The log names the SMTC route' ($null -ne $route) "$(if ($route) { 'GetForWindow' } else { 'no line' })"
    Check 'The log records the bridge attaching to the session' ($null -ne $attached) "$(if ($attached) { 'attached' } else { 'no line' })"
    Check 'The log records the Pause, Play and Next presses' ($presses.Count -ge 3) "$($presses.Count) press line(s)"
    Check 'The log records the seek request' ($null -ne $seekLine) "$(if ($seekLine) { $seekLine.Substring($seekLine.IndexOf('Media control seek')) } else { 'no line' })"
    Check 'The log records the media session closing at shutdown' ($null -ne $closed) "$(if ($closed) { 'closed' } else { 'no line' })"
}
catch {
    $script:failures += "script error: $($_.Exception.Message)"
    Write-Output "  FAIL  script error: $($_.Exception.Message)"
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $process.CloseMainWindow() | Out-Null } catch { }
        if (-not $process.WaitForExit(20000)) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    }
    if (-not $Keep) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
    else { Write-Output "  note  scratch kept at $scratch" }
}

if ($script:failures.Count -gt 0) {
    Write-Output "check-smtc: FAIL ($($script:failures.Count) check(s)): $($script:failures -join '; ')"
    exit 1
}
Write-Output 'check-smtc: PASS'
exit 0
