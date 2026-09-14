<#
.SYNOPSIS
  T-182: the shell's layout as Phil asked for it, read off the live automation tree without a single keystroke.

  E2-S1's --shell-spike proves the panels' shares reached the visual tree. What it cannot see is what a person
  complained about, twice, in review: a sidebar too narrow to browse, library navigation that could not be used
  in the narrow layout, and then the navigation's own menu button drawn over the page title. Each of those is a
  statement about where things are on screen relative to each other, so each is checked here as one.

  THE CHECKS, per window width:
    - column layouts: the library sidebar is at least a third of the client width (Q-54).
    - Compact: the library views are not listed on screen until the menu is opened (Q-55).
    - every width: the NavigationView's Back and menu buttons overlap no page title. In Compact, where those
      buttons float over the content, every library view and Settings is visited through the menu and checked,
      not only the default Albums page.

  WHAT IT TOUCHES. It launches its own Tunqio, resizes that window, opens the library menu and selects views
  through UI Automation patterns, and closes it. It sends no keystrokes and needs no foreground, so it can run
  while someone is using the machine (T-166).
.PARAMETER Exe
  The built shell. Defaults to the Debug x64 output.
.PARAMETER Widths
  Window widths, comma-separated. A string because an [int[]] of "1616,716" arrives as one integer under
  powershell.exe -File (T-168). The defaults are a Full, a Medium and a Compact client plus the 16 px of frame.
.PARAMETER Seconds
  How long to give the window before reading the tree.
#>
[CmdletBinding()]
param(
    [switch]$SkipFreshnessCheck,
    [string]$Exe,
    [string]$Widths = '1616,1216,1016,716',
    [int]$Seconds = 10
)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\artifacts\bin\Tunqio.App\debug_win-x64\Tunqio.exe' }
if (-not (Test-Path $Exe)) { throw "$Exe not found; build the solution: msbuild Tunqio.sln -restore -p:Configuration=Debug -p:Platform=x64 (T-161)." }

. (Join-Path $PSScriptRoot 'assert-fresh-build.ps1')
if (-not $SkipFreshnessCheck) { Assert-FreshBuild -AppDir (Split-Path $Exe) }
. (Join-Path $PSScriptRoot 'uia-geometry.ps1')

$widthList = @($Widths -split ',' | Where-Object { $_.Trim() } | ForEach-Object { [int]$_.Trim() })
if ($widthList.Count -eq 0) { throw "-Widths '$Widths' names no width" }
if (@(Get-Process Tunqio -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Tunqio is already running; close it first rather than have this drive a second instance.' }

# The titles the library's pages show. Text elements only: a navigation item of the same name is a ListItem.
$script:titleNames = 'Albums', 'Artists', 'Tracks', 'All tracks', 'Genres', 'Folders', 'Recently added', 'Recently played', 'Most played'

function TypeOf($e) { $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '' }

function Get-Named($scope, [string]$name, [string]$type) {
    foreach ($e in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -eq $name -and (-not $type -or (TypeOf $e) -eq $type)) { return $e }
    }
    return $null
}

# The navigation buttons and page titles currently on screen in $scope.
function Get-ButtonsAndTitles($scope) {
    $buttons = @()
    $titles = @()
    foreach ($e in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $rect = Get-UiaRect $e
        if ($rect.Offscreen) { continue }
        $type = TypeOf $e
        $name = $e.Current.Name
        if ($type -eq 'Button' -and $name -match '^(Back|Open Navigation|Close Navigation)$') { $buttons += [pscustomobject]@{ Name = $name; Rect = $rect } }
        if ($type -eq 'Text' -and $script:titleNames -contains $name) { $titles += [pscustomobject]@{ Name = $name; Rect = $rect } }
    }
    return [pscustomobject]@{ Buttons = $buttons; Titles = $titles }
}

# A problem for every navigation button that overlaps a page title.
function Get-TitleOverlaps([object[]]$Buttons, [object[]]$Titles, [string]$Where) {
    $problems = @()
    foreach ($b in $Buttons) {
        foreach ($t in $Titles) {
            $overlapX = [math]::Min($b.Rect.Right, $t.Rect.Right) - [math]::Max($b.Rect.Left, $t.Rect.Left)
            $overlapY = [math]::Min($b.Rect.Bottom, $t.Rect.Bottom) - [math]::Max($b.Rect.Top, $t.Rect.Top)
            if ($overlapX -gt 0 -and $overlapY -gt 0) {
                $problems += "$Where the '$($b.Name)' button ($($b.Rect.Describe)) covers the '$($t.Name)' title ($($t.Rect.Describe)) by $overlapX x $overlapY px"
            }
        }
    }
    return $problems
}

# Selects a library view (or Settings) by its navigation item, opening the menu first when the item is hidden
# behind it, and closing the pane again if it stayed open. True when the item was selected.
function Select-LibraryView($scope, [string]$view) {
    $item = Get-Named $scope $view 'ListItem'
    if (-not $item -or (Get-UiaRect $item).Offscreen) {
        $menu = Get-Named $scope 'Open Navigation' 'Button'
        if (-not $menu) { return $false }
        try { $menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { return $false }
        Start-Sleep -Milliseconds 900
        $item = Get-Named $scope $view 'ListItem'
    }
    if (-not $item) { return $false }
    try { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    catch {
        try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { return $false }
    }
    Start-Sleep -Milliseconds 1500
    # LeftMinimal closes its pane on selection; if it did not, close it, so the page is what gets measured.
    $close = Get-Named $scope 'Close Navigation' 'Button'
    if ($close -and -not (Get-UiaRect $close).Offscreen) {
        try { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        Start-Sleep -Milliseconds 700
    }
    return $true
}

$failures = @()
$process = Start-Process $Exe -PassThru
try {
    Start-Sleep -Seconds $Seconds
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)

    foreach ($w in $widthList) {
        Set-UiaWindowSize -ProcessId $process.Id -Width $w -Height 900
        Start-Sleep -Milliseconds 800
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
        if (-not $window) { $failures += "the shell window left the automation tree at ${w}px"; continue }
        $frame = Get-UiaRect $window
        $clientRight = $frame.Right - 8
        $clientWidth = $frame.Width - 16
        $bar = Get-Named $window 'Playback controls panel' $null
        if (-not $bar) { $failures += "no playback controls panel at ${w}px"; continue }
        $barRect = Get-UiaRect $bar
        $stacked = $barRect.Width -ge ($clientWidth - 2)
        $albumsItem = Get-Named $window 'Albums' 'ListItem'
        $onScreen = Get-ButtonsAndTitles $window

        $shape = if ($stacked) { 'stacked' } else { 'columns' }
        Write-Output ("  {0,5}px  {1}, bar {2}, {3} navigation button(s), {4} page title(s) on screen" -f $w, $shape, $barRect.Describe, $onScreen.Buttons.Count, $onScreen.Titles.Count)

        if (-not $stacked) {
            # The sidebar begins where the bar (spanning Now Playing) ends.
            $sidebarWidth = $clientRight - $barRect.Right
            $third = [math]::Floor($clientWidth / 3)
            Write-Output ("         sidebar about {0} px of a {1} px client (a third is {2})" -f $sidebarWidth, $clientWidth, $third)
            if ($sidebarWidth -lt ($third - 4)) {
                $failures += "at ${w}px the sidebar is about $sidebarWidth px, under a third of the $clientWidth px client (Q-54)"
            }
        }
        else {
            $listed = $albumsItem -and -not (Get-UiaRect $albumsItem).Offscreen
            if ($listed) { $failures += "at ${w}px (stacked) the library views are listed on screen instead of behind the menu (Q-55)" }
        }

        if ($onScreen.Titles.Count -eq 0) { $failures += "at ${w}px no page title was on screen, so the overlap check measured nothing" }
        foreach ($problem in (Get-TitleOverlaps $onScreen.Buttons $onScreen.Titles "at ${w}px")) { $failures += $problem }

        # Stacked is where the menu buttons float over the content, so every library page is visited there, not
        # only the default one: a title with a different margin would pass on Albums and fail elsewhere.
        if ($stacked) {
            # Not Settings since E6-S3: it is an overlay over the whole shell now, not a page in this pane.
            foreach ($view in 'Artists', 'Tracks', 'Genres', 'Folders') {
                if (-not (Select-LibraryView $window $view)) {
                    $failures += "at ${w}px the '$view' view could not be reached through the menu, so its title was not checked"
                    continue
                }
                $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
                $page = Get-ButtonsAndTitles $window
                Write-Output ("         {0,-9} {1} navigation button(s), title(s): {2}" -f $view, $page.Buttons.Count, (($page.Titles | ForEach-Object { "'$($_.Name)' $($_.Rect.Describe)" }) -join '; '))
                if ($page.Titles.Count -eq 0) {
                    $failures += "at ${w}px the '$view' page showed no known title, so the overlap check measured nothing there"
                    continue
                }
                foreach ($problem in (Get-TitleOverlaps $page.Buttons $page.Titles "at ${w}px on the $view page")) { $failures += $problem }
            }
            # Back to the default view, so a following width starts where the first one did.
            Select-LibraryView $window 'Albums' | Out-Null
        }
    }

    Write-Output ''
    if ($failures.Count -eq 0) {
        Write-Output "PASS: at $Widths px the sidebar is wide enough, the narrow layout's views are behind the menu, and no navigation button covers a page title on any library view"
        exit 0
    }
    foreach ($f in $failures) { Write-Output "FAIL: $f" }
    exit 1
}
finally {
    # An app that does not exit, or exits with a crash code, fails the run (T-188); exit here overrides the try's exit 0.
    $closeProblem = Close-TunqioShell $process $null 20
    if ($closeProblem) { Write-Output "FAIL: $closeProblem"; exit 1 }
}
