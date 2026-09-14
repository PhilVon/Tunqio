<#
.SYNOPSIS
  T-195: closing the main window while a ContentDialog is open shuts Tunqio down cleanly (exit code 0, the shutdown
  steps in the log, no crash, the queue capture not reported as failed), and with close-to-tray on it hides the window
  as usual. Every case launches the shell on a SCRATCH data root and drives it through UIA patterns only: no
  keystrokes, no pointer, no file picker.

  Cases, one launch each:
    1. welcome, closed early: the close is sent as soon as the log says library.db was created, which is before the
       window exists (check-jump-list's first setup launch, T-78). Close-TunqioShell has to wait for the window.
    2. welcome, open: the first-run welcome (T-72) on a fresh profile.
    3. New playlist (T-67): Library > Playlists > New playlist.
    4. Rename playlist: a playlist made through New playlist, then Rename.
    5. Delete playlist confirmation: a playlist made through New playlist, then Delete.
    6. Add to playlist: a fixture folder added through the welcome, an album played muted, Go to album, More > Add to
       playlist.
    7. Remove folder confirmation: Settings > Library > Remove folder, on case 6's profile.
    8. close-to-tray on (T-76) with New playlist open: the close hides the window and the process keeps running;
       tunqio://show brings it back, the dialog is cancelled, close-to-tray is turned off in Settings, and the close then
       exits with code 0.

  NOT OPENED HERE, because nothing but a keystroke opens them: the tag editor (T-48; F2 or the row menu, which needs
  Shift+F10 or a right click, as check-tag-editor.ps1 does) and the shortcut conflict (a chord typed on the Shortcuts
  page). They are ContentDialogs shown by ShowAsync on the window's XamlRoot like every case above.

  WHAT IT CHANGES. Nothing outside the scratch folder under artifacts\check-dialog-close, deleted at the end unless
  -Keep. The real profile (%LocalAppData%\Tunqio) is never opened: every launch passes --data-root, and the script
  refuses a data root inside it. A fixture album plays for a moment with the output muted.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output beside this script.
.PARAMETER WaitMinutes
  How long to wait for a Tunqio somebody else started to go away, checking every 30 s, before refusing.
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
. (Join-Path $here 'uia-geometry.ps1') # Close-TunqioShell (T-188, T-195)

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:failures = @()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = [System.IO.Path]::GetFullPath((Join-Path $here "..\artifacts\check-dialog-close\$stamp"))
$music = Join-Path $scratch 'music'
$fixtures = Join-Path $here '..\tests\fixtures\library'
$fixtureAlbum = 'Night Signal - Aurora Lines (2019)'
$realRoot = Join-Path $env:LOCALAPPDATA 'Tunqio'

# ---- refuse while somebody's Tunqio is open, for at most -WaitMinutes (T-174: every wait has an end) ----------------
function Wait-NoTunqio {
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Seconds 30
    }
    return $true
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
function Find-ListItem($scope, [string]$text) {
    $scope.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)),
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))))
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Set-Value($element, [string]$text) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}
function Quote([string]$text) { '"' + $text + '"' }
function Get-WindowOf([int]$processId) {
    $A::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId)))
}
# A ContentDialog is a Window inside the shell's tree; its title is its automation name.
function Find-Dialog($window, [string]$titleLike) {
    $window.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) |
        Where-Object { $_.Current.Name -like $titleLike } | Select-Object -First 1
}
function Read-Log([string]$root) {
    $lines = @()
    foreach ($log in @(Get-ChildItem (Join-Path $root 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) {
        $lines += @(Get-Content $log.FullName)
    }
    return $lines
}
function New-Root([string]$name, [string]$settingsJson) {
    $root = Join-Path $scratch $name
    $full = [System.IO.Path]::GetFullPath($root)
    if ($full.StartsWith([System.IO.Path]::GetFullPath($realRoot), [System.StringComparison]::OrdinalIgnoreCase) -or $full -like '*\Packages\*\LocalCache\*') {
        throw "refusing data root ${full}: it is (or is a redirected copy of) the real profile"
    }
    New-Item -ItemType Directory -Force -Path $full | Out-Null
    # A settings.json with ui.welcomeShown already settled keeps the welcome away (FirstRunWelcomeViewModel.ShouldShow).
    if ($settingsJson) { [System.IO.File]::WriteAllText((Join-Path $full 'settings.json'), $settingsJson) }
    return $full
}
function Start-Shell([string]$root, [string]$extra = '') {
    $p = Start-Process $Exe -ArgumentList ("--data-root {0} {1}" -f (Quote $root), $extra) -PassThru
    $null = $p.Handle   # Windows PowerShell 5.1: ExitCode reads back empty unless the handle was opened while it was alive.
    $script:launched += $p
    return $p
}
function Get-MainWindow($process) { Wait-Until { Get-WindowOf $process.Id } 30 'the shell window appeared' }
function Set-Muted($window) {
    $toggle = (Wait-Until { Find-ById $window 'MuteButton' } 20 'the Mute button appeared').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ("$($toggle.Current.ToggleState)" -ne 'On') { $toggle.Toggle() }
}
function Open-Playlists($window) {
    Select-Element (Wait-Until { Find-ListItem $window 'Playlists' } 15 'the sidebar showed Playlists')
    Wait-Until { Find-Named $window 'New playlist' } 10 'Library > Playlists opened' | Out-Null
    Start-Sleep -Milliseconds 800
}
function New-PlaylistThroughDialog($window, [string]$name) {
    Open-Playlists $window
    Invoke-Element (Find-Named $window 'New playlist')
    $dialog = Wait-Until { Find-Dialog $window 'New playlist' } 10 'the New playlist dialog opened'
    Set-Value (Wait-Until { Find-Named $dialog 'Playlist name' } 5 'the name box appeared') $name
    Invoke-Element (Wait-Until { Find-Named $dialog 'Create' } 5 'the dialog offered Create')
    Wait-Until { Find-ById $window 'PlaylistSummary' } 10 'the new playlist opened' | Out-Null
    Start-Sleep -Milliseconds 800
}

# The close itself, and what a clean one leaves in the log. $closeAfter is a log line the case waits for before closing
# (case 1), else the dialog is found first.
function Assert-CleanClose([string]$label, $process, $window, [string]$root) {
    $problem = Close-TunqioShell $process $window 20
    Check "$label - Tunqio exits with code 0" ($null -eq $problem) "$(if ($problem) { $problem } else { 'exit code 0' })"
    if ($problem) { $script:failures += $problem }
    Start-Sleep -Milliseconds 300
    $log = Read-Log $root
    $requested = @($log | Where-Object { $_ -match 'Close: main window close requested' })
    $closed = @($log | Where-Object { $_ -match 'Shutdown: main window closed' })
    $ending = @($log | Where-Object { $_ -match 'Session .* ending' })
    $disposed = @($log | Where-Object { $_ -match 'Shutdown: host disposed' })
    $fatal = @($log | Where-Object { $_ -match '\[FTL\]' })
    $save = @($log | Where-Object { $_ -match 'Could not save the queue|Audio teardown did not finish|Audio teardown failed' })
    Check "$label - the log has the close request and every shutdown step to the host" ($requested.Count -ge 1 -and $closed.Count -eq 1 -and $ending.Count -eq 1 -and $disposed.Count -eq 1) "close requested $($requested.Count), window closed $($closed.Count), session ending $($ending.Count), host disposed $($disposed.Count)"
    Check "$label - nothing fatal, and no queue capture or audio teardown failure" ($fatal.Count -eq 0 -and $save.Count -eq 0) "$(if ($fatal.Count) { $fatal[0] } elseif ($save.Count) { $save[0] } else { 'none logged' })"
}

$script:launched = @()
$refused = $false
try {
    if (-not (Wait-NoTunqio)) {
        $refused = $true
        throw 'refused'
    }
    New-Item -ItemType Directory -Force -Path $music | Out-Null
    Copy-Item -Recurse (Join-Path $fixtures $fixtureAlbum) $music
    Write-Output "shell: $Exe"
    Write-Output "scratch: $scratch"
    $settled = '{ "ui.welcomeShown": false }'

    # ---- 1. the welcome, closed before the window exists ------------------------------------------------------------
    Write-Output 'case 1: first-run welcome, close sent before the window exists'
    $root = New-Root 'welcome-early' $null
    $p = Start-Shell $root
    Wait-Until { Read-Log $root | Where-Object { $_ -match 'library\.db created at schema' } } 30 'the log said library.db was created' | Out-Null
    $early = Get-WindowOf $p.Id
    Write-Output "  note  closing with a window element $(if ($early) { 'present' } else { 'absent' }), MainWindowHandle $($p.MainWindowHandle)"
    Assert-CleanClose 'welcome, early close' $p $early $root
    $welcomeLines = @(Read-Log $root | Where-Object { $_ -match 'First-run welcome: shown' })
    Write-Output "  note  the welcome was shown $($welcomeLines.Count) time(s) before the close reached the window"

    # ---- 2. the welcome, open ---------------------------------------------------------------------------------------
    Write-Output 'case 2: first-run welcome open'
    $root = New-Root 'welcome-open' $null
    $p = Start-Shell $root
    $window = Get-MainWindow $p
    $dialog = Wait-Until { Find-Dialog $window 'Welcome to Tunqio' } 30 'the welcome opened'
    Start-Sleep -Seconds 1
    Check 'welcome - the dialog is open at the close' ($null -ne (Find-Dialog $window 'Welcome to Tunqio')) "'$($dialog.Current.Name)'"
    Assert-CleanClose 'welcome' $p $window $root

    # ---- 3. New playlist ------------------------------------------------------------------------------------------------
    Write-Output 'case 3: New playlist'
    $root = New-Root 'new-playlist' $settled
    $p = Start-Shell $root
    $window = Get-MainWindow $p
    Open-Playlists $window
    Invoke-Element (Find-Named $window 'New playlist')
    $dialog = Wait-Until { Find-Dialog $window 'New playlist' } 10 'the New playlist dialog opened'
    Check 'New playlist - the dialog is open at the close' ($null -ne $dialog) "'$($dialog.Current.Name)'"
    Assert-CleanClose 'New playlist' $p $window $root

    # ---- 4. Rename playlist ---------------------------------------------------------------------------------------------
    Write-Output 'case 4: Rename playlist'
    $root = New-Root 'rename-playlist' $settled
    $p = Start-Shell $root
    $window = Get-MainWindow $p
    New-PlaylistThroughDialog $window 'Dialog close check'
    Invoke-Element (Wait-Until { Find-Named $window 'Rename playlist' } 10 'the playlist page offered Rename')
    $dialog = Wait-Until { Find-Dialog $window 'Rename playlist' } 10 'the Rename playlist dialog opened'
    Check 'Rename playlist - the dialog is open at the close' ($null -ne $dialog) "'$($dialog.Current.Name)'"
    Assert-CleanClose 'Rename playlist' $p $window $root

    # ---- 5. Delete playlist confirmation --------------------------------------------------------------------------------
    Write-Output 'case 5: Delete playlist confirmation'
    $root = New-Root 'delete-playlist' $settled
    $p = Start-Shell $root
    $window = Get-MainWindow $p
    New-PlaylistThroughDialog $window 'Dialog close check'
    Invoke-Element (Wait-Until { Find-Named $window 'Delete playlist' } 10 'the playlist page offered Delete')
    $dialog = Wait-Until { Find-Dialog $window 'Delete *' } 10 'the delete confirmation opened'
    Check 'Delete confirmation - the dialog is open at the close' ($null -ne $dialog) "'$($dialog.Current.Name)'"
    Assert-CleanClose 'Delete confirmation' $p $window $root

    # ---- 6. Add to playlist ---------------------------------------------------------------------------------------------
    Write-Output 'case 6: Add to playlist'
    $libraryRoot = New-Root 'library' $null
    $p = Start-Shell $libraryRoot
    $window = Get-MainWindow $p
    $welcome = Wait-Until { Find-Dialog $window 'Welcome to Tunqio' } 30 'the welcome opened'
    Set-Value (Find-Named $welcome 'Folder to add') $music
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-Named $welcome 'Add this folder')
    Wait-Until { $n = Find-ById $welcome 'WelcomeFolderNotice'; if ($n -and $n.Current.Name -like 'Added *') { $n } } 15 'the welcome said the folder was added' | Out-Null
    Invoke-Element (Find-Named $welcome 'Skip all')
    Wait-Until { -not (Find-Dialog $window 'Welcome to Tunqio') } 10 'the welcome closed' | Out-Null
    Set-Muted $window
    $tile = Wait-Until { $window.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like 'Album * by *' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' } | Select-Object -First 1 } 60 'an album tile appeared'
    Invoke-Element $tile
    Invoke-Element (Wait-Until { $l = Find-Named $window 'Go to album'; if ($l -and $l.Current.IsEnabled) { $l } } 15 'Now Playing offered Go to album')
    $more = Wait-Until { Find-Named $window 'More' } 10 'album detail opened'
    $more.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Invoke-Element (Wait-Until { Find-Named $A::RootElement 'Add to playlist' } 5 'the More menu offered Add to playlist')
    $dialog = Wait-Until { Find-Dialog $window 'Add * to a playlist' } 10 'the Add to playlist dialog opened'
    Check 'Add to playlist - the dialog is open at the close' ($null -ne $dialog) "'$($dialog.Current.Name)'"
    Assert-CleanClose 'Add to playlist' $p $window $libraryRoot

    # ---- 7. Remove folder confirmation ----------------------------------------------------------------------------------
    Write-Output 'case 7: Remove folder confirmation'
    $lines = @(Read-Log $libraryRoot).Count
    $p = Start-Shell $libraryRoot
    $window = Get-MainWindow $p
    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 15 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    Select-Element (Wait-Until { Find-Named $overlay 'Library settings' } 10 'the overlay listed Library')
    Invoke-Element (Wait-Until { Find-Named $overlay 'Remove folder' } 15 'Settings > Library offered Remove folder')
    $dialog = Wait-Until { Find-Dialog $window 'Remove *' } 10 'the remove confirmation opened'
    Check 'Remove folder confirmation - the dialog is open at the close' ($null -ne $dialog) "'$($dialog.Current.Name)'"
    # The profile's log already holds case 6's shutdown, so this launch's lines are read on their own.
    $problem = Close-TunqioShell $p $window 20
    Check 'Remove folder confirmation - Tunqio exits with code 0' ($null -eq $problem) "$(if ($problem) { $problem } else { 'exit code 0' })"
    if ($problem) { $script:failures += $problem }
    Start-Sleep -Milliseconds 300
    $log = @(Read-Log $libraryRoot | Select-Object -Skip $lines)
    $steps = @('Close: main window close requested', 'Shutdown: main window closed', 'Session .* ending', 'Shutdown: host disposed' | ForEach-Object { $pattern = $_; @($log | Where-Object { $_ -match $pattern }).Count })
    $bad = @($log | Where-Object { $_ -match '\[FTL\]|Could not save the queue|Audio teardown did not finish|Audio teardown failed' })
    Check 'Remove folder confirmation - the log has the close request and every shutdown step to the host' (($steps | Where-Object { $_ -lt 1 }).Count -eq 0) "counts $($steps -join ', ')"
    Check 'Remove folder confirmation - nothing fatal, and no queue capture or audio teardown failure' ($bad.Count -eq 0) "$(if ($bad.Count) { $bad[0] } else { 'none logged' })"

    # ---- 8. close-to-tray on, New playlist open -------------------------------------------------------------------------
    Write-Output 'case 8: close-to-tray on, New playlist open'
    $root = New-Root 'tray' '{ "ui.welcomeShown": false, "ui.closeToTray": true }'
    $p = Start-Shell $root
    $window = Get-MainWindow $p
    Open-Playlists $window
    Invoke-Element (Find-Named $window 'New playlist')
    $dialog = Wait-Until { Find-Dialog $window 'New playlist' } 10 'the New playlist dialog opened'
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $hidden = $null
    try { $hidden = Wait-Until { if (-not (Get-WindowOf $p.Id)) { 'hidden' } } 10 'the window hid' } catch { }
    Start-Sleep -Seconds 2
    Check 'tray - a close with a dialog open hides the window to the tray' ($hidden -eq 'hidden') "$(if ($hidden) { 'no UIA window' } else { 'the window is still shown' })"
    Check 'tray - the process keeps running' (-not $p.HasExited) "pid $($p.Id)"
    $hideLine = @(Read-Log $root | Where-Object { $_ -match 'Tray: main window hidden to the tray on close' })
    Check 'tray - the log records the hide' ($hideLine.Count -eq 1) "$($hideLine.Count) line(s)"
    $second = Start-Shell $root 'tunqio://show'
    $exited = $second.WaitForExit(15000)
    Check 'tray - tunqio://show hands over and exits' ($exited) "pid $($second.Id)"
    $window = Get-MainWindow $p
    $dialog = Find-Dialog $window 'New playlist'
    Check 'tray - the window comes back with the dialog still open' ($null -ne $dialog) "$(if ($dialog) { 'dialog open' } else { 'no dialog' })"
    if ($dialog) { Invoke-Element (Find-Named $dialog 'Cancel'); Wait-Until { -not (Find-Dialog $window 'New playlist') } 10 'the dialog closed' | Out-Null }
    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 10 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    Select-Element (Wait-Until { Find-Named $overlay 'Appearance settings' } 10 'the overlay listed Appearance')
    $switch = (Wait-Until { Find-Named $overlay 'Close to the tray' } 10 'Appearance offered Close to the tray').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ("$($switch.Current.ToggleState)" -eq 'On') { $switch.Toggle() }
    Start-Sleep -Milliseconds 800
    Assert-CleanClose 'tray, after turning close-to-tray off' $p $window $root
}
catch {
    if (-not $refused) {
        $script:failures += "the run stopped: $($_.Exception.Message)"
        Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
    }
}
finally {
    # Only the processes this run started, each through Close-TunqioShell (T-188).
    foreach ($launched in $script:launched) {
        if ($launched -and -not $launched.HasExited) {
            $problem = Close-TunqioShell $launched (Get-WindowOf $launched.Id) 20
            if ($problem) { $script:failures += "cleanup: $problem" }
        }
    }
    if (-not $Keep -and (Test-Path $scratch)) {
        try { Remove-Item -Recurse -Force $scratch } catch { Write-Output "  note  scratch folder not removed: $($_.Exception.Message)" }
    }
    elseif ($Keep) { Write-Output "  note  kept $scratch" }
}

Write-Output '  note  not opened: the tag editor (F2 or the row menu by Shift+F10/right click) and the shortcut conflict (a typed chord) need keystrokes'
# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($refused) {
    Write-Output "check-dialog-close: REFUSED (a Tunqio process was still running after $WaitMinutes minute(s))"
    exit 2
}
if ($script:failures.Count -eq 0) {
    Write-Output 'check-dialog-close: PASS'
    exit 0
}
Write-Output "check-dialog-close: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
