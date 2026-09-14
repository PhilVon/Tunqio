# Guarding the user's clipboard around a harness check that has to write to it (T-201). Dot-source it:
#
#   . (Join-Path $PSScriptRoot 'clipboard-guard.ps1')
#   $plan = Get-ClipboardGuardPlan (Get-ClipboardState)
#   if ($plan.Action -eq 'Skip') { ...record $plan.Note as a skip, touch nothing... }
#   else { try { ...the check... } finally { $problem = Invoke-ClipboardRestore $plan {clear} {param($t) set} {read} } }
#
# The clipboard is the person's, not the test's, and a scratch profile does not isolate it. The old check saved it
# with Get-Clipboard -Raw and put it back with Set-Clipboard, which (a) throws ArgumentNullException in Windows
# PowerShell 5.1 when the saved value is empty, leaving the test's text behind, and (b) can only ever save text, so
# an image or files on the clipboard were destroyed even when the restore "worked". So:
#
#   nothing on the clipboard   -> run the check, then clear it
#   text only                  -> run the check, then put back exactly that text (an empty-but-present string too)
#   anything else, or unknown  -> skip the check and leave the clipboard alone
#
# Get-ClipboardState is the only function here that touches the real clipboard, and it only reads. The decision
# (Get-ClipboardGuardPlan) is pure, and the restore (Invoke-ClipboardRestore) is handed its clipboard operations,
# so tools/test-clipboard-guard.ps1 exercises every branch with fakes and never touches the real one.
#
# Helpers return one value and write nothing to the pipeline: in a harness Test-Case, pipeline output is the failure.
# ASCII only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

# Formats that plain text puts on the clipboard, natively or synthesized by Windows. Anything outside this set is
# data a text restore would lose (HTML Format, Rich Text Format, Bitmap, FileDrop, ...).
$script:ClipboardTextFormats = @('UnicodeText', 'Text', 'OEMText', 'Locale', 'System.String')

# A description of what the clipboard holds. Unreadable is a reason string when it could not be read at all.
function New-ClipboardState {
    param(
        [string[]]$Formats = @(),
        [bool]$HasText = $false,
        [bool]$HasImage = $false,
        [bool]$HasFiles = $false,
        # Untyped: a [string] parameter turns $null into '', and '' (text present but empty) is not "no text".
        [AllowNull()]$Text = $null,
        [string]$Unreadable = $null
    )
    return [pscustomobject]@{
        Formats    = @($Formats | Where-Object { $_ })
        HasText    = $HasText
        HasImage   = $HasImage
        HasFiles   = $HasFiles
        Text       = $Text
        Unreadable = $Unreadable
    }
}

# Reads the real clipboard without changing it. Needs System.Windows.Forms loaded (Add-Type -AssemblyName
# System.Windows.Forms) and an STA thread: System.Windows.Forms.Clipboard is OLE and throws ThreadStateException on
# an MTA thread. powershell.exe (Windows PowerShell 5.1) runs STA by default, which is what the harnesses use; on
# any other apartment this reports the clipboard unreadable, which skips the check rather than guess.
function Get-ClipboardState {
    $apartment = [System.Threading.Thread]::CurrentThread.GetApartmentState()
    if ($apartment -ne [System.Threading.ApartmentState]::STA) {
        return New-ClipboardState -Unreadable "this thread is $apartment, and the clipboard can only be read on an STA thread (run under powershell.exe)"
    }
    # Resolved by name when called, not as a [System.Windows.Forms.Clipboard] literal: Windows PowerShell 5.1 loads
    # the assembly for a literal while dot-sourcing this file, and test-clipboard-guard.ps1 proves it never reached
    # the clipboard by checking the assembly was never loaded.
    $clipboard = 'System.Windows.Forms.Clipboard' -as [type]
    if (-not $clipboard) {
        return New-ClipboardState -Unreadable 'System.Windows.Forms is not loaded (Add-Type -AssemblyName System.Windows.Forms)'
    }
    try {
        $data = $clipboard::GetDataObject()
        $formats = @()
        if ($data) { $formats = @($data.GetFormats($false)) }
        $hasText = [bool]$clipboard::ContainsText()
        $text = $null
        # ContainsText is true for a text format that is present but empty, and GetText then returns ''.
        if ($hasText) { $text = $clipboard::GetText() }
        return New-ClipboardState -Formats $formats -HasText $hasText -Text $text `
            -HasImage ([bool]$clipboard::ContainsImage()) `
            -HasFiles ([bool]$clipboard::ContainsFileDropList())
    }
    catch {
        return New-ClipboardState -Unreadable "reading it threw: $($_.Exception.Message)"
    }
}

# PURE. What to do about a check that writes to the clipboard, given what the clipboard holds:
#   Action  'Run' or 'Skip'
#   Restore 'Clear', 'Text' or 'None' (Skip always has None: the clipboard is not touched)
#   Text    the exact text to put back, when Restore is Text
#   Note    why, for the log; for a Skip it is the skip note
function Get-ClipboardGuardPlan {
    param([AllowNull()]$State)

    $skip = {
        param([string]$why)
        [pscustomobject]@{ Action = 'Skip'; Restore = 'None'; Text = $null; Note = $why }
    }

    if ($null -eq $State) {
        return & $skip 'skipped, the clipboard could not be read, so the harness could not restore it; the clipboard was left untouched'
    }
    if ($State.Unreadable) {
        return & $skip "skipped, the clipboard could not be read ($($State.Unreadable)), so the harness could not restore it; the clipboard was left untouched"
    }

    $formats = @($State.Formats | Where-Object { $_ })
    $nonText = @($formats | Where-Object { $script:ClipboardTextFormats -notcontains $_ })
    if ($State.HasImage -or $State.HasFiles -or $nonText.Count -gt 0) {
        $what = @()
        if ($State.HasImage) { $what += 'an image' }
        if ($State.HasFiles) { $what += 'files' }
        if ($nonText.Count -gt 0) { $what += "formats $($nonText -join ', ')" }
        return & $skip "skipped, the clipboard held non-text content the harness cannot restore ($($what -join '; ')); the clipboard was left untouched"
    }

    if ($State.HasText) {
        if ($null -eq $State.Text) {
            return & $skip 'skipped, the clipboard reported text but none could be read, so the harness could not restore it; the clipboard was left untouched'
        }
        return [pscustomobject]@{ Action = 'Run'; Restore = 'Text'; Text = [string]$State.Text; Note = "the clipboard held $($State.Text.Length) character(s) of text, restored afterwards" }
    }

    if ($formats.Count -eq 0) {
        return [pscustomobject]@{ Action = 'Run'; Restore = 'Clear'; Text = $null; Note = 'the clipboard was empty, cleared afterwards' }
    }

    return & $skip "skipped, the clipboard held formats $($formats -join ', ') but no readable text, which the harness cannot restore; the clipboard was left untouched"
}

# Puts the clipboard back as $Plan says and reads it back to check. Never throws: returns $null when the clipboard
# is back as it was, or a problem string saying what went wrong, for the check to report as a failure.
#   $Clear    { }                 empties the clipboard
#   $SetText  { param($text) }    puts exactly $text on the clipboard, '' included
#   $Read     { }                 returns a state as New-ClipboardState describes it
function Invoke-ClipboardRestore {
    param(
        [Parameter(Mandatory = $true)]$Plan,
        [Parameter(Mandatory = $true)][scriptblock]$Clear,
        [Parameter(Mandatory = $true)][scriptblock]$SetText,
        [Parameter(Mandatory = $true)][scriptblock]$Read
    )

    try {
        if ($Plan.Action -ne 'Run') { return $null }

        switch ($Plan.Restore) {
            'Clear' {
                try { & $Clear | Out-Null }
                catch { return "restoring the clipboard failed: clearing it threw: $($_.Exception.Message)" }
            }
            'Text' {
                try { & $SetText ([string]$Plan.Text) | Out-Null }
                catch { return "restoring the clipboard failed: putting back its $($Plan.Text.Length) character(s) of text threw: $($_.Exception.Message)" }
            }
            default { return "restoring the clipboard failed: the plan names no restore it knows ('$($Plan.Restore)')" }
        }

        try { $after = & $Read }
        catch { return "restoring the clipboard could not be checked: reading it back threw: $($_.Exception.Message)" }
        if ($null -eq $after -or $after.Unreadable) {
            return "restoring the clipboard could not be checked: reading it back failed$(if ($after) { " ($($after.Unreadable))" })"
        }

        if ($Plan.Restore -eq 'Clear') {
            $left = @($after.Formats | Where-Object { $_ })
            if ($left.Count -gt 0 -or $after.HasText) {
                return "restoring the clipboard failed: it was empty before the check, and after clearing it still holds $(if ($left.Count) { $left -join ', ' } else { 'text' })"
            }
        }
        else {
            if (-not $after.HasText -or $null -eq $after.Text) {
                return "restoring the clipboard failed: its $($Plan.Text.Length) character(s) of text were put back but no text reads back"
            }
            if (-not ([string]$after.Text).Equals([string]$Plan.Text, [System.StringComparison]::Ordinal)) {
                return "restoring the clipboard failed: $($Plan.Text.Length) character(s) of text were put back but $(([string]$after.Text).Length) different character(s) read back"
            }
        }
        return $null
    }
    catch {
        return "restoring the clipboard failed: $($_.Exception.Message)"
    }
}
