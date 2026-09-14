<#
.SYNOPSIS
  E7-S1 (T-74; AC-474, AC-475, AC-153 command-line half): one Tunqio per data root, proven unpackaged. A second process
  launched against the same --data-root hands its arguments to the running window and exits; a process launched against
  a different data root is its own instance.

  Against a scratch profile, one step after another:
    1. launches the Release build on scratch root A and dismisses the first-run welcome; mutes the output;
    2. launches a second process on root A with a 90 s FLAC's path: it exits within a few seconds with code 0, root A still
       has exactly one Tunqio process and one window, and that window's Now Playing shows the file's title;
    3. a second process with tunqio://queue?path=<the second FLAC>, then one with tunqio://toggle: the transport offers
       Play (paused); then tunqio://next: Now Playing shows the second title;
    4. a second process with 50 file paths: it exits, one instance remains, and the log says the queue holds 50 items;
       then one with tunqio://play?path=<the first FLAC>: Now Playing shows its title again;
    4c. (T-192) a second process started from the all-lowercase spelling of the shell's path, then one from the real path with
       root A spelled in lowercase: each exits with 0 and root A keeps one process and one window; the log shows exactly
       one relaunch from the true path, and eight redirects in all;
    5. a process on scratch root B: it does not exit, and it has a window of its own;
  then closes both windows it launched (Close-TunqioShell, which fails the run on a hang or a crash code) and reads root
  A's log for the redirect, receive and route lines.

  The audio is generated for the run with ffmpeg (artifacts\ffmpeg\bin first, then PATH): two 90 s tagged FLAC tones, and
  one 5 s tone copied to 50 files.

  WHAT IT CHANGES. Nothing outside artifacts\check-single-instance\<stamp>, deleted at the end unless -Keep. The real
  profile is never opened, and nothing is installed or registered: this is the unpackaged app. It refuses to start while
  any Tunqio is running (checking again once a minute for at most -WaitMinutes), and it only ever closes the processes it
  launched, by their ids. Every process is launched after the previous one has finished or settled, never several at once.

  ASCII only, Windows PowerShell 5.1, safe under -File. UIA only; no keystrokes and no pointer.
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
if ($WaitMinutes -gt 10) { $WaitMinutes = 10 }
if ($WaitMinutes -lt 0) { $WaitMinutes = 0 }

# ---- refuse while anybody's Tunqio is open: once a minute, at most -WaitMinutes times (T-174: every wait has an end) ----
for ($attempt = 0; @(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0; $attempt++) {
    if ($attempt -ge $WaitMinutes) {
        Write-Output "check-single-instance: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (attempt $($attempt + 1) of $WaitMinutes)"
    Start-Sleep -Seconds 60
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-single-instance\$stamp"))
$rootA = Join-Path $scratch 'root-a'
$rootB = Join-Path $scratch 'root-b'
$music = Join-Path $scratch 'music'
$fifty = Join-Path $scratch 'fifty'
$artist = 'Instance Artist'
$titleOne = 'Instance One'
$titleTwo = 'Instance Two'
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'

if (-not $Ffmpeg) {
    $fetched = Join-Path $here '..\artifacts\ffmpeg\bin\ffmpeg.exe'
    if (Test-Path $fetched) { $Ffmpeg = [System.IO.Path]::GetFullPath($fetched) }
    elseif (Get-Command ffmpeg -ErrorAction SilentlyContinue) { $Ffmpeg = (Get-Command ffmpeg).Source }
    else { throw 'ffmpeg was not found: run tools/fetch-ffmpeg.ps1, put ffmpeg on PATH, or pass -Ffmpeg.' }
}

# One tagged FLAC tone. Start-Process with a bounded wait, so a stuck encoder cannot hold the run.
function New-Tone([string]$out, [int]$hz, [int]$seconds, [string]$title) {
    $argumentLine = ('-nostdin -hide_banner -loglevel error -y -f lavfi -i "sine=frequency={0}:sample_rate=44100:duration={1}" ' +
        '-c:a flac -ac 2 -metadata "title={2}" -metadata "artist={3}" -metadata "album=Single Instance" "{4}"') -f $hz, $seconds, $title, $artist, $out
    $encoder = Start-Process -FilePath $Ffmpeg -ArgumentList $argumentLine -NoNewWindow -PassThru
    $null = $encoder.Handle
    if (-not $encoder.WaitForExit(60000)) { $encoder.Kill(); throw "ffmpeg did not finish $out within 60 s" }
    if ($encoder.ExitCode -ne 0 -or -not (Test-Path $out)) { throw "ffmpeg could not write $out (exit $($encoder.ExitCode))" }
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
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}
function Get-WindowsOf([int]$processId) {
    @($A::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId))))
}
# Every Tunqio process whose command line names this data root, and every one at all.
function Get-TunqioOn([string]$root) {
    @(Get-CimInstance Win32_Process -Filter "Name='Tunqio.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}
function Start-Shell([string]$argumentLine) {
    $p = Start-Process $Exe -ArgumentList $argumentLine -PassThru
    $null = $p.Handle   # Windows PowerShell 5.1: ExitCode reads back empty unless the handle was opened while it was alive.
    return $p
}
function Quote([string]$text) { '"' + $text + '"' }

# A second process on root A: it must hand over its activation and exit, cleanly, within $seconds. $path and $root default
# to the shell's own spelling and root A's; T-192 passes other spellings of both.
function Invoke-Second([string]$label, [string]$arguments, [int]$seconds = 15, [string]$path = $Exe, [string]$root = $rootA) {
    $second = Start-Process $path -ArgumentList ("--data-root {0} {1}" -f (Quote $root), $arguments) -PassThru
    $null = $second.Handle
    $script:launched += $second
    $exited = $second.WaitForExit($seconds * 1000)
    Check "$label - the second process exits" $exited "pid $($second.Id), $(if ($exited) { 'exited' } else { "still running after $seconds s" })"
    if ($exited) {
        Check "$label - it exits with code 0" ($second.ExitCode -eq 0) ("exit code 0x{0:X8}" -f $second.ExitCode)
    }
    else {
        $problem = Close-TunqioShell $second $null 10
        if ($problem) { $script:failures += $problem }
    }
    # A relaunched process (T-192) may still be handing over when its parent exits: give root A up to 10 s to settle to one.
    # @() at the call as well: a function's one-item array unrolls to a bare CimInstance, whose Count reads back empty in 5.1.
    $settleBy = (Get-Date).AddSeconds(10)
    while (@(Get-TunqioOn $rootA).Count -gt 1 -and (Get-Date) -lt $settleBy) { Start-Sleep -Milliseconds 250 }
    $onA = @(Get-TunqioOn $rootA)
    Check "$label - root A still has exactly one Tunqio process" ($onA.Count -eq 1 -and $onA[0].ProcessId -eq $script:first.Id) "$($onA.Count) process(es): $(($onA | ForEach-Object { $_.ProcessId }) -join ', ')"
    Check "$label - and one window" ((Get-WindowsOf $script:first.Id).Count -eq 1 -and (Get-WindowsOf $second.Id).Count -eq 0) "first $((Get-WindowsOf $script:first.Id).Count), second $((Get-WindowsOf $second.Id).Count)"
}

$script:launched = @()
$script:first = $null
$other = $null
try {
    # ---- the audio and the scratch roots --------------------------------------------------------------------------------
    New-Item -ItemType Directory -Force -Path $rootA, $rootB, $music, $fifty | Out-Null
    foreach ($root in @($rootA, $rootB)) {
        $full = [System.IO.Path]::GetFullPath($root)
        if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
            throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
        }
    }
    $trackOne = Join-Path $music '01 - Instance One.flac'
    $trackTwo = Join-Path $music '02 - Instance Two.flac'
    New-Tone $trackOne 330 90 $titleOne
    New-Tone $trackTwo 440 90 $titleTwo
    $short = Join-Path $scratch 'short.flac'
    New-Tone $short 550 5 'Fifty'
    $fiftyPaths = @()
    for ($i = 1; $i -le 50; $i++) {
        $path = Join-Path $fifty ('{0:00} - Fifty.flac' -f $i)
        Copy-Item $short $path
        $fiftyPaths += $path
    }
    Write-Output "  note  generated two 90 s tracks and 50 copies of a 5 s track with $Ffmpeg"
    Write-Output "shell: $Exe"
    Write-Output "scratch root A: $rootA"
    Write-Output "scratch root B: $rootB"

    # ---- 1. the running instance on root A ----------------------------------------------------------------------------
    $script:first = Start-Shell ("--data-root {0}" -f (Quote $rootA))
    $script:launched += $script:first
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $script:first.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window on root A appeared'
    $welcome = $null
    try { $welcome = Wait-Until { Find-Named $window 'Welcome to Tunqio' } 15 'the welcome dialog appeared' } catch { }
    if ($welcome) {
        Invoke-Element (Wait-Until { Find-Named $welcome 'Skip all' } 5 'the welcome offered Skip all')
        Wait-Until { -not (Find-Named $window 'Welcome to Tunqio') } 10 'the welcome closed' | Out-Null
    }
    $mute = Wait-Until { Find-ById $window 'MuteButton' } 20 'the Mute button appeared'
    $toggle = $mute.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    Write-Output "  note  root A's instance is pid $($script:first.Id), output muted"

    # ---- 2. a file, from a second process -----------------------------------------------------------------------------
    Invoke-Second 'a file path' (Quote $trackOne)
    $shown = $null
    try { $shown = Wait-Until { Find-Named $window $titleOne } 30 "Now Playing showed '$titleOne'" } catch { }
    Check 'The running window plays the file the second process was given' ($null -ne $shown) "$(if ($shown) { "'$titleOne' shown" } else { 'not shown within 30 s' })"
    $playing = $null
    try { $playing = Wait-Until { Find-Named $window 'Pause' } 10 'the transport offered Pause' } catch { }
    Check 'It is playing' ($null -ne $playing) "$(if ($playing) { 'the transport offers Pause' } else { 'no Pause button' })"

    # ---- 3. tunqio://queue, toggle, next ------------------------------------------------------------------------------
    Invoke-Second 'tunqio://queue' (Quote ('tunqio://queue?path=' + [Uri]::EscapeDataString($trackTwo)))
    Start-Sleep -Seconds 2
    Invoke-Second 'tunqio://toggle' 'tunqio://toggle'
    $paused = $null
    try { $paused = Wait-Until { Find-Named $window 'Play' } 10 'the transport offered Play' } catch { }
    Check 'tunqio://toggle pauses the running window' ($null -ne $paused) "$(if ($paused) { 'the transport offers Play' } else { 'still offers Pause' })"
    Invoke-Second 'tunqio://next' 'tunqio://next'
    $moved = $null
    try { $moved = Wait-Until { Find-Named $window $titleTwo } 15 "Now Playing showed '$titleTwo'" } catch { }
    Check 'tunqio://next moves to the queued track' ($null -ne $moved) "$(if ($moved) { "'$titleTwo' shown" } else { 'not shown within 15 s' })"

    # ---- 4. fifty files at once ---------------------------------------------------------------------------------------
    Invoke-Second 'fifty file paths' (($fiftyPaths | ForEach-Object { Quote $_ }) -join ' ') 20
    $fiftyShown = $null
    try { $fiftyShown = Wait-Until { Find-Named $window 'Fifty' } 30 "Now Playing showed 'Fifty'" } catch { }
    Check 'The fifty files replace the queue in the running window' ($null -ne $fiftyShown) "$(if ($fiftyShown) { "'Fifty' shown" } else { 'not shown within 30 s' })"
    Start-Sleep -Seconds 3

    # ---- 4b. tunqio://play?path= from the command line replaces the queue (AC-153) --------------------------------------
    Invoke-Second 'tunqio://play' (Quote ('tunqio://play?path=' + [Uri]::EscapeDataString($trackOne)))
    $replayed = $null
    try { $replayed = Wait-Until { Find-Named $window $titleOne } 15 "Now Playing showed '$titleOne' again" } catch { }
    Check 'tunqio://play?path= plays that file in the running window' ($null -ne $replayed) "$(if ($replayed) { "'$titleOne' shown" } else { 'not shown within 15 s' })"

    # ---- 4c. T-192: another spelling of Tunqio.exe's path, and of the data root, still find root A's instance -----------
    # COM starts a toast press from the SDK's all-lowercase path; AppInstance scopes keys by the exact module path.
    Invoke-Second 'a lowercased Tunqio.exe path' 'tunqio://show' -path $Exe.ToLowerInvariant()
    Invoke-Second 'a lowercased data root' 'tunqio://show' -root $rootA.ToLowerInvariant()

    # ---- 5. a different data root is a different instance -------------------------------------------------------------
    $other = Start-Shell ("--data-root {0}" -f (Quote $rootB))
    $script:launched += $other
    $stayed = -not $other.WaitForExit(8000)
    Check 'A launch on root B does not redirect into root A' $stayed "pid $($other.Id), $(if ($stayed) { 'still running after 8 s' } else { ('exited with 0x{0:X8}' -f $other.ExitCode) })"
    $otherWindow = $null
    if ($stayed) {
        $byOther = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $other.Id)
        try { $otherWindow = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byOther) } 30 'root B window appeared' } catch { }
    }
    Check 'Root B has a window of its own' ($null -ne $otherWindow) "$(if ($otherWindow) { "window for pid $($other.Id)" } else { 'no window' })"
    $besideA = @(Get-TunqioOn $rootA)
    $besideB = @(Get-TunqioOn $rootB)
    Check 'Root A still has its one process beside it' ($besideA.Count -eq 1 -and $besideB.Count -eq 1) "$($besideA.Count) on root A, $($besideB.Count) on root B"

    # ---- close both, then read root A's log ---------------------------------------------------------------------------
    $problem = Close-TunqioShell $other $otherWindow 20
    if ($problem) { $script:failures += $problem }
    $problem = Close-TunqioShell $script:first $window 20
    if ($problem) { $script:failures += $problem }

    $log = @()
    foreach ($file in @(Get-ChildItem (Join-Path $rootA 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) { $log += @(Get-Content $file.FullName) }
    $redirected = @($log | Where-Object { $_ -match 'Single instance: .* activation redirected to the running instance' })
    $received = @($log | Where-Object { $_ -match 'Single instance: received a redirected' })
    $refused = @($log | Where-Object { $_ -match 'Activation input refused|Activation command .* refused' })
    $queue50 = $log | Where-Object { $_ -match 'queue replaced from 50 path\(s\); queue now 50 item\(s\)' } | Select-Object -Last 1
    $foreground = @($log | Where-Object { $_ -match 'main window activated \(foreground granted' })
    $relaunchedLines = @($log | Where-Object { $_ -match 'Single instance: started as .*; relaunched as pid' })
    Check 'Root A logged eight redirected activations from the second processes' ($redirected.Count -eq 8) "$($redirected.Count) redirect line(s)"
    Check 'The running instance received all eight' ($received.Count -eq 8) "$($received.Count) receive line(s)"
    Check 'Only the lowercased Tunqio.exe launch was relaunched from the true path (T-192)' ($relaunchedLines.Count -eq 1) "$($relaunchedLines.Count) relaunch line(s)"
    Check 'Nothing the harness sent was refused' ($refused.Count -eq 0) "$(if ($refused.Count) { $refused[0] } else { 'no refusal lines' })"
    Check 'Fifty paths from one launch became a 50-item queue in one instance' ($null -ne $queue50) "$(if ($queue50) { 'queue now 50 item(s)' } else { 'no such line' })"
    Check 'The play and file activations asked for the foreground' ($foreground.Count -ge 2) "$($foreground.Count) line(s); last: $(if ($foreground.Count) { $foreground[-1].Substring($foreground[-1].IndexOf('main window')) } else { 'none' })"
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
        }
    }
    if (-not $Keep) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
    else { Write-Output "  note  scratch kept at $scratch" }
}

if ($script:failures.Count -gt 0) {
    Write-Output "check-single-instance: FAIL ($($script:failures.Count) check(s)): $($script:failures -join '; ')"
    exit 1
}
Write-Output 'check-single-instance: PASS'
exit 0
