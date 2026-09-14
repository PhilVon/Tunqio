<#
.SYNOPSIS
  T-201: every branch of tools/clipboard-guard.ps1's decision and restore, driven by fakes. It never touches the real
  clipboard: Get-Clipboard, Set-Clipboard and Get-ClipboardState are shadowed with functions that throw, the restore
  is only ever handed fake operations, and the script checks at the end that System.Windows.Forms (the only way
  clipboard-guard.ps1 reaches the clipboard) was never loaded into this process.

  Run it with powershell.exe -NoProfile -File tools\test-clipboard-guard.ps1. Exit 0 when every case passes.
  ASCII only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Get-FormsLoaded {
    return @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'System.Windows.Forms' }).Count -gt 0
}
$formsLoadedAtStart = Get-FormsLoaded
# Where System.Windows.Forms got loaded, if it does, so the isolation check can name the call.
$script:formsLoadedBy = $null
[AppDomain]::CurrentDomain.add_AssemblyLoad({
    param($sender, $e)
    if (-not $script:formsLoadedBy -and $e.LoadedAssembly.GetName().Name -eq 'System.Windows.Forms') {
        $script:formsLoadedBy = "after $($script:passed) passing check(s) and $(@($script:failures).Count) failing, in " +
            ((Get-PSCallStack | ForEach-Object { "$($_.Command) at $($_.Location)" }) -join ' <- ')
    }
})

# A string path, not Join-Path: Join-Path autoloads Microsoft.PowerShell.Commands.Management (the module that also
# holds Get-Clipboard), whose assembly loads System.Windows.Forms, and that would blind the isolation check below.
. "$PSScriptRoot\clipboard-guard.ps1"

# Anything that would reach the real clipboard fails the run instead.
$script:realClipboardTouched = @()
function Get-Clipboard { $script:realClipboardTouched += 'Get-Clipboard'; throw 'test touched the real clipboard: Get-Clipboard' }
function Set-Clipboard { $script:realClipboardTouched += 'Set-Clipboard'; throw 'test touched the real clipboard: Set-Clipboard' }
function Get-ClipboardState { $script:realClipboardTouched += 'Get-ClipboardState'; throw 'test touched the real clipboard: Get-ClipboardState' }

$script:failures = @()
$script:passed = 0

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { $script:passed++; Write-Host "  ok    $what" }
    else { $script:failures += "$what - $detail"; Write-Host "  FAIL  $what"; Write-Host "        $detail" }
}

# A fake clipboard: an in-memory state plus the calls made to it.
function New-FakeClipboard($state) {
    return [pscustomobject]@{ State = $state; Clears = 0; Sets = @(); Reads = 0; ClearThrows = $null; SetThrows = $null; ReadThrows = $null; ReadOverride = $null }
}
# The fake operations write 'noise' to the pipeline on purpose: the restore must not pass it on.
# Script-scope variable rather than GetNewClosure, whose dynamic module cannot see this script's functions.
function Invoke-FakeRestore($plan, $fake) {
    $script:fake = $fake
    $clear = {
        $script:fake.Clears++
        if ($script:fake.ClearThrows) { throw $script:fake.ClearThrows }
        $script:fake.State = New-ClipboardState
        'noise that must not leak'
    }
    $set = {
        param($text)
        $script:fake.Sets += , $text
        if ($script:fake.SetThrows) { throw $script:fake.SetThrows }
        $script:fake.State = New-ClipboardState -Formats @('UnicodeText', 'Locale', 'Text', 'OEMText') -HasText $true -Text $text
        'noise that must not leak'
    }
    $read = {
        $script:fake.Reads++
        if ($script:fake.ReadThrows) { throw $script:fake.ReadThrows }
        if ($script:fake.ReadOverride) { return $script:fake.ReadOverride }
        return $script:fake.State
    }
    return , @(Invoke-ClipboardRestore -Plan $plan -Clear $clear -SetText $set -Read $read)
}

$textFormats = @('UnicodeText', 'Locale', 'Text', 'OEMText')

Write-Host 'Get-ClipboardGuardPlan'

$p = Get-ClipboardGuardPlan $null
Check 'no state at all skips' ($p.Action -eq 'Skip' -and $p.Restore -eq 'None' -and $p.Note -like '*could not be read*untouched*') "$($p.Action)/$($p.Restore): $($p.Note)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Unreadable 'this thread is MTA')
Check 'an unreadable clipboard (MTA) skips and says why' ($p.Action -eq 'Skip' -and $p.Restore -eq 'None' -and $p.Note -like '*MTA*untouched*') "$($p.Action)/$($p.Restore): $($p.Note)"

$p = Get-ClipboardGuardPlan (New-ClipboardState)
Check 'an empty clipboard runs and is cleared afterwards' ($p.Action -eq 'Run' -and $p.Restore -eq 'Clear' -and $null -eq $p.Text) "$($p.Action)/$($p.Restore)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats $textFormats -HasText $true -Text 'hello')
Check 'plain text runs and restores that text' ($p.Action -eq 'Run' -and $p.Restore -eq 'Text' -and $p.Text -ceq 'hello') "$($p.Action)/$($p.Restore) '$($p.Text)'"

$tricky = "  Line one`r`nline TWO`twith tab`n" + [char]0x00E9 + [char]0x4E2D + '  '
$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats @('UnicodeText') -HasText $true -Text $tricky)
Check 'text with CRLF, tabs, trailing spaces and non-ASCII is kept exactly' ($p.Action -eq 'Run' -and $p.Restore -eq 'Text' -and $p.Text.Equals($tricky, [System.StringComparison]::Ordinal)) "$($p.Action)/$($p.Restore) length $($p.Text.Length) vs $($tricky.Length)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats @('UnicodeText') -HasText $true -Text '')
Check 'an empty-but-present string restores as text, not as a clear' ($p.Action -eq 'Run' -and $p.Restore -eq 'Text' -and $null -ne $p.Text -and $p.Text -ceq '') "$($p.Action)/$($p.Restore) text null: $($null -eq $p.Text)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats @('UnicodeText') -HasText $true -Text $null)
Check 'text reported but unreadable skips' ($p.Action -eq 'Skip' -and $p.Restore -eq 'None') "$($p.Action)/$($p.Restore)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats @('Bitmap', 'DeviceIndependentBitmap') -HasImage $true)
Check 'an image skips with a non-text note' ($p.Action -eq 'Skip' -and $p.Restore -eq 'None' -and $p.Note -like '*non-text*an image*untouched*') "$($p.Action)/$($p.Restore): $($p.Note)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats @('FileDrop', 'FileNameW', 'Shell IDList Array') -HasFiles $true)
Check 'files skip with a non-text note' ($p.Action -eq 'Skip' -and $p.Restore -eq 'None' -and $p.Note -like '*non-text*files*untouched*') "$($p.Action)/$($p.Restore): $($p.Note)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats @('HTML Format', 'UnicodeText', 'Text') -HasText $true -Text 'copied from a browser')
Check 'rich text (HTML beside the text) skips, since a text restore would lose the HTML' ($p.Action -eq 'Skip' -and $p.Restore -eq 'None' -and $p.Note -like '*HTML Format*') "$($p.Action)/$($p.Restore): $($p.Note)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -HasImage $true)
Check 'an image with no format list still skips' ($p.Action -eq 'Skip') "$($p.Action)/$($p.Restore)"

$p = Get-ClipboardGuardPlan (New-ClipboardState -Formats @('Locale'))
Check 'text-family formats with no text skip rather than clear' ($p.Action -eq 'Skip' -and $p.Restore -eq 'None') "$($p.Action)/$($p.Restore): $($p.Note)"

Write-Host 'Invoke-ClipboardRestore'

$fake = New-FakeClipboard (New-ClipboardState -HasImage $true)
$r = Invoke-FakeRestore (Get-ClipboardGuardPlan $fake.State) $fake
Check 'a skip plan calls nothing and reports nothing' ($r.Count -eq 1 -and $null -eq $r[0] -and $fake.Clears -eq 0 -and $fake.Sets.Count -eq 0 -and $fake.Reads -eq 0) "result count $($r.Count), clears $($fake.Clears), sets $($fake.Sets.Count), reads $($fake.Reads)"

$fake = New-FakeClipboard (New-ClipboardState)
$plan = Get-ClipboardGuardPlan $fake.State
$fake.State = New-ClipboardState -Formats $textFormats -HasText $true -Text '[Playback] the report'
$r = Invoke-FakeRestore $plan $fake
Check 'an empty clipboard is cleared, not set to empty text, and checks clean' ($r.Count -eq 1 -and $null -eq $r[0] -and $fake.Clears -eq 1 -and $fake.Sets.Count -eq 0 -and $fake.Reads -eq 1 -and -not $fake.State.HasText) "result '$($r -join '|')', clears $($fake.Clears), sets $($fake.Sets.Count)"

$fake = New-FakeClipboard (New-ClipboardState)
$plan = Get-ClipboardGuardPlan $fake.State
$fake.ClearThrows = 'OpenClipboard Failed (Exception from HRESULT: 0x800401D0 (CLIPBRD_E_CANT_OPEN))'
$threw = $false
try { $r = Invoke-FakeRestore $plan $fake } catch { $threw = $true }
Check 'a clear that throws is a problem string, not a throw' (-not $threw -and $r.Count -eq 1 -and $r[0] -like 'restoring the clipboard failed: clearing it threw*CLIPBRD_E_CANT_OPEN*') "threw $threw, result '$($r -join '|')'"

$fake = New-FakeClipboard (New-ClipboardState)
$plan = Get-ClipboardGuardPlan $fake.State
$fake.ReadOverride = New-ClipboardState -Formats $textFormats -HasText $true -Text 'still here'
$r = Invoke-FakeRestore $plan $fake
Check 'a clear that leaves text behind is reported' ($r.Count -eq 1 -and $r[0] -like '*empty before*still holds*') "result '$($r -join '|')'"

$fake = New-FakeClipboard (New-ClipboardState -Formats $textFormats -HasText $true -Text $tricky)
$plan = Get-ClipboardGuardPlan $fake.State
$fake.State = New-ClipboardState -Formats $textFormats -HasText $true -Text '[Playback] the report'
$r = Invoke-FakeRestore $plan $fake
Check 'text is put back exactly and checks clean' ($r.Count -eq 1 -and $null -eq $r[0] -and $fake.Clears -eq 0 -and $fake.Sets.Count -eq 1 -and ([string]$fake.Sets[0]).Equals($tricky, [System.StringComparison]::Ordinal) -and $fake.State.Text.Equals($tricky, [System.StringComparison]::Ordinal)) "result '$($r -join '|')', sets $($fake.Sets.Count)"

$fake = New-FakeClipboard (New-ClipboardState -Formats @('UnicodeText') -HasText $true -Text '')
$plan = Get-ClipboardGuardPlan $fake.State
$fake.State = New-ClipboardState -Formats $textFormats -HasText $true -Text '[Playback] the report'
$r = Invoke-FakeRestore $plan $fake
Check 'an empty-but-present string is put back as empty text' ($r.Count -eq 1 -and $null -eq $r[0] -and $fake.Clears -eq 0 -and $fake.Sets.Count -eq 1 -and $null -ne $fake.Sets[0] -and $fake.Sets[0] -ceq '' -and $fake.State.HasText -and $fake.State.Text -ceq '') "result '$($r -join '|')', sets $($fake.Sets.Count)"

$fake = New-FakeClipboard (New-ClipboardState -Formats $textFormats -HasText $true -Text 'hello')
$plan = Get-ClipboardGuardPlan $fake.State
$fake.SetThrows = New-Object System.ArgumentNullException('Value', 'Value cannot be null.')
$threw = $false
try { $r = Invoke-FakeRestore $plan $fake } catch { $threw = $true }
Check 'a set that throws ArgumentNullException (the T-197 failure) is a problem string, not a throw' (-not $threw -and $r.Count -eq 1 -and $r[0] -like 'restoring the clipboard failed: putting back*threw*Value cannot be null*') "threw $threw, result '$($r -join '|')'"

$fake = New-FakeClipboard (New-ClipboardState -Formats $textFormats -HasText $true -Text 'Hello')
$plan = Get-ClipboardGuardPlan $fake.State
$fake.ReadOverride = New-ClipboardState -Formats $textFormats -HasText $true -Text 'hello'
$r = Invoke-FakeRestore $plan $fake
Check 'text that reads back different (case only) is reported' ($r.Count -eq 1 -and $r[0] -like '*read back*') "result '$($r -join '|')'"

$fake = New-FakeClipboard (New-ClipboardState -Formats $textFormats -HasText $true -Text 'hello')
$plan = Get-ClipboardGuardPlan $fake.State
$fake.ReadOverride = New-ClipboardState
$r = Invoke-FakeRestore $plan $fake
Check 'text that reads back as nothing is reported' ($r.Count -eq 1 -and $r[0] -like '*no text reads back*') "result '$($r -join '|')'"

$fake = New-FakeClipboard (New-ClipboardState -Formats $textFormats -HasText $true -Text 'hello')
$plan = Get-ClipboardGuardPlan $fake.State
$fake.ReadThrows = 'clipboard busy'
$threw = $false
try { $r = Invoke-FakeRestore $plan $fake } catch { $threw = $true }
Check 'a read-back that throws is a problem string, not a throw' (-not $threw -and $r.Count -eq 1 -and $r[0] -like '*could not be checked*clipboard busy*') "threw $threw, result '$($r -join '|')'"

$fake = New-FakeClipboard (New-ClipboardState)
$fake.ReadOverride = New-ClipboardState -Unreadable 'this thread is MTA'
$r = Invoke-FakeRestore (Get-ClipboardGuardPlan (New-ClipboardState)) $fake
Check 'a read-back that is unreadable is reported' ($r.Count -eq 1 -and $r[0] -like '*could not be checked*MTA*') "result '$($r -join '|')'"

$threw = $false
try { $r = Invoke-FakeRestore ([pscustomobject]@{ Action = 'Run'; Restore = 'Bogus'; Text = $null; Note = '' }) (New-FakeClipboard (New-ClipboardState)) } catch { $threw = $true }
Check 'a plan with an unknown restore is a problem string, not a throw' (-not $threw -and $r.Count -eq 1 -and $r[0] -like '*no restore it knows*') "threw $threw, result '$($r -join '|')'"

Write-Host 'Isolation'
Check 'the real clipboard was never touched through Get-Clipboard, Set-Clipboard or Get-ClipboardState' ($script:realClipboardTouched.Count -eq 0) ($script:realClipboardTouched -join ', ')
Check 'System.Windows.Forms was never loaded by this test' ($formsLoadedAtStart -or -not (Get-FormsLoaded)) "System.Windows.Forms is loaded; loaded by: $script:formsLoadedBy"
if ($formsLoadedAtStart) { Write-Host '        (it was already loaded when the test started, so this check proves nothing)' }

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host "PASS: $($script:passed) clipboard-guard checks"
    exit 0
}
foreach ($f in $script:failures) { Write-Host "FAIL: $f" }
exit 1
