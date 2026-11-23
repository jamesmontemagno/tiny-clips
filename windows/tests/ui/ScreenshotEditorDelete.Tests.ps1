<#
.SYNOPSIS
    UI automation regression test for the screenshot editor's delete confirmation preference.

.DESCRIPTION
    Drives the already-running Tiny Clips app by PID. The Settings window must be open, and
    Screenshot shortcuts must use the app default. The test restores Confirm delete afterward.

.EXAMPLE
    .\ScreenshotEditorDelete.Tests.ps1 -AppPid 12345
#>
param([Parameter(Mandatory)][int]$AppPid)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Invoke-UI {
    & winapp ui @args
    if ($LASTEXITCODE -ne 0) { throw "winapp ui failed: $args" }
}

$settings = @(Invoke-UI list-windows -a $AppPid --json | ConvertFrom-Json |
    Where-Object { $_.title -eq 'Tiny Clips Settings' } | Select-Object -First 1)
if (-not $settings) {
    throw 'Open Tiny Clips Settings before running this test.'
}
$settingsTarget = @('-w', "$($settings[0].hwnd)")
Invoke-UI invoke "ClipsLibraryNavigationItem" @settingsTarget | Out-Null
Invoke-UI wait-for "LibraryConfirmDeleteToggle" @settingsTarget -t 5000 | Out-Null
$settingsRoot = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settings[0].hwnd)
$toggleCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    "LibraryConfirmDeleteToggle")

function Get-ConfirmDeleteToggle {
    $toggle = $settingsRoot.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $toggleCondition)
    if ($null -eq $toggle) { throw 'The Confirm delete setting was not found.' }
    return $toggle
}

function Get-ConfirmDelete {
    return (Get-ConfirmDeleteToggle).GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq
        [System.Windows.Automation.ToggleState]::On
}

function Set-ConfirmDelete([bool]$enabled) {
    if ((Get-ConfirmDelete) -eq $enabled) { return }
    (Get-ConfirmDeleteToggle).GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $deadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 50
    } while ((Get-ConfirmDelete) -ne $enabled -and (Get-Date) -lt $deadline)
    if ((Get-ConfirmDelete) -ne $enabled) { throw 'The Confirm delete setting did not update.' }
}

function Open-ScreenshotEditor {
    Invoke-UI send-keys "ctrl+shift+5" -a $AppPid --via send-input | Out-Null
    Invoke-UI wait-for "CaptureScreenButton" -a $AppPid -t 5000 | Out-Null
    Invoke-UI invoke "CaptureScreenButton" -a $AppPid | Out-Null
    Invoke-UI wait-for "ScreenshotEditorTitleBar" -a $AppPid -t 10000 | Out-Null

    $editor = @(Invoke-UI list-windows -a $AppPid --json | ConvertFrom-Json |
        Where-Object { $_.title -match 'Edit Screenshot' } | Select-Object -Last 1)
    if (-not $editor) { throw 'Screenshot editor window was not found.' }

    $target = @('-w', "$($editor[0].hwnd)")
    Invoke-UI invoke "EditorToolText" @target | Out-Null
    Invoke-UI click "Screenshot annotation canvas" @target | Out-Null
    Invoke-UI wait-for "TextDialogEntry" -a $AppPid -t 5000 | Out-Null
    Invoke-UI set-value "TextDialogEntry" "delete screenshot regression" -a $AppPid | Out-Null
    Invoke-UI invoke "OK" -a $AppPid | Out-Null
    Invoke-UI wait-for "TextDialogEntry" -a $AppPid --gone -t 5000 | Out-Null
    return $target
}

$originalConfirmDelete = Get-ConfirmDelete
try {
    Set-ConfirmDelete $true
    $target = Open-ScreenshotEditor

    Invoke-UI invoke "EditorDeleteScreenshotButton" @target | Out-Null
    Invoke-UI wait-for "EditorDeleteScreenshotDialog" -a $AppPid -t 5000 | Out-Null
    Invoke-UI invoke "Cancel" -a $AppPid | Out-Null
    Invoke-UI wait-for "EditorDeleteScreenshotDialog" -a $AppPid --gone -t 5000 | Out-Null
    Invoke-UI wait-for "ScreenshotEditorTitleBar" @target -t 3000 | Out-Null

    Invoke-UI invoke "EditorDeleteScreenshotButton" @target | Out-Null
    Invoke-UI wait-for "EditorDeleteScreenshotDialog" -a $AppPid -t 5000 | Out-Null
    Invoke-UI invoke "Delete screenshot" -a $AppPid | Out-Null
    Invoke-UI wait-for "ScreenshotEditorTitleBar" @target --gone -t 5000 | Out-Null

    Set-ConfirmDelete $false
    $target = Open-ScreenshotEditor
    Invoke-UI invoke "EditorDeleteScreenshotButton" @target | Out-Null
    Invoke-UI wait-for "ScreenshotEditorTitleBar" @target --gone -t 5000 | Out-Null
    Invoke-UI wait-for "EditorDeleteScreenshotDialog" -a $AppPid --gone -t 1000 | Out-Null
}
finally {
    Set-ConfirmDelete $originalConfirmDelete
}
