<#
.SYNOPSIS
  E6-S5 (T-71): Settings > About & Diagnostics in a running shell, UIA only. Opens the overlay from the controls
  bar, selects the About section (the last one), reads the version lines, the BASS attribution and the licence
  list, opens a licence text, checks the buttons and the switches by automation name, reads the performance
  readout and watches it move, opens the logs folder (and closes the Explorer window it opened), flips the
  crash-reporting switch and reads diagnostics.crashReporting back, then measures the page at a wide window and
  two narrow ones. The diagnostics zip is written by the app's own --export-diagnostics switch, because a file
  picker cannot be driven by UIA, and is opened and read after the app exits.

  UIA patterns only: no keystrokes and no pointer.

  WHAT IT CHANGES. diagnostics.crashReporting is set to true and back to false through the page (and restored to
  its previous value in the finally). A zip is written to %TEMP% and deleted. An Explorer window is opened on the
  logs folder and closed again. The launch count and the log grow, as they do for any launch. Nothing is played.
.PARAMETER Exe
  The built shell. Defaults to the Release x64 output.
.PARAMETER Seconds
  How long to give the window before reading the tree; audio and the renderer come up after the first frame.
.PARAMETER Widths
  Comma-separated window widths for the geometry pass. A string because under powershell.exe -File an [int[]]
  of "1616,716" becomes one integer.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 10,
    [string]$Widths = '1616,716,560'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\artifacts\bin\Tunqio.App\release_win-x64\Tunqio.exe' }
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "The shell is not built at $Exe." }
$Exe = $resolved.Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.IO.Compression.FileSystem
. (Join-Path $here 'uia-geometry.ps1')
$widthList = @($Widths -split ',' | Where-Object { $_.Trim() } | ForEach-Object { [int]$_.Trim() })
if ($widthList.Count -eq 0) { throw "-Widths '$Widths' names no width" }

if (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Tunqio is already running. This script changes and restores a setting through the instance it launches, so it will not touch one somebody is using.'
}

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$settingsPath = Join-Path $env:LOCALAPPDATA 'Tunqio\settings.json'
$logDir = Join-Path $env:LOCALAPPDATA 'Tunqio\logs'
$propsVersion = ([xml](Get-Content (Join-Path $here '..\Directory.Build.props') -Raw)).Project.PropertyGroup.TunqioVersion | Where-Object { $_ } | Select-Object -First 1
$zip = Join-Path $env:TEMP ('tunqio-check-about-' + [guid]::NewGuid().ToString('N') + '.zip')
$script:failures = @()

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
function Find-ById($scope, [string]$id) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}
function Find-AllOfType($scope, $type) {
    @($scope.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type))))
}
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Get-ToggleState($element) { $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() }
function Set-Toggle($element) { $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function Get-Value($element) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Set-ScrollTo($surface, [double]$percent) {
    try { $surface.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, $percent) } catch { }
    Start-Sleep -Milliseconds 500
}

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "  ok    $what ($detail)" }
    else { $script:failures += $what; Write-Output "  FAIL  $what ($detail)" }
}

function Get-StoredCrashReporting {
    if (-not (Test-Path $settingsPath)) { return $null }
    return (Get-Content $settingsPath -Raw | ConvertFrom-Json).'diagnostics.crashReporting'
}

function Close-ExplorerOn([string]$folder) {
    $closed = 0
    try {
        $shell = New-Object -ComObject Shell.Application
        foreach ($w in @($shell.Windows())) {
            try {
                $location = [uri]::UnescapeDataString([string]$w.LocationURL).Replace('/', '\')
                if ($location -and $location.TrimEnd('\').EndsWith($folder.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) { $w.Quit(); $closed++ }
            }
            catch { }
        }
    }
    catch { }
    return $closed
}

# Selects the About section, opening the navigation menu first when the window is narrow enough to have hidden it.
function Select-AboutSection($overlay) {
    $item = Find-Named $overlay 'About and diagnostics settings'
    if (-not $item -or (Get-UiaRect $item).Offscreen) {
        $menu = $overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq 'Open Navigation' } | Select-Object -First 1
        if ($menu) { Invoke-Element $menu; Start-Sleep -Milliseconds 900 }
        $item = Find-Named $overlay 'About and diagnostics settings'
    }
    if (-not $item) { throw 'the overlay does not list About and diagnostics settings' }
    Select-Element $item
    Start-Sleep -Milliseconds 1200
    $closeNav = $overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq 'Close Navigation' -and -not (Get-UiaRect $_).Offscreen } | Select-Object -First 1
    if ($closeNav) { Invoke-Element $closeNav; Start-Sleep -Milliseconds 700 }
}

$process = $null
$window = $null
$crashBefore = $null
$crashChanged = $false
$reachedEnd = $false
$startedAt = Get-Date
$controlNames = @('Licence list', 'Licence text', 'Open licences folder', 'Redact paths in the export', 'Open logs folder', 'Export diagnostics', 'Send crash reports')

try {
    $crashBefore = Get-StoredCrashReporting
    Write-Output "shell: $Exe"
    Write-Output "Directory.Build.props TunqioVersion: $propsVersion"
    Write-Output "diagnostics.crashReporting before: $(if ($null -eq $crashBefore) { '(unset)' } else { $crashBefore })"
    Write-Output "zip: $zip"
    $process = Start-Process $Exe -ArgumentList @('--export-diagnostics', "`"$zip`"", '--redact-paths') -PassThru
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
    $window = Wait-Until { $A::RootElement.FindFirst($TS::Children, $byPid) } 30 'the shell window appeared'
    Start-Sleep -Seconds $Seconds
    Set-UiaWindowSize -ProcessId $process.Id -Width $widthList[0] -Height 900

    # ---- open the section ---------------------------------------------------------------------------------------------
    Invoke-Element (Wait-Until { Find-Named $window 'Open settings' } 10 'the controls bar offered Settings')
    $overlay = Wait-Until { Find-Named $window 'Settings overlay' } 10 'the settings overlay opened'
    Start-Sleep -Milliseconds 800
    $items = @($overlay.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -like '* settings' })
    Check 'About and diagnostics is the last section in the list' ($items.Count -gt 0 -and $items[-1].Current.Name -eq 'About and diagnostics settings') "sections: $(($items | ForEach-Object { $_.Current.Name }) -join ', ')"
    Select-AboutSection $overlay
    $surface = Wait-Until { Find-Named $overlay 'About settings surface' } 8 'the About page appeared'

    # ---- version ------------------------------------------------------------------------------------------------------
    $appVersion = Find-ById $overlay 'AppVersion'
    $appText = if ($appVersion) { $appVersion.Current.Name } else { '' }
    Check 'The app version is the product name and TunqioVersion from Directory.Build.props' ($appText -eq "Tunqio $propsVersion") "'$appText'"
    $coreVersion = Find-ById $overlay 'CoreVersion'
    $coreText = if ($coreVersion) { $coreVersion.Current.Name } else { '' }
    Check 'The core version names mpcore and its ABI' ($coreText -match '^mpcore \d+\.\d+\.\d+ . ABI \d+\.\d+$') "'$coreText'"
    $bass = Find-ById $overlay 'BassAttribution'
    $bassText = if ($bass) { $bass.Current.Name } else { '' }
    Check 'The BASS attribution sentence is on the page' ($bassText -like '*BASS audio library*' -and $bassText -like '*un4seen.com*') "'$($bassText.Substring(0, [math]::Min(60, $bassText.Length)))...'"

    # ---- licences -----------------------------------------------------------------------------------------------------
    # 'Licence list', not the 'Licences' section header, which is a text block named by its text and comes first.
    $list = Find-Named $overlay 'Licence list'
    $rows = @()
    if ($list) { $rows = Find-AllOfType $list ([System.Windows.Automation.ControlType]::ListItem) }
    $rowNames = @($rows | ForEach-Object { $_.Current.Name })
    Check 'The licence list has the notices, every BASS package and the vendored sources' ($rowNames.Count -ge 10 -and $rowNames[0] -like 'Third-party notices*' -and ($rowNames -match '^bass 2\.4\.\d+ ').Count -eq 1 -and ($rowNames -match '^bass_ape ').Count -eq 1 -and ($rowNames -match '^pffft ').Count -eq 1) "$($rowNames.Count) rows: $($rowNames -join ' | ')"
    $textBox = Find-Named $overlay 'Licence text'
    $noticesText = if ($textBox) { Get-Value $textBox } else { '' }
    Check 'The notices file is open in the text box before anything is chosen' ($noticesText -like '# Third-party notices*') "$($noticesText.Length) chars"
    $bassRow = $rows | Where-Object { $_.Current.Name -match '^bass 2\.4\.\d+ ' } | Select-Object -First 1
    if ($bassRow) {
        try { $bassRow.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
        Select-Element $bassRow
        Start-Sleep -Milliseconds 800
        $licenceText = Get-Value (Find-Named $overlay 'Licence text')
        Check 'Choosing bass shows the BASS licence text' ($licenceText -like '*BASS*' -and $licenceText -ne $noticesText) "$($licenceText.Length) chars, starts '$($licenceText.Substring(0, [math]::Min(40, $licenceText.Length)).Replace("`r", ' ').Replace("`n", ' '))'"
    }
    else { Check 'Choosing bass shows the BASS licence text' $false 'no bass row' }

    # ---- controls by name ----------------------------------------------------------------------------------------------
    foreach ($name in $controlNames) {
        $element = Find-Named $overlay $name
        Check "The page has '$name'" ($null -ne $element) $(if ($element) { $element.Current.ControlType.ProgrammaticName } else { 'not in the tree' })
    }
    $redact = Find-Named $overlay 'Redact paths in the export'
    Check 'Redact paths is on by default' ($redact -and (Get-ToggleState $redact) -eq 'On') "$(if ($redact) { Get-ToggleState $redact })"
    $hint = Find-ById $overlay 'CrashReportingHint'
    Check 'The crash reporting text says nothing is sent yet' ($hint -and $hint.Current.Name -like 'Nothing is sent yet*') "$(if ($hint) { $hint.Current.Name.Substring(0, [math]::Min(50, $hint.Current.Name.Length)) })"

    # ---- readout ------------------------------------------------------------------------------------------------------
    $dropouts = Find-ById $overlay 'DropoutsReadout'
    $frame = Find-ById $overlay 'FrameTimeReadout'
    $memory = Find-ById $overlay 'MemoryReadout'
    $dropText = if ($dropouts) { $dropouts.Current.Name } else { '' }
    $frameText = if ($frame) { $frame.Current.Name } else { '' }
    $memoryText = if ($memory) { $memory.Current.Name } else { '' }
    Check 'Dropouts reads underruns against callbacks' ($dropText -match '^\d+ underruns? in \d+ callbacks') "'$dropText'"
    Check 'Frame time reads the average and worst frame' ($frameText -match '^avg [\d.]+ ms . max [\d.]+ ms . [\d.]+ fps') "'$frameText'"
    Check 'Memory reads the working set in MB' ($memoryText -match '^\d+ MB working set$') "'$memoryText'"
    Start-Sleep -Milliseconds 1600
    $dropLater = (Find-ById $overlay 'DropoutsReadout').Current.Name
    $frameLater = (Find-ById $overlay 'FrameTimeReadout').Current.Name
    Check 'The readout moves while the page is open' ($dropLater -ne $dropText -or $frameLater -ne $frameText) "dropouts '$dropText' -> '$dropLater'; frame '$frameText' -> '$frameLater'"

    # T-71 review: the readout grid had no RowDefinitions, so its three rows drew on one line and blurred as the
    # numbers changed. UIA cannot see pixels, but it can see where each row is and how many text blocks there are.
    # Scrolled to the bottom so the rows are on screen (an offscreen element has no rectangle), then read after the
    # first refresh and again after several more: the same number of text blocks, three rows one below the other.
    Set-ScrollTo $surface 100
    function Get-ReadoutTexts {
        $ids = @('DropoutsReadout', 'FrameTimeReadout', 'MemoryReadout')
        $values = @(Find-AllOfType $overlay ([System.Windows.Automation.ControlType]::Text) | Where-Object { $ids -contains $_.Current.AutomationId })
        $labels = @(Find-AllOfType $overlay ([System.Windows.Automation.ControlType]::Text) | Where-Object { @('Dropouts', 'Frame time', 'Memory') -contains $_.Current.Name })
        return [pscustomobject]@{ Values = $values; Labels = $labels; Count = $values.Count + $labels.Count }
    }
    $first = Get-ReadoutTexts
    Start-Sleep -Milliseconds 3000
    $later = Get-ReadoutTexts
    Check 'The readout holds the same text blocks after several refreshes' ($first.Count -eq 6 -and $later.Count -eq $first.Count) "$($first.Count) text blocks after the first refresh, $($later.Count) after six more"
    $rowRects = @('DropoutsReadout', 'FrameTimeReadout', 'MemoryReadout') | ForEach-Object { $e = Find-ById $overlay $_; [pscustomobject]@{ Id = $_; Rect = $(if ($e) { Get-UiaRect $e } else { $null }) } }
    $stacked = $true
    $rowProblems = @()
    for ($i = 0; $i -lt $rowRects.Count; $i++) {
        $r = $rowRects[$i].Rect
        if (-not $r -or $r.Offscreen) { $stacked = $false; $rowProblems += "$($rowRects[$i].Id) not on screen"; continue }
        if ($i -gt 0 -and $rowRects[$i - 1].Rect -and -not $rowRects[$i - 1].Rect.Offscreen -and $r.Top -lt ($rowRects[$i - 1].Rect.Bottom - 1)) {
            $stacked = $false; $rowProblems += "$($rowRects[$i].Id) starts at $($r.Top), above the end of $($rowRects[$i - 1].Id) at $($rowRects[$i - 1].Rect.Bottom)"
        }
    }
    Check 'The readout rows sit one below the other, not on top of each other' $stacked $(if ($rowProblems.Count) { $rowProblems -join '; ' } else { ($rowRects | ForEach-Object { "$($_.Id) $($_.Rect.Top)..$($_.Rect.Bottom)" }) -join ', ' })
    Set-ScrollTo $surface 0

    # ---- open logs folder ---------------------------------------------------------------------------------------------
    Invoke-Element (Find-Named $overlay 'Open logs folder')
    Start-Sleep -Milliseconds 2500
    $closedExplorer = Close-ExplorerOn $logDir
    Check 'Open logs folder opened the logs directory in Explorer' ($closedExplorer -ge 1) "$closedExplorer Explorer window(s) on $logDir, closed again"

    # ---- crash reporting ----------------------------------------------------------------------------------------------
    $crash = Find-Named $overlay 'Send crash reports'
    $stateBefore = Get-ToggleState $crash
    Set-Toggle $crash
    $crashChanged = $true
    Start-Sleep -Milliseconds 1500
    $storedOn = Get-StoredCrashReporting
    Set-Toggle (Find-Named $overlay 'Send crash reports')
    Start-Sleep -Milliseconds 1500
    $storedOff = Get-StoredCrashReporting
    $expectOn = ($stateBefore -eq 'Off')
    Check 'The crash reporting switch writes diagnostics.crashReporting at once' (($storedOn -eq $expectOn) -and ($storedOff -eq (-not $expectOn))) "switch was $stateBefore; stored $storedOn then $storedOff"

    # ---- geometry: the page reads right at every width ----------------------------------------------------------------
    $readings = @()
    foreach ($width in $widthList) {
        Set-UiaWindowSize -ProcessId $process.Id -Width $width -Height 900
        Start-Sleep -Milliseconds 1000
        $overlay = Find-Named $window 'Settings overlay'
        Select-AboutSection $overlay
        $surface = Find-Named $overlay 'About settings surface'
        $column = Find-Named $overlay 'About settings content'
        $columnRect = Get-UiaRect $column
        $surfaceRect = Get-UiaRect $surface
        $controls = Find-Named $window 'Playback controls panel'
        $controlsRect = Get-UiaRect $controls
        Write-Output "  note  ${width}px window: surface $($surfaceRect.Describe), column $($columnRect.Describe), controls bar from $($controlsRect.Top)"
        Check "At $width px the column fills the surface it is in" ($columnRect.Width -ge ($surfaceRect.Width - 40) -or $columnRect.Width -ge 700) "column $($columnRect.Width) px in a $($surfaceRect.Width) px surface"
        Check "At $width px the page stops short of the controls bar" ($surfaceRect.Bottom -le ($controlsRect.Top + 1)) "surface ends $($surfaceRect.Bottom), controls bar starts $($controlsRect.Top)"

        # The section menu must not cover the title (T-69 review, T-182): only Minimal navigation draws it over the page.
        $buttons = @($overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match '^(Open Navigation|Close Navigation)$' -and -not (Get-UiaRect $_).Offscreen })
        $title = Find-ById $overlay 'Title'
        if (-not $title) { $title = $overlay.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq 'About & Diagnostics' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | Select-Object -First 1 }
        $overlaps = @()
        if ($title) {
            $tr = Get-UiaRect $title
            foreach ($b in $buttons) {
                $br = Get-UiaRect $b
                $x = [math]::Min($br.Right, $tr.Right) - [math]::Max($br.Left, $tr.Left)
                $y = [math]::Min($br.Bottom, $tr.Bottom) - [math]::Max($br.Top, $tr.Top)
                if ($x -gt 0 -and $y -gt 0) { $overlaps += "'$($b.Current.Name)' $($br.Describe) covers the title $($tr.Describe)" }
            }
        }
        Check "At $width px the section menu leaves the page title clear" ($null -ne $title -and $overlaps.Count -eq 0) $(if (-not $title) { 'no title on screen' } elseif ($overlaps.Count) { $overlaps -join '; ' } else { "$($buttons.Count) menu button(s), title $((Get-UiaRect $title).Describe)" })

        # Every control inside the column, at three scroll positions, because a control below the fold is offscreen.
        $seen = @{}
        foreach ($percent in 0, 50, 100) {
            Set-ScrollTo $surface $percent
            $columnRect = Get-UiaRect (Find-Named $overlay 'About settings content')
            foreach ($name in $controlNames) {
                $element = Find-Named $overlay $name
                if (-not $element) { continue }
                $rect = Get-UiaRect $element
                if ($rect.Offscreen) { continue }
                $seen[$name] = $true
                $readings += [pscustomobject]@{ Width = $width; Name = $name; Rect = $rect }
                $problem = Test-UiaInside $rect $columnRect "'$name'" 'the content column'
                if ($problem) { Check "At $width px $problem" $false "scroll $percent%" }
            }
        }
        Set-ScrollTo $surface 0
        $missing = @($controlNames | Where-Object { -not $seen.ContainsKey($_) })
        Check "At $width px every control was on screen at some scroll position" ($missing.Count -eq 0) $(if ($missing.Count) { "never seen: $($missing -join ', ')" } else { "$($seen.Count) controls measured" })
    }
    $clipped = @(Get-UiaClippedControls -Readings $readings -Stretch @('Licence list', 'Licence text', 'Redact paths in the export', 'Send crash reports'))
    Check 'No control is cut off by its container at any width' ($clipped.Count -eq 0) $(if ($clipped.Count) { $clipped -join '; ' } else { "$($readings.Count) readings across $($widthList.Count) widths" })
    Set-UiaWindowSize -ProcessId $process.Id -Width 1616 -Height 900
    Start-Sleep -Milliseconds 800

    # ---- close --------------------------------------------------------------------------------------------------------
    $overlay = Find-Named $window 'Settings overlay'
    Invoke-Element (Find-Named $overlay 'Close settings')
    Start-Sleep -Milliseconds 800
    Check 'Close shuts the overlay' ($null -eq (Find-Named $window 'Settings overlay')) 'overlay gone'
    $reachedEnd = $true
}
catch {
    $script:failures += "the run stopped: $($_.Exception.Message)"
    Write-Output "  FAIL  the run stopped: $($_.Exception.Message)"
}
finally {
    if ($window) {
        try {
            if ($crashChanged -and ((Get-StoredCrashReporting) -ne $crashBefore) -and -not ($null -eq $crashBefore -and (Get-StoredCrashReporting) -eq $false)) {
                $open = Find-Named $window 'Settings overlay'
                if (-not $open) { Invoke-Element (Find-Named $window 'Open settings'); Start-Sleep -Milliseconds 800; $open = Find-Named $window 'Settings overlay' }
                Select-AboutSection $open
                Set-Toggle (Wait-Until { Find-Named $open 'Send crash reports' } 8 'the crash reporting switch')
                Start-Sleep -Milliseconds 1200
                Write-Output "cleanup: diagnostics.crashReporting back to '$(Get-StoredCrashReporting)'"
            }
        }
        catch { Write-Output "WARNING: cleanup did not finish: $($_.Exception.Message). Check Settings > About & Diagnostics > Send crash reports." }
        try { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    }
    if ($process -and -not $process.WaitForExit(15000)) { $process.Kill() }
    Close-ExplorerOn $logDir | Out-Null
}

# ---- the zip and the log: read after the app has exited, because the file sink buffers --------------------------------
if ($reachedEnd) {
    $found = $false
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) { if (Test-Path $zip) { $found = $true; break }; Start-Sleep -Milliseconds 500 }
    Check 'The --export-diagnostics switch wrote the zip' $found $zip
    if ($found) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entries = @($archive.Entries | ForEach-Object { $_.FullName })
            $logs = @($entries | Where-Object { $_ -like 'logs/tunqio-*.log' })
            Check 'The zip holds system-info.txt, settings.json and the logs' (($entries -contains 'system-info.txt') -and ($entries -contains 'settings.json') -and $logs.Count -ge 1) "$($entries.Count) entries: $($entries -join ', ')"
            $reader = New-Object System.IO.StreamReader($archive.GetEntry('system-info.txt').Open())
            try { $info = $reader.ReadToEnd() } finally { $reader.Dispose() }
            Check 'system-info.txt names the app, core and ABI versions, the OS, the GPU and the output' (($info -like "*App version: $propsVersion*") -and ($info -match 'Core version: \d+\.\d+\.\d+') -and ($info -match 'ABI version: \d+\.\d+') -and ($info -like '*OS: *') -and ($info -like '*GPU adapter: *') -and ($info -like '*Output device: *') -and ($info -like '*Output format: *')) (($info -split "`n" | Where-Object { $_ -match '^(App version|Core version|ABI version|GPU adapter|Output device|Output format):' }) -join '; ').Trim()
            $settingsEntry = $archive.GetEntry('settings.json')
            $reader = New-Object System.IO.StreamReader($settingsEntry.Open())
            try { $settingsText = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $profile = $env:USERPROFILE
            $escaped = $profile.Replace('\', '\\')
            $leaks = ($settingsText.IndexOf($profile, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or ($settingsText.IndexOf($escaped, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
            Check 'With --redact-paths the copied settings.json does not carry the profile path' (-not $leaks -and ($info -like '*Paths redacted: yes*')) "$($settingsText.Length) chars of settings, $(if ($settingsText -like '*[user profile]*') { 'placeholder present' } else { 'no profile path was in it to replace' })"
        }
        finally { $archive.Dispose() }
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }

    $log = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
    $lines = @(if ($log) { Get-Content $log.FullName })
    $exported = $lines | Where-Object { $_ -match 'Diagnostics exported to' } | Select-Object -Last 1
    Check 'The log records the export' ($null -ne $exported) "$(if ($exported) { $exported.Substring([math]::Max(0, $exported.IndexOf('Diagnostics'))) } else { "no line in $logDir" })"
    $opened = $lines | Where-Object { $_ -match 'Opened the logs folder' } | Select-Object -Last 1
    Check 'The log records the logs folder opening' ($null -ne $opened) "$(if ($opened) { $opened.Substring([math]::Max(0, $opened.IndexOf('Opened'))) } else { 'no line' })"
    $modes = @($lines | Where-Object { $_ -match 'Settings navigation is "?(\w+)"? at (\d+) px' } | ForEach-Object { $Matches[1] + ' at ' + $Matches[2] + ' px' })
    Write-Output "  note  settings navigation modes seen: $($modes -join '; ')"
}

# Printed on every outcome, so a waiter has something to match either way (T-174).
if ($script:failures.Count -eq 0) {
    Write-Output 'check-about: PASS'
    exit 0
}
Write-Output "check-about: FAIL ($($script:failures.Count)): $($script:failures -join '; ')"
exit 1
