<#
.SYNOPSIS
  Puts one kind of content after another on the clipboard and runs `Voica.exe --probe-clipboard`
  on each: can it be saved, how long does that take, does it come back byte for byte (spec §5).

.DESCRIPTION
  Clipboard behaviour cannot be unit-tested — the self-test must not touch what the person has
  copied — and it cannot be checked from a sandboxed session either, which is denied the clipboard
  altogether. So this is run by hand, in a normal desktop session.

  ⚠️ It OVERWRITES the clipboard several times. Whatever you had copied is gone afterwards.

  What it cannot stage is the content only another application can put there: copy an Excel range,
  an Outlook attachment or a screenshot yourself and run the probe directly:

      Voica.exe --probe-clipboard

  Everything goes through System.Windows.Forms rather than Set-Clipboard: PowerShell 7's cmdlet
  only takes text (-AsHtml and -Path exist in Windows PowerShell 5.1 alone).

.PARAMETER Exe
  Path to Voica.exe. Defaults to the Release publish output, then the Debug build.
#>
param([string]$Exe)

$ErrorActionPreference = 'Stop'

# The Windows.Forms clipboard is OLE and needs a single-threaded apartment.
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    & (Get-Process -Id $PID).Path -STA -NoProfile -File $PSCommandPath @PSBoundParameters
    return
}

$root = Split-Path $PSScriptRoot -Parent
if (-not $Exe) {
    $Exe = @(
        "$root\src\Voica\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\Voica.exe",
        "$root\src\Voica\bin\Debug\net8.0-windows10.0.17763.0\Voica.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Exe -or -not (Test-Path $Exe)) { throw "Voica.exe not found — build it first or pass -Exe." }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$Clip = [System.Windows.Forms.Clipboard]
$out = Join-Path $env:TEMP 'voica-clipboard-probe.txt'

# This script is the clipboard's OWNER for everything it stages, exactly like the application you
# copied from. A live application keeps answering messages, so while the probe runs we keep pumping
# them. -Frozen does the opposite on purpose: it blocks, the way a hung application would.
function Probe([string]$label, [switch]$Frozen) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if ($Frozen) {
        # ⚠️ `Start-Process -Wait` is what really stops this window answering. WaitForExit() and
        # Thread.Sleep do not: on a single-threaded apartment a managed wait still serves sent
        # messages, so the "frozen" owner went on rendering and the step proved nothing.
        $p = Start-Process -FilePath $Exe -ArgumentList '--probe-clipboard' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
    } else {
        $p = Start-Process -FilePath $Exe -ArgumentList '--probe-clipboard' -PassThru -NoNewWindow -RedirectStandardOutput $out
        $null = $p.Handle   # without this PowerShell loses the exit code of a process it did not wait on
        while (-not $p.HasExited) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
    }
    Write-Host ''
    Write-Host "== $label  (exit $($p.ExitCode), $($clock.ElapsedMilliseconds) ms)"
    Get-Content $out -Encoding utf8 | ForEach-Object { Write-Host $_ }
}

# One step failing must not hide the rest: the point of the run is the whole picture.
function Step([string]$label, [scriptblock]$stage, [scriptblock]$after, [switch]$Frozen) {
    try {
        & $stage
        Probe $label -Frozen:$Frozen
        if ($after) { & $after }
    } catch {
        Write-Host ''
        Write-Host "== $label  — COULD NOT STAGE: $($_.Exception.Message)"
    }
}

Write-Host "Voica: $Exe"
Write-Host "PowerShell $($PSVersionTable.PSVersion), $([Threading.Thread]::CurrentThread.GetApartmentState())"

Step 'plain text' { $Clip::SetText('https://example.com/a-link-I-copied-before-dictating') }

Step 'unicode text, two lines' { $Clip::SetText("строка один`r`nстрока два — «кавычки», emoji 🙂") }

Step 'HTML with a plain-text twin (as copied from a web page)' {
    $d = New-Object System.Windows.Forms.DataObject
    $d.SetData([System.Windows.Forms.DataFormats]::Html, '<b>bold</b> and a <a href="https://example.com">link</a>')
    $d.SetData([System.Windows.Forms.DataFormats]::UnicodeText, 'bold and a link')
    $Clip::SetDataObject($d, $true)
} {
    Write-Host "   text twin after the round trip: $($Clip::GetText())"
}

Step 'rich text, rendered on demand by a live application' {
    $rtf = New-Object System.Windows.Forms.RichTextBox
    $rtf.Text = 'rich text'; $rtf.SelectAll()
    $rtf.SelectionFont = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Bold)
    $rtf.Copy()   # not flushed: every format is produced when asked for
}

# The dangerous case. The same on-demand data, but its owner has stopped answering: reading it
# would wait 15 s PER FORMAT. Expected: refused within a fraction of a second, never a hang.
Step 'rich text whose application is FROZEN (expected: cannot be kept, and fast)' {
    $rtf = New-Object System.Windows.Forms.RichTextBox
    $rtf.Text = 'rich text'; $rtf.SelectAll()
    $rtf.Copy()
} -Frozen

Step 'a file, as copied in Explorer' {
    $list = New-Object System.Collections.Specialized.StringCollection
    [void]$list.Add($Exe)
    $Clip::SetFileDropList($list)
} {
    Write-Host "   file list after the round trip: $(@($Clip::GetFileDropList()) -join ', ')"
}

Step 'image 1920x1080 (a screenshot)' {
    $bmp = New-Object System.Drawing.Bitmap 1920, 1080
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::SteelBlue); $g.Dispose()
    $Clip::SetImage($bmp)
} {
    $back = $Clip::GetImage()
    Write-Host "   image after the round trip: $(if ($back) { "$($back.Width)x$($back.Height)" } else { 'MISSING' })"
}

Step 'image 7680x4320 (over the 64 MB limit — expected: cannot be kept)' {
    $Clip::SetImage((New-Object System.Drawing.Bitmap 7680, 4320))
}

Step 'empty clipboard' { $Clip::Clear() }

$Clip::SetText('try-clipboard.ps1 finished')
Write-Host ''
Write-Host 'done'
