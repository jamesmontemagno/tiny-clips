<#
.SYNOPSIS
    Copies and exports the selected video frame and compares the resulting pixels.
.DESCRIPTION
    Run on Windows with an isolated test app/profile and a synthetic MP4 already open
    and paused in the video trimmer. SourcePath must be that video's path. ExportDirectory
    must be the configured screenshot save directory. This changes the test clipboard
    and exports one PNG, which is retained for inspection. No source is deleted.
    Run at multiple timestamps with a numbered/color-coded fixture; separately check
    Narrator, high contrast, scaling, close/delete races and clipboard contention.
.EXAMPLE
    .\TrimmerFrameCopy.Tests.ps1 -AppPid 12345 -SourcePath C:\Tests\frames.mp4 -ExportDirectory C:\Tests\Captures -ExpectedWidth 640 -ExpectedHeight 360
#>
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$ExportDirectory,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$ExpectedWidth,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$ExpectedHeight
)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this clipboard check with powershell.exe -STA.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Invoke-UI {
    & winapp ui @args
    if ($LASTEXITCODE -ne 0) { throw "winapp ui failed: $args" }
}

$SourcePath = (Resolve-Path -LiteralPath $SourcePath).Path
$ExportDirectory = (Resolve-Path -LiteralPath $ExportDirectory).Path
$sourceHash = (Get-FileHash -LiteralPath $SourcePath).Hash
$before = @(Get-ChildItem -LiteralPath $ExportDirectory -File | Select-Object -ExpandProperty FullName)
$windows = @(Invoke-UI list-windows -a $AppPid --json | ConvertFrom-Json |
    Where-Object { $_.title -match 'Trim Video' })
if ($windows.Count -ne 1) { throw 'Open exactly one paused video trimmer with the test source.' }
$target = @('-w', "$($windows[0].hwnd)")
$root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$windows[0].hwnd)

function Find-Control([string]$id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "Missing control: $id" }
    return $element
}

function Read-Controls {
    # Covers the trim bar's accessible values, speed, audio choice and playback state.
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    @(
        foreach ($element in $elements) {
            $range = $null
            $toggle = $null
            $selection = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$range)) {
                "$($element.Current.AutomationId)|$($element.Current.Name)|$($range.Current.Value)"
            }
            if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
                "$($element.Current.AutomationId)|$($element.Current.Name)|$($toggle.Current.ToggleState)"
            }
            if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$selection)) {
                "$($element.Current.AutomationId)|$(@($selection.Current.GetSelection() | ForEach-Object { $_.Current.Name }) -join ',')"
            }
        }
    ) -join "`n"
}

$copy = Find-Control 'VideoCopyFrameButton'
if (-not $copy.Current.IsEnabled -or $copy.Current.IsOffscreen) { throw 'Copy Frame is not ready/visible.' }
if ($copy.Current.Name -ne 'Copy current frame to clipboard') { throw 'Copy Frame has an unexpected accessible name.' }
$play = Find-Control 'VideoPlayToggle'
$playPattern = $play.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
if ($playPattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
    throw 'Pause the video at a known fixture frame before running this check.'
}
$controlsBefore = Read-Controls
[System.Windows.Forms.Clipboard]::Clear()
Invoke-UI focus 'VideoCopyFrameButton' @target | Out-Null
Invoke-UI send-keys 'enter' @target --via send-input | Out-Null
$deadline = (Get-Date).AddSeconds(15)
do {
    $status = (Find-Control 'VideoFrameCopyStatus').Current.Name
    if ($status -eq 'Frame copied to clipboard.') { break }
    if ($status -like 'Could not*') { throw $status }
    Start-Sleep -Milliseconds 100
} while ((Get-Date) -lt $deadline)
if ($status -ne 'Frame copied to clipboard.') { throw "Copy did not complete: $status" }
if (-not [System.Windows.Forms.Clipboard]::ContainsImage()) { throw 'Clipboard has no image.' }
if ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) { throw 'Copy unexpectedly published a file.' }
if ((Read-Controls) -ne $controlsBefore) { throw 'Copy changed trim, speed, audio, or playback state.' }
$afterCopy = @(Get-ChildItem -LiteralPath $ExportDirectory -File | Select-Object -ExpandProperty FullName)
if (@(Compare-Object -ReferenceObject (@('sentinel') + $before) -DifferenceObject (@('sentinel') + $afterCopy)).Count -ne 0) {
    throw 'Copy created or removed a file.'
}
if ((Get-FileHash -LiteralPath $SourcePath).Hash -ne $sourceHash) { throw 'Copy modified the source.' }

$clipboard = [System.Windows.Forms.Clipboard]::GetImage()
$saved = $null
try {
    if ($clipboard.Width -ne $ExpectedWidth -or $clipboard.Height -ne $ExpectedHeight) {
        throw 'Copied dimensions do not match the known source fixture resolution.'
    }
    Invoke-UI invoke 'VideoExportFrameButton' @target | Out-Null
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $newFiles = @(Get-ChildItem -LiteralPath $ExportDirectory -Filter '*.png' -File |
            Where-Object { $_.FullName -notin $before })
        if ($newFiles.Count -eq 1 -and (Find-Control 'VideoExportFrameButton').Current.IsEnabled) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if ($newFiles.Count -ne 1) { throw 'Expected exactly one exported PNG.' }
    $saved = [System.Drawing.Bitmap]::new($newFiles[0].FullName)
    if ($clipboard.Width -ne $saved.Width -or $clipboard.Height -ne $saved.Height) {
        throw 'Copied/exported dimensions differ.'
    }
    for ($y = 0; $y -lt $saved.Height; $y++) {
        for ($x = 0; $x -lt $saved.Width; $x++) {
            if ($clipboard.GetPixel($x, $y).ToArgb() -ne $saved.GetPixel($x, $y).ToArgb()) {
                throw "Copied/exported pixel differs at $x,$y."
            }
        }
    }
    Invoke-UI wait-for 'VideoTrimmerTitleBar' @target -t 3000 | Out-Null
    if ((Get-FileHash -LiteralPath $SourcePath).Hash -ne $sourceHash) { throw 'Export modified the source.' }
    Write-Output "PASS: keyboard Copy Frame preserved editor/source, created no file, and matched PNG pixels: $($newFiles[0].FullName)"
}
finally {
    if ($saved) { $saved.Dispose() }
    if ($clipboard) { $clipboard.Dispose() }
}
