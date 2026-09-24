<#
.SYNOPSIS
    UI automation regression test for deleting an original screenshot from the editor.

.EXAMPLE
    .\ScreenshotEditorDelete.Tests.ps1 -AppPid 12345
#>
param([Parameter(Mandatory)][int]$AppPid)
$ErrorActionPreference = 'Stop'

winapp ui send-keys "ctrl+shift+5" -a $AppPid --via send-input
winapp ui wait-for "CaptureScreenButton" -a $AppPid -t 5000
winapp ui invoke "CaptureScreenButton" -a $AppPid
winapp ui wait-for "ScreenshotEditorTitleBar" -a $AppPid -t 10000

$editor = @(winapp ui list-windows -a $AppPid --json | ConvertFrom-Json |
    Where-Object { $_.title -match 'Edit Screenshot' } | Select-Object -Last 1)
if (-not $editor) {
    throw "Screenshot editor window was not found."
}

$target = @("-w", "$($editor.hwnd)")

winapp ui invoke "EditorToolText" @target
winapp ui click "Screenshot annotation canvas" @target
winapp ui wait-for "TextDialogEntry" -a $AppPid -t 5000
winapp ui set-value "TextDialogEntry" "delete screenshot regression" -a $AppPid
winapp ui invoke "OK" -a $AppPid
winapp ui wait-for "TextDialogEntry" -a $AppPid --gone -t 5000

winapp ui invoke "EditorDeleteScreenshotButton" @target
winapp ui wait-for "EditorDeleteScreenshotDialog" -a $AppPid -t 5000
winapp ui invoke "Cancel" -a $AppPid
winapp ui wait-for "EditorDeleteScreenshotDialog" -a $AppPid --gone -t 5000
winapp ui wait-for "ScreenshotEditorTitleBar" @target -t 3000

winapp ui invoke "EditorDeleteScreenshotButton" @target
winapp ui wait-for "EditorDeleteScreenshotDialog" -a $AppPid -t 5000
winapp ui invoke "Delete screenshot" -a $AppPid
winapp ui wait-for "ScreenshotEditorTitleBar" @target --gone -t 5000
