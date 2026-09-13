<#
.SYNOPSIS
    UI automation regression test for multiple simultaneous screenshot editors.

.DESCRIPTION
    Drives the already-running Tiny Clips app by PID. It opens a screenshot editor, adds an
    unsaved text annotation, captures a second screenshot, and verifies the first editor remains
    open without a discard prompt.

.EXAMPLE
    .\ScreenshotEditor.Tests.ps1 -AppPid 12345 -ShotDir .\screenshots
#>
param([Parameter(Mandatory)][int]$AppPid, [string]$ShotDir = ".")
$ErrorActionPreference = 'Continue'
$pass = 0; $fail = 0; $results = @()
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += @{ name = $Name; status = "PASS" }
        }
        else {
            $script:fail++
            $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" }
        }
    }
    catch {
        $script:fail++
        $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
    }
}

function Get-EditorWindows {
    @(winapp ui list-windows -a $AppPid --json 2>$null |
        ConvertFrom-Json |
        Where-Object { $_.title -match 'Edit Screenshot' })
}

function Wait-EditorCount {
    param([Parameter(Mandatory)][int]$Expected, [int]$TimeoutMs = 10000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    do {
        $editors = Get-EditorWindows
        if ($editors.Count -ge $Expected) {
            return $editors
        }

        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    throw "Expected at least $Expected screenshot editor windows, found $($editors.Count)."
}

function Shot($name) {
    winapp ui screenshot -a $AppPid -o (Join-Path $ShotDir "$name.png") 2>$null | Out-Null
}

Test-UI "Start first screenshot capture" {
    winapp ui send-keys "ctrl+shift+5" -a $AppPid --via send-input
}
Test-UI "Capture first screen" {
    winapp ui wait-for "CaptureScreenButton" -a $AppPid -t 5000
    if ($LASTEXITCODE -ne 0) { throw "Capture picker did not open." }
    winapp ui invoke "CaptureScreenButton" -a $AppPid
}
Test-UI "First editor opens" {
    winapp ui wait-for "ScreenshotEditorTitleBar" -a $AppPid -t 10000
}

$first = Get-EditorWindows | Select-Object -First 1
if (-not $first) {
    Write-Host "First screenshot editor window was not found." -ForegroundColor Red
    exit 1
}
$firstTarget = @("-w", "$($first.hwnd)")

Test-UI "Select text tool in first editor" {
    winapp ui invoke "EditorToolText" @firstTarget
}
Test-UI "Open text annotation dialog" {
    winapp ui click "Screenshot annotation canvas" @firstTarget
    winapp ui wait-for "TextDialogEntry" -a $AppPid -t 5000
}
Test-UI "Add unsaved annotation to first editor" {
    winapp ui set-value "TextDialogEntry" "multi-editor regression" -a $AppPid
    winapp ui invoke "OK" -a $AppPid
    winapp ui wait-for "TextDialogEntry" -a $AppPid --gone -t 5000
}

Test-UI "Start second screenshot capture" {
    winapp ui send-keys "ctrl+shift+5" -a $AppPid --via send-input
}
Test-UI "Capture second screen" {
    winapp ui wait-for "CaptureScreenButton" -a $AppPid -t 5000
    if ($LASTEXITCODE -ne 0) { throw "Capture picker did not reopen." }
    winapp ui invoke "CaptureScreenButton" -a $AppPid
}
Test-UI "Second editor opens" {
    [void](Wait-EditorCount -Expected 2)
}

$editors = Get-EditorWindows
if ($editors.Count -eq 2) {
    $pass++
    $results += @{ name = "Both screenshot editors remain open"; status = "PASS" }
}
else {
    $fail++
    $results += @{
        name = "Both screenshot editors remain open"
        status = "FAIL"
        detail = "Expected 2 editor windows, found $($editors.Count)"
    }
}

Test-UI "First editor remains available" {
    winapp ui wait-for "ScreenshotEditorTitleBar" @firstTarget -t 3000
}
Test-UI "No discard prompt from second capture" {
    winapp ui wait-for "Discard changes?" -a $AppPid --gone -t 1000
}
Shot "multiple-editors"

# Reset and close each editor so the test leaves the running app usable.
foreach ($editor in @(Get-EditorWindows)) {
    $target = @("-w", "$($editor.hwnd)")
    winapp ui invoke "EditorResetButton" @target 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui invoke "EditorCloseButton" @target 2>$null | Out-Null
}

Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } |
    ForEach-Object { Write-Host "  FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red }
$results | ConvertTo-Json | Out-File (Join-Path $ShotDir "test-results.json")
if ($fail -gt 0) { exit 1 } else { exit 0 }
