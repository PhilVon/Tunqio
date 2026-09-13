<#
.SYNOPSIS
  E2-S2 AC-70 and E2-S5 AC-239: every control in the controls panel is reachable by keyboard and named for
  Narrator, and the Queue button opens a panel whose own controls are too. T-168: and every one of them is on
  screen, whole, at the window widths the shell is used at.

  Launches the shell and reads its automation tree the way Narrator does, rather than asking the app what it
  believes about itself. A glyph is not a name, and a Border with a Tapped handler is not a button: both of those
  mistakes look right on screen and are invisible until someone tabs through, which is why this is a real UIA walk
  and not an assertion inside the app.

  Names carrying state ("Shuffle off", "Repeat all") are checked as prefixes, since which state the app happens to
  be in when the script runs is not the point.

  WHAT IT TOUCHES. It resizes and moves its own window and invokes the Queue button. It sends no keystrokes and
  changes no focus outside that window.
.PARAMETER Exe
  The built shell. Defaults to the Debug x64 output.
.PARAMETER Seconds
  How long to give the window before reading the tree.
.PARAMETER Widths
  Window widths the geometry is measured at, comma-separated ("1600,800"). The defaults span all three ShellLayout
  modes, and the Compact ones matter: that is where the controls panel is a full-width bar and every control lays
  out at its natural size. A string rather than [int[]]: under powershell.exe -File, "1600,800,640" converts to
  the single integer 1600800640, the commas read as thousands separators, and a run measured one absurd window
  and printed PASS (T-168).
#>
[CmdletBinding()]
param(
    # T-161: drive a build that is older than the source on purpose (comparing against an old shell).
    [switch]$SkipFreshnessCheck,
    [string]$Exe,
    [int]$Seconds = 9,
    [string]$Widths = '1600,1200,1000,800,640'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# Resolved in the body rather than in the param default: $PSScriptRoot is empty there under powershell.exe -File,
# which made the default resolve to \..\artifacts and the script refuse a build that existed (T-168).
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\artifacts\bin\Tunqio.App\debug_win-x64\Tunqio.exe' }
if (-not (Test-Path $Exe)) { throw "$Exe not found; build the solution: msbuild Tunqio.sln -restore -p:Configuration=Debug -p:Platform=x64 (T-161)." }

# T-161: a harness driving a build that predates its own source reports the OLD binary's behaviour, and every
# symptom of that reads as a product bug. Refuse up front and say which binary is behind.
. (Join-Path $PSScriptRoot 'assert-fresh-build.ps1')
if (-not $SkipFreshnessCheck) { Assert-FreshBuild -AppDir (Split-Path $Exe) }

. (Join-Path $PSScriptRoot 'uia-geometry.ps1')

# Prefix, control type. One row per control the transport panel owes the keyboard and Narrator.
$expected = @(
    @{ Name = 'Previous track'; Type = 'Button' },
    @{ Name = 'Play';           Type = 'Button' },   # or Pause, depending on state
    @{ Name = 'Next track';     Type = 'Button' },
    @{ Name = 'Elapsed time';   Type = 'Button' },
    @{ Name = 'Seek';           Type = 'Slider' },
    @{ Name = 'Shuffle';        Type = 'Button' },
    @{ Name = 'Repeat';         Type = 'Button' },
    @{ Name = 'Mute';           Type = 'Button' },   # or Unmute
    @{ Name = 'Volume';         Type = 'Slider' },
    @{ Name = 'Queue';          Type = 'Button' }   # E2-S5: opens the queue panel
)

# What the queue panel owes once it is open. The rows carry their own names, which only exist when something is
# queued; these are the panel's own furniture, which is there whether the queue is empty or not.
$expectedInQueue = @(
    @{ Name = 'Clear upcoming'; Type = 'Button' },
    @{ Name = 'Upcoming tracks'; Type = 'List' }
)

$process = Start-Process $Exe -PassThru
try {
    Start-Sleep -Seconds $Seconds
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $window) { throw 'The shell window never appeared in the automation tree.' }

    $found = @{}
    $all = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        $type = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if ($type -notin @('Button', 'Slider')) { continue }
        $found[$element.Current.Name] = [pscustomobject]@{
            Type      = $type
            Enabled   = $element.Current.IsEnabled
            Focusable = $element.Current.IsKeyboardFocusable
        }
    }

    $failures = @()
    foreach ($want in $expected) {
        $match = $found.Keys | Where-Object { $_ -like "$($want.Name)*" } | Select-Object -First 1
        if (-not $match) {
            $failures += "no control named '$($want.Name)...' in the automation tree"
            continue
        }

        $control = $found[$match]
        $state = if ($control.Enabled) { 'enabled' } else { 'disabled' }
        Write-Output ("  {0,-8} '{1}'  {2}, focusable={3}" -f $control.Type, $match, $state, $control.Focusable)
        if ($control.Type -ne $want.Type) {
            $failures += "'$match' is a $($control.Type); Narrator needs a $($want.Type) to announce it correctly"
        }
        # A disabled control is deliberately unreachable - the scrubber is, with nothing loaded - so focusability
        # is only owed by the ones that are actually offered.
        if ($control.Enabled -and -not $control.Focusable) {
            $failures += "'$match' is enabled but cannot be reached by keyboard"
        }
    }

    # ---- E2-S5: the Queue button opens the panel, and what is in it is named too --------------------------------
    Write-Output ''
    # By name *and* control type: a button's own label is an element with the same name, and picking that one up
    # would report a Text where the tree in fact has a perfectly good Button.
    function Find-ByNameAndType($scope, $name, $type) {
        $scope.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, $name)),
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::$type)))))
    }

    $queueButton = Find-ByNameAndType $window 'Queue' 'Button'
    if (-not $queueButton) {
        $failures += "no control named 'Queue' to open the queue panel with"
    }
    else {
        $queueButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 2
        foreach ($want in $expectedInQueue) {
            $control = Find-ByNameAndType $window $want.Name $want.Type
            if (-not $control) {
                $failures += "the queue panel opened without a $($want.Type) named '$($want.Name)'"
                continue
            }

            $state = if ($control.Current.IsEnabled) { 'enabled' } else { 'disabled' }
            Write-Output ("  {0,-8} '{1}'  {2}, focusable={3}" -f
                $want.Type, $want.Name, $state, $control.Current.IsKeyboardFocusable)
        }
        # Closed the way it was opened, so the geometry below starts from the shell as a person leaves it.
        try { $queueButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        Start-Sleep -Milliseconds 800
    }

    # ---- T-168: where the controls ARE, not only that they exist -----------------------------------------------
    #
    # Everything above passed on a build in which Shuffle could not be seen. Measured on that build, window 900 px
    # tall: the controls panel is 238 px wide at 1600, 195 at 1200 and 128 at 1000, and the transport's modes-and-
    # volume row needs about 240. At 1000 px Shuffle is IsOffscreen with an empty rectangle and Previous and Next
    # are 27 px against their natural 38; at 1200 Shuffle is 15 px. At 800 and 640 the panel is a full-width bar
    # and every control is its natural size again (T-182).
    #
    # THE INVARIANTS, per width. (1) No transport control is offscreen while its panel is on screen - T-137's shape,
    # in the tree and nothing to see. (2) Every control was measured, so a walk that lost some cannot pass. (3) No
    # fixed-size control is narrower than its natural width, which is the widest it measured at any width; that is
    # how clipping inside a centred StackPanel shows, because the panel does not grow and a boundary check between
    # siblings would pass. The Seek slider is the one control that sizes to its room, and is held to a floor
    # instead: 60 px, under which a scrubber has no travel to aim along. (4) The queue flyout's presenter lies
    # inside the window.
    Write-Output ''
    $widthList = @($Widths -split ',' | Where-Object { $_.Trim() } | ForEach-Object { [int]$_.Trim() })
    if ($widthList.Count -eq 0) { throw "-Widths '$Widths' names no width" }

    # A harness that changes what it measures is not measuring. The first run of this section read Shuffle off at
    # launch and Shuffle on by 1000 px, and a step-by-step probe of the same actions could not make it happen
    # again. So the transport's state is read before the walk and after it, and any difference is a failure
    # rather than a curiosity.
    function Get-TransportState($scope) {
        $names = @()
        foreach ($e in $scope.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if (($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') -ne 'Button') { continue }
            $n = $e.Current.Name
            if ($n -like 'Shuffle*' -or $n -like 'Repeat*' -or $n -like 'Mute*' -or $n -like 'Unmute*' -or
                $n -eq 'Play' -or $n -eq 'Pause') { $names += $n }
        }
        return (($names | Sort-Object -Unique) -join ', ')
    }
    $stateBefore = Get-TransportState $window

    $readings = @()
    foreach ($w in $widthList) {
        Set-UiaWindowSize -ProcessId $process.Id -Width $w -Height 900
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
        if (-not $window) { $failures += "the shell window left the automation tree at ${w}px"; continue }
        $windowRect = Get-UiaRect $window
        $panel = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'Playback controls panel')))
        if (-not $panel) { $failures += "no playback controls panel in the tree at ${w}px"; continue }
        $panelRect = Get-UiaRect $panel

        $seen = @{}
        $offscreen = @()
        foreach ($element in $panel.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $type = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            if ($type -notin @('Button', 'Slider')) { continue }
            $name = $element.Current.Name
            $want = $expected | Where-Object { $name -like "$($_.Name)*" -and $_.Type -eq $type } | Select-Object -First 1
            if (-not $want -or $seen.ContainsKey($want.Name)) { continue }
            $seen[$want.Name] = $true
            $rect = Get-UiaRect $element
            $readings += [pscustomobject]@{ Width = $w; Name = $want.Name; Rect = $rect }
            if ($rect.Offscreen) { $offscreen += "'$name'" }
        }

        Write-Output ("  {0,5}px  panel {1} ({2} px wide), {3} of {4} controls found, {5} with nothing on screen" -f
            $w, $panelRect.Describe, $panelRect.Width, $seen.Count, $expected.Count, $offscreen.Count)
        if (-not $panelRect.Offscreen -and $offscreen.Count -gt 0) {
            $failures += "at ${w}px $($offscreen -join ', ') are in the automation tree with nothing on screen, inside a panel spanning $($panelRect.Describe)"
        }
        if ($seen.Count -lt $expected.Count) {
            $missing = $expected | Where-Object { -not $seen.ContainsKey($_.Name) } | ForEach-Object { $_.Name }
            $failures += "at ${w}px only $($seen.Count) of $($expected.Count) transport controls were measured; not found: $($missing -join ', ')"
        }

        $queue = Find-ByNameAndType $window 'Queue' 'Button'
        if ($queue -and -not (Get-UiaRect $queue).Offscreen) {
            $queue.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1500
            $clear = Find-ByNameAndType $window 'Clear upcoming' 'Button'
            if (-not $clear) {
                $failures += "at ${w}px the Queue button did not open the queue flyout"
            }
            else {
                $presenter = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($clear)
                $problem = Test-UiaInside (Get-UiaRect $presenter) $windowRect 'the queue flyout' "the ${w}px window"
                if ($problem) { $failures += "at ${w}px $problem" }
            }
            try { $queue.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
            Start-Sleep -Milliseconds 800
        }
    }
    foreach ($problem in (Get-UiaClippedControls -Readings $readings -Stretch @('Seek') -StretchFloor 60)) {
        $failures += $problem
    }

    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    $stateAfter = if ($window) { Get-TransportState $window } else { '(no window)' }
    Write-Output "  transport state before the walk: $stateBefore; after: $stateAfter"
    if ($stateAfter -ne $stateBefore) {
        $failures += "the geometry walk changed the transport's state from '$stateBefore' to '$stateAfter'; it only resizes the window and opens and closes the queue, so something in that is pressing a control"
    }

    Write-Output ''
    if ($failures.Count -eq 0) {
        Write-Output "PASS: $($expected.Count) controls-panel controls and $($expectedInQueue.Count) in the queue panel, each named for Narrator, reachable when enabled, and whole on screen at $($Widths -join ', ') px"
        exit 0
    }

    foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
    exit 1
}
finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 3 }
    if (-not $process.HasExited) { $process.Kill() }
}
