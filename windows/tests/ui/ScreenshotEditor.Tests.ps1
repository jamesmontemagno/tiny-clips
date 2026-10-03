<#
.SYNOPSIS
    UI automation regression tests for output resolution and simultaneous screenshot editors.

.DESCRIPTION
    Drives the already-running Tiny Clips app by PID. It opens a screenshot editor, adds an
    unsaved text annotation, captures a second screenshot, and verifies the first editor remains
    open without a discard prompt.
    Run with no screenshot editors open. Also checks live output-resolution metadata on initial
    load, scale/focus return, and padding changes. Reset is a consistency smoke check because it
    retains scale/padding; manually check crop -> reset for changing source dimensions.
    Also smoke-checks immediate close after capture and Reset. These timing-sensitive UI checks
    cannot prove late bitmap disposal; closed UIA elements cannot be queried reliably.

    Deterministic Windows debugger regression matrix (run each case with a fresh editor):
    - File-backed open / Reset: pause before GetSoftwareBitmapAsync completes, close the editor
      while the operation is pending, then resume its UI-thread continuation.
    - Captured-frame open / Reset: pause before the Task.Run copy completes, close, then resume.
    - Both paths: pause the SoftwareBitmapSource.SetBitmapAsync completion, close, then resume.
    For each case, after the load task finishes verify controller Bitmap and PreviewSource remain
    null, no ImageChanged or AnnotationsStructureChanged is raised after closure, and the late
    SoftwareBitmap and staged CanvasBitmap (if created) are disposed exactly once. No load-failure
    notification or second Close should occur. Check ImageSizeText.Text is empty and the scale
    button's AutomationProperties.Name remains "Output resolution", including after completion.
    With a redaction preview pending, close before its queued invalidation runs; verify its
    temporary SoftwareBitmap is disposed and AnnotationVisualInvalidated is not raised.
    Controls: repeat each load / Reset without closing; dimensions and preview must populate,
    annotations must reset, and output scale/padding must remain unchanged.

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

function Invoke-UI {
    $output = & winapp ui @args
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $args failed (exit $LASTEXITCODE)." }
    $output
}

function Assert-OutputResolution {
    param([string]$ExpectedText)

    if ($ExpectedText) {
        Invoke-UI wait-for "EditorImageSizeText" @firstTarget -p Name --value $ExpectedText -t 5000 | Out-Null
    }
    else {
        Invoke-UI wait-for "EditorImageSizeText" @firstTarget -p Name --value " px" --contains -t 10000 | Out-Null
    }
    $labels = @((Invoke-UI search "EditorImageSizeText" @firstTarget --json | ConvertFrom-Json).matches |
        Where-Object { $_.automationId -eq 'EditorImageSizeText' })
    if ($labels.Count -ne 1 -or $labels[0].name -notmatch '^([1-9]\d*) × ([1-9]\d*) px$') {
        throw "Expected one visible output-dimension label, found: $($labels.name)"
    }
    $expectedName = "Output resolution, $($Matches[1]) by $($Matches[2]) pixels"
    Invoke-UI wait-for "EditorOutputScaleButton" @firstTarget -p Name --value $expectedName -t 5000 | Out-Null
    $labels[0].name
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

Test-UI "Output-resolution name matches visible dimensions with flyout closed" {
    Invoke-UI wait-for "EditorOutputScaleSlider" -a $AppPid --gone -t 3000
    $script:initialOutputResolution = Assert-OutputResolution
}
Test-UI "Scale updates resolution and dismissing flyout restores button focus" {
    # Derive expectations from the live label, not a fixed monitor resolution or DPI.
    $dimensions = $initialOutputResolution -split ' × | px'
    $halfWidth = [Math]::Max(1, [Math]::Round([int]$dimensions[0] / 2.0))
    $halfHeight = [Math]::Max(1, [Math]::Round([int]$dimensions[1] / 2.0))
    $script:scaledOutputResolution = "$halfWidth × $halfHeight px"
    Invoke-UI focus "EditorOutputScaleButton" @firstTarget
    Invoke-UI send-keys "enter" @firstTarget --via send-input
    Invoke-UI wait-for "EditorOutputScaleSlider" -a $AppPid -t 3000
    Invoke-UI set-value "EditorOutputScaleSlider" 50 -a $AppPid
    Invoke-UI focus "EditorOutputScaleSlider" -a $AppPid
    Invoke-UI send-keys "escape" -a $AppPid --via send-input
    Invoke-UI wait-for "EditorOutputScaleSlider" -a $AppPid --gone -t 3000
    Invoke-UI wait-for "EditorOutputScaleButton" @firstTarget -p HasKeyboardFocus --value True -t 3000
    Assert-OutputResolution $scaledOutputResolution | Out-Null
}
Test-UI "Padding updates the output-resolution name" {
    # Padding adds 64 source pixels to each dimension, hence 32 output pixels at 50%.
    $dimensions = $scaledOutputResolution -split ' × | px'
    $script:paddedOutputResolution = "$([int]$dimensions[0] + 32) × $([int]$dimensions[1] + 32) px"
    Invoke-UI invoke "EditorBackgroundExpander" @firstTarget
    Invoke-UI wait-for "EditorPaddingSlider" @firstTarget -t 3000
    Invoke-UI set-value "EditorPaddingSlider" 32 @firstTarget
    Assert-OutputResolution $paddedOutputResolution | Out-Null
}
Test-UI "Removing padding restores scaled output-resolution metadata" {
    Invoke-UI set-value "EditorPaddingSlider" 0 @firstTarget
    Assert-OutputResolution $scaledOutputResolution | Out-Null
}

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

Test-UI "Immediate Reset then close does not reopen the clean second editor" {
    $second = @(Get-EditorWindows | Where-Object { $_.hwnd -ne $first.hwnd })
    if ($second.Count -ne 1) { throw "Expected one clean second editor." }
    $secondTarget = @("-w", "$($second[0].hwnd)")
    Invoke-UI invoke "EditorResetButton" @secondTarget
    Invoke-UI invoke "EditorCloseButton" @secondTarget
    Invoke-UI wait-for "ScreenshotEditorTitleBar" @secondTarget --gone -t 5000
    Start-Sleep -Milliseconds 1000
    if (@(Get-EditorWindows).Count -ne 1) { throw "Only the first editor should remain." }
    Invoke-UI wait-for "ScreenshotEditorTitleBar" @firstTarget -t 3000
}

# Reset and close each editor so the test leaves the running app usable.
foreach ($editor in @(Get-EditorWindows)) {
    $target = @("-w", "$($editor.hwnd)")
    winapp ui invoke "EditorResetButton" @target 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    if ($editor.hwnd -eq $first.hwnd) {
        Test-UI "Reset preserves output-resolution metadata" {
            Assert-OutputResolution $scaledOutputResolution | Out-Null
        }
    }
    winapp ui invoke "EditorCloseButton" @target 2>$null | Out-Null
}
Test-UI "Screenshot editors close" {
    winapp ui wait-for "ScreenshotEditorTitleBar" -a $AppPid --gone -t 5000
}

Test-UI "Immediate close after captured-frame editor opens leaves no editor" {
    Invoke-UI send-keys "ctrl+shift+5" -a $AppPid --via send-input
    Invoke-UI wait-for "CaptureScreenButton" -a $AppPid -t 5000
    Invoke-UI invoke "CaptureScreenButton" -a $AppPid
    $capturedEditor = @(Wait-EditorCount -Expected 1)
    $captureTarget = @("-w", "$($capturedEditor[0].hwnd)")
    Invoke-UI invoke "EditorCloseButton" @captureTarget
    Invoke-UI wait-for "ScreenshotEditorTitleBar" -a $AppPid --gone -t 5000
    Start-Sleep -Milliseconds 1000
    if (@(Get-EditorWindows).Count -ne 0) { throw "A closed capture editor reappeared." }
    if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw "Tiny Clips exited." }
}

Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } |
    ForEach-Object { Write-Host "  FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red }
$results | ConvertTo-Json | Out-File (Join-Path $ShotDir "test-results.json")
if ($fail -gt 0) { exit 1 } else { exit 0 }
