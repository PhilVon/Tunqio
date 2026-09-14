<#
.SYNOPSIS
  E6-S6 (T-72): flow 1, first run to first sound, against a SCRATCH data root. UIA patterns only: no keystrokes, no
  pointer, no file picker.

  Launch 1 runs the shell with --data-root on an empty folder (no settings.json) and a tiny music folder copied from
  tests/fixtures/library. It checks the welcome appears on step 1 of 3 with the Music folder pre-filled, types the
  scratch music folder into the folder box through ValuePattern, invokes Add this folder, and checks the folder is
  stored and its scan runs while the dialog is still open. It walks Next and Back, reaches the device step (the Output
  page's list), picks Dark on the theme step and reads ui.theme back, invokes Done and checks the dialog closed and
  ui.welcomeShown was written. It waits for the Albums grid to hold the copied albums, invokes an album tile, closes
  the app, and reads the log after exit (the file sink buffers) for the first sound.

  Launch 2 reuses the same data root and checks the welcome does not come back. Launch 3 is a second scratch root with
  a settings.json carrying app.launchCount and no ui.welcomeShown, the shape of a profile from before this build, and
  checks the welcome is not shown to it either.

  WHAT IT CHANGES. Nothing outside the scratch folder under artifacts\check-first-run, which is deleted at the end
  unless -Keep. The real profile (%LocalAppData%\Tunqio) is never opened: every launch passes --data-root, and the
  script refuses a data root inside it. Music plays for a few seconds on the default output.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio window somebody else opened to go away, retrying once a minute, before refusing.
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

# ---- refuse while somebody's Tunqio is open: once a minute, for at most -WaitMinutes (T-174: every wait has an end) ----
$refuseDeadline = (Get-Date).AddMinutes($WaitMinutes)
while (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    if ((Get-Date) -ge $refuseDeadline) {
        Write-Output "check-first-run: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s); this script launches its own and will not run beside one somebody is using)"
        exit 2
    }
    Write-Output "  wait  Tunqio is running; checking again in 60 s (until $($refuseDeadline.ToString('HH:mm')))"
    Start-Sleep -Seconds 60
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-first-run\$stamp"))
$dataRoot = Join-Path $scratch 'data'
$legacyRoot = Join-Path $scratch 'legacy-data'
$music = Join-Path $scratch 'music'
$fixtures = Join-Path $here '..\tests\fixtures\library'
$albums = @(
    @{ Folder = 'Night Signal - Aurora Lines (2019)'; Tile = 'Album Aurora Lines by Night Signal, 2019' },
    @{ Folder = 'Field Notes - Tape One (1998)'; Tile = 'Album Tape One by Field Notes, 1998' }
)
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'

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
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Get-Value($element) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Set-Value($element, [string]$text) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}
function Read-Settings([string]$root) {
    $file = Join-Path $root 'settings.json'
    if (-not (Test-Path $file)) { return $null }
    return Get-Content $file -Raw | ConvertFrom-Json
}
function Read-Log([string]$root) {
    $lines = @()
    foreach ($log in @(Get-ChildItem (Join-Path $root 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) {
        $lines += @(Get-Content $log.FullName)
    }
    return $lines
}
# The welcome: the ContentDialog carries its title as its automation name.
function Find-Welcome($window) { Find-Named $window 'Welcome to Tunqio' }

function Start-Shell([string]$root) {
    $full = [System.IO.Path]::GetFullPath($root)
    if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    $p = Start-Process $Exe -ArgumentList @('--data-root', "`"$full`"") -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)
    $w = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    return @{ Process = $p; Window = $w }
}
function Stop-Shell($shell) {
    if (-not $shell) { return }
    try { $shell.Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    if (-not $shell.Process.WaitForExit(20000)) { Write-Output '  note  the shell did not exit within 20 s of Close; killing it'; $shell.Process.Kill(); $shell.Process.WaitForExit(5000) | Out-Null }
}
function Find-Tile($window, [string]$name) {
    $tile = Find-Named $window $name
    if ($tile -and $tile.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') { return $tile }
    return $null
}

$shell = $null
try {
    New-Item -ItemType Directory -Force -Path $dataRoot, $music, $legacyRoot | Out-Null
    foreach ($album in $albums) { Copy-Item -Recurse (Join-Path $fixtures $album.Folder) $music }
    Write-Output "shell: $Exe"
    Write-Output "scratch data root: $dataRoot"
    Write-Output "scratch music: $music ($(@(Get-ChildItem $music -Recurse -File).Count) files)"
    Check 'The scratch data root starts with no settings.json' (-not (Test-Path (Join-Path $dataRoot 'settings.json'))) $dataRoot

    # ==== launch 1: fresh profile ====================================================================================
    $shell = Start-Shell $dataRoot
    $window = $shell.Window
    $dialog = Wait-Until { Find-Welcome $window } 30 'the welcome dialog appeared'
    Check 'A fresh profile launches into the welcome' ($null -ne $dialog) $dialog.Current.ControlType.ProgrammaticName
    $caption = Wait-Until { Find-ById $dialog 'WelcomeStepCaption' } 10 'the step caption appeared'
    Check 'It opens on step 1 of 3' ($caption.Current.Name -eq 'Step 1 of 3') "'$($caption.Current.Name)'"
    Check 'Step 1 offers adding folders, and each dialog button is there' ((Find-Named $dialog 'Add this folder') -and (Find-Named $dialog 'Add another folder') -and (Find-Named $dialog 'Skip this step') -and (Find-Named $dialog 'Skip all') -and (Find-Named $dialog 'Back')) 'Add this folder, Add another folder, Skip this step, Back, Skip all'
    $box = Find-Named $dialog 'Folder to add'
    $suggested = Get-Value $box
    $myMusic = [Environment]::GetFolderPath('MyMusic')
    Check 'The folder box is pre-filled with the Music folder' ($suggested -eq $myMusic) "'$suggested' (MyMusic is '$myMusic')"

    Set-Value $box $music
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-Named $dialog 'Add this folder')
    $notice = Wait-Until { $n = Find-ById $dialog 'WelcomeFolderNotice'; if ($n -and $n.Current.Name -like 'Added *') { $n } } 15 'the welcome said the folder was added'
    Check 'Add this folder stores the folder and says the scan has started' ($notice.Current.Name -like "*scanning it now*") "'$($notice.Current.Name)'"
    $next = Wait-Until { Find-Named $dialog 'Next' } 5 'the forward button became Next'
    Check 'Once a folder is added the forward button reads Next' ($null -ne $next) 'Next'

    $tileWhileOpen = Wait-Until { Find-Tile $window $albums[0].Tile } 60 'an album tile appeared behind the dialog'
    Check 'The Albums grid fills while the welcome is still open' ($null -ne $tileWhileOpen -and $null -ne (Find-Welcome $window)) "'$($albums[0].Tile)' with the dialog open"

    Invoke-Element $next
    Start-Sleep -Milliseconds 700
    Check 'Next goes to step 2 of 3, the output device' ((Find-ById $dialog 'WelcomeStepCaption').Current.Name -eq 'Step 2 of 3' -and $null -ne (Find-Named $dialog 'Output device')) "caption '$((Find-ById $dialog 'WelcomeStepCaption').Current.Name)'"
    $device = Find-Named $dialog 'Output device'
    $deviceText = ''
    try { $deviceText = Get-Value $device } catch { $deviceText = '(no value pattern)' }
    Write-Output "  note  output device shown: '$deviceText'"
    Invoke-Element (Find-Named $dialog 'Back')
    Start-Sleep -Milliseconds 700
    Check 'Back returns to step 1' ((Find-ById $dialog 'WelcomeStepCaption').Current.Name -eq 'Step 1 of 3') "'$((Find-ById $dialog 'WelcomeStepCaption').Current.Name)'"
    Invoke-Element (Find-Named $dialog 'Next')
    Start-Sleep -Milliseconds 700
    Invoke-Element (Find-Named $dialog 'Next')
    Start-Sleep -Milliseconds 700
    Check 'Next again goes to step 3 of 3, the theme' ((Find-ById $dialog 'WelcomeStepCaption').Current.Name -eq 'Step 3 of 3') "'$((Find-ById $dialog 'WelcomeStepCaption').Current.Name)'"
    $theme = Wait-Until { Find-Named $dialog 'Theme' } 5 'the theme choices appeared'
    $dark = Wait-Until { Find-Named $theme 'Dark' } 5 'the Dark choice appeared'
    Select-Element $dark
    $storedTheme = Wait-Until { $s = Read-Settings $dataRoot; if ($s -and $s.'ui.theme' -eq 'dark') { $s.'ui.theme' } } 10 'ui.theme became dark'
    Check 'Choosing Dark on the theme step writes ui.theme' ($storedTheme -eq 'dark') "ui.theme '$storedTheme'"

    Invoke-Element (Find-Named $dialog 'Done')
    $gone = Wait-Until { -not (Find-Welcome $window) } 10 'the welcome closed'
    Check 'Done closes the welcome' ([bool]$gone) 'dialog gone'
    $shown = Wait-Until { $s = Read-Settings $dataRoot; if ($s -and $s.'ui.welcomeShown' -eq $true) { 'true' } } 10 'ui.welcomeShown was written'
    Check 'A setting records that the welcome ran' ($shown -eq 'true') "ui.welcomeShown $shown"

    $tiles = @(foreach ($album in $albums) { Wait-Until { Find-Tile $window $album.Tile } 60 "the tile '$($album.Tile)' appeared" })
    Check 'The Albums grid holds both copied albums' ($tiles.Count -eq $albums.Count) (($albums | ForEach-Object { $_.Tile }) -join '; ')

    try { $tiles[0].GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
    Invoke-Element $tiles[0]
    $pause = Wait-Until { $b = Find-Named $window 'Pause'; if ($b) { $b } } 15 'the transport offered Pause'
    Check 'Invoking the album tile starts playback (the transport offers Pause)' ($null -ne $pause) 'Pause'
    Start-Sleep -Seconds 3
    Stop-Shell $shell
    $shell = $null

    $log = Read-Log $dataRoot
    $decided = @($log | Where-Object { $_ -match 'First-run welcome: shown' })
    $added = $log | Where-Object { $_ -match 'First-run welcome added' } | Select-Object -Last 1
    $scanned = $log | Where-Object { $_ -match 'First-run scan of .* ended' } | Select-Object -Last 1
    $closed = $log | Where-Object { $_ -match 'First-run welcome closed \(done\)' } | Select-Object -Last 1
    $sound = $log | Where-Object { $_ -match 'First run: first sound' } | Select-Object -Last 1
    $applied = $log | Where-Object { $_ -match 'applied Dark' } | Select-Object -Last 1
    Check 'The log records the welcome being shown once' ($decided.Count -eq 1) "$($decided.Count) line(s)"
    Check 'The log records the folder add and its scan ending' ($null -ne $added -and $null -ne $scanned) "$(if ($scanned) { $scanned.Substring($scanned.IndexOf('First-run scan')) } else { 'no scan line' })"
    Check 'The log records the window repainting Dark from the welcome' ($null -ne $applied) "$(if ($applied) { 'applied Dark' } else { 'no line' })"
    Check 'The log records Done' ($null -ne $closed) "$(if ($closed) { $closed.Substring($closed.IndexOf('First-run welcome closed')) } else { 'no line' })"
    Check 'First sound: the log records playback reaching Playing' ($null -ne $sound) "$(if ($sound) { $sound.Substring($sound.IndexOf('First run:')) } else { 'no line' })"

    # ==== launch 2: the same profile ==================================================================================
    $shell = Start-Shell $dataRoot
    $window = $shell.Window
    $tile = Wait-Until { Find-Tile $window $albums[0].Tile } 30 'the library came back'
    Start-Sleep -Seconds 6
    Check 'A relaunch on the same data root does not show the welcome' ($null -eq (Find-Welcome $window)) "library present ('$($tile.Current.Name)'), no dialog after 6 s"
    Stop-Shell $shell
    $shell = $null
    $decided = @(Read-Log $dataRoot | Where-Object { $_ -match 'First-run welcome: shown' })
    Check 'The log still has exactly one welcome shown' ($decided.Count -eq 1) "$($decided.Count) line(s)"

    # ==== launch 3: a profile from before this build =================================================================
    # No ui.welcomeShown, but launched before: the upgrade Phil's own profile makes.
    Set-Content -Path (Join-Path $legacyRoot 'settings.json') -Encoding ASCII -Value '{ "app.launchCount": 57, "ui.theme": "system" }'
    $shell = Start-Shell $legacyRoot
    $window = $shell.Window
    Start-Sleep -Seconds 10
    Check 'An existing profile upgraded to this build is not shown the welcome' ($null -eq (Find-Welcome $window)) 'no dialog after 10 s'
    Stop-Shell $shell
    $shell = $null
    $legacy = Read-Settings $legacyRoot
    $predates = Read-Log $legacyRoot | Where-Object { $_ -match 'First-run welcome: not shown, this profile predates it' } | Select-Object -Last 1
    Check 'It records the welcome as settled without showing it' ($legacy.'ui.welcomeShown' -eq $false -and $null -ne $predates) "ui.welcomeShown '$($legacy.'ui.welcomeShown')', log line $(if ($predates) { 'present' } else { 'missing' })"
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    Stop-Shell $shell
    if (-not $Keep -and (Test-Path $scratch)) {
        try { Remove-Item -Recurse -Force $scratch } catch { Write-Output "  note  scratch folder not removed: $($_.Exception.Message)" }
    }
    elseif ($Keep) { Write-Output "  note  kept $scratch" }
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-first-run: PASS'
    exit 0
}
Write-Output "check-first-run: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
