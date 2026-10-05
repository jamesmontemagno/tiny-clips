<#
.SYNOPSIS
    Regression test for the screenshot File Explorer reveal preference (#397).

.DESCRIPTION
    Run against the updated app with Settings open and no screenshot editors open. Ctrl+Shift+5
    must be assigned to this app, with no other instance competing for the shortcut.
    CaptureDirectory must be the app's effective screenshot save folder.

    Captures the main display with reveal off, on, then off again. Checks the saved PNG,
    a new clipboard image, and Explorer selection/navigation (not Explorer process counts).
    Temporarily configures screenshot settings and a unique file-name template, restores them
    in finally, and removes only test captures and newly opened Explorer windows still selecting
    a test capture. Existing Explorer windows are never closed. The clipboard is overwritten.

.EXAMPLE
    .\ScreenshotReveal.Tests.ps1 -AppPid 12345 -CaptureDirectory "$env:USERPROFILE\Pictures\TinyClips"
#>
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$CaptureDirectory,
    [ValidateRange(3, 30)][int]$ObservationSeconds = 5
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace ScreenshotRevealTest -Name Native -MemberDefinition @'
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();
'@

if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this clipboard test in an STA PowerShell session (powershell.exe -STA).'
}
$CaptureDirectory = (Resolve-Path -LiteralPath $CaptureDirectory).Path
if (-not (Test-Path -LiteralPath $CaptureDirectory -PathType Container)) {
    throw 'CaptureDirectory must be an existing screenshot save folder.'
}
$prefix = "TinyClips-reveal-test-$([Guid]::NewGuid().ToString('N'))"
$originalSettings = @()
$capturedFiles = @()
$failures = 0
$shell = New-Object -ComObject Shell.Application

function Invoke-UI {
    $output = & winapp ui @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $args failed: $output" }
    $output
}

function Get-ExplorerState {
    foreach ($window in @($shell.Windows())) {
        if ($window.FullName -notlike '*\explorer.exe') { continue }
        $document = $window.Document
        [pscustomobject]@{
            Hwnd = [long]$window.HWND
            Folder = $document.Folder.Self.Path
            Selected = @($document.SelectedItems() | ForEach-Object { $_.Path })
            Window = $window
        }
    }
}

function Set-Setting {
    param($Setting, [string]$Value)
    Invoke-UI invoke $Setting.Section @settingsTarget | Out-Null
    $current = (Invoke-UI get-value $Setting.Id @settingsTarget --json | ConvertFrom-Json).text
    if ($current -ne $Value) {
        switch ($Setting.Kind) {
            'Toggle' { Invoke-UI invoke $Setting.Id @settingsTarget | Out-Null }
            'Text' { Invoke-UI set-value $Setting.Id $Value @settingsTarget | Out-Null }
            'Combo' {
                Invoke-UI invoke $Setting.Id @settingsTarget | Out-Null
                $items = @((Invoke-UI search $Value @settingsTarget --type ListItem --json |
                    ConvertFrom-Json).matches | Where-Object { $_.name -eq $Value } |
                    Sort-Object selector -Unique)
                if ($items.Count -ne 1) { throw "Expected one combo-box option named '$Value'." }
                Invoke-UI invoke $items[0].selector @settingsTarget | Out-Null
            }
        }
    }
    Invoke-UI wait-for $Setting.Id @settingsTarget --value $Value -t 3000 | Out-Null
}

function Test-CaptureReveal {
    param([bool]$Reveal)
    $value = if ($Reveal) { 'On' } else { 'Off' }
    Set-Setting $revealSetting $value
    $before = @(Get-ExplorerState)
    $clipboardSequence = [ScreenshotRevealTest.Native]::GetClipboardSequenceNumber()

    Invoke-UI send-keys 'ctrl+shift+5' @settingsTarget --via send-input | Out-Null
    Invoke-UI wait-for 'CaptureScreenButton' -a $AppPid -t 5000 | Out-Null
    Invoke-UI invoke 'CaptureScreenButton' -a $AppPid | Out-Null

    $deadline = (Get-Date).AddSeconds(15)
    $capture = $null
    do {
        $newFiles = @(Get-ChildItem -LiteralPath $CaptureDirectory -Filter "$prefix*.png" -File |
            Where-Object { $_.FullName -notin $script:capturedFiles })
        if ($newFiles.Count -gt 1) { throw 'More than one new test screenshot was saved.' }
        if ($newFiles.Count -eq 1) { $capture = $newFiles[0]; break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if (-not $capture) { throw "No screenshot was saved in $CaptureDirectory." }
    $script:capturedFiles += $capture.FullName

    $deadline = (Get-Date).AddSeconds(5)
    do {
        $copied = [ScreenshotRevealTest.Native]::GetClipboardSequenceNumber() -ne $clipboardSequence
        if ($copied) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if (-not $copied) { throw 'The capture did not update the clipboard.' }
    # Reading immediately after SetContent can lock the clipboard while the app is flushing it.
    Start-Sleep -Milliseconds 500
    if (-not [System.Windows.Forms.Clipboard]::ContainsImage()) {
        throw 'The capture did not copy a new image to the clipboard.'
    }

    $savedImage = $null
    $clipboardImage = $null
    try {
        $savedImage = [System.Drawing.Image]::FromFile($capture.FullName)
        $clipboardImage = [System.Windows.Forms.Clipboard]::GetImage()
        if ($null -eq $clipboardImage -or $clipboardImage.Width -ne $savedImage.Width -or
            $clipboardImage.Height -ne $savedImage.Height) {
            throw 'Clipboard image dimensions do not match the saved screenshot.'
        }
    }
    finally {
        if ($savedImage) { $savedImage.Dispose() }
        if ($clipboardImage) { $clipboardImage.Dispose() }
    }

    $deadline = (Get-Date).AddSeconds($ObservationSeconds)
    $selected = $false
    do {
        foreach ($state in @(Get-ExplorerState)) {
            $selected = $state.Selected -contains $capture.FullName
            $navigated = $state.Folder -eq $CaptureDirectory -and
                @($before | Where-Object { $_.Hwnd -eq $state.Hwnd -and $_.Folder -eq $state.Folder }).Count -eq 0
            if (-not $Reveal -and ($selected -or $navigated)) {
                throw "Reveal is Off, but Explorer opened/navigated to or selected $($capture.Name)."
            }
            if ($Reveal -and $selected) { break }
        }
        if ($Reveal -and $selected) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if ($Reveal -and -not $selected) { throw "Reveal is On, but Explorer did not select $($capture.Name)." }
    Invoke-UI wait-for 'ScreenshotEditorTitleBar' -a $AppPid --gone -t 1000 | Out-Null
    Write-Host "PASS: Reveal $value; screenshot saved and copied."
}

$settingsWindows = @(Invoke-UI list-windows -a $AppPid --json | ConvertFrom-Json |
    Where-Object { $_.title -eq 'Tiny Clips Settings' })
if ($settingsWindows.Count -ne 1) { throw 'Open Settings in the target Tiny Clips app first.' }
$settingsTarget = @('-w', "$($settingsWindows[0].hwnd)")
Invoke-UI wait-for 'ScreenshotEditorTitleBar' -a $AppPid --gone -t 1000 | Out-Null
$originalExplorerWindows = @(Get-ExplorerState | ForEach-Object { $_.Hwnd })
$revealSetting = @{ Section = 'GeneralNavigationItem'; Id = 'ShowInExplorerToggle'; Kind = 'Toggle' }
$configuration = @(
    @{ Section = 'GeneralNavigationItem'; Id = 'ShowInExplorerToggle'; Kind = 'Toggle'; Value = 'Off' }
    @{ Section = 'GeneralNavigationItem'; Id = 'FileNameTemplateTextBox'; Kind = 'Text'; Value = "$prefix {date} {time}" }
    @{ Section = 'GeneralNavigationItem'; Id = 'MultiMonitorCaptureModeComboBox'; Kind = 'Combo'; Value = 'Always use main display' }
    @{ Section = 'ScreenshotNavigationItem'; Id = 'ScreenshotFormatComboBox'; Kind = 'Combo'; Value = 'PNG' }
    @{ Section = 'ScreenshotNavigationItem'; Id = 'ShowScreenshotEditorToggle'; Kind = 'Toggle'; Value = 'Off' }
    @{ Section = 'ScreenshotNavigationItem'; Id = 'CopyScreenshotToggle'; Kind = 'Toggle'; Value = 'On' }
    @{ Section = 'ScreenshotNavigationItem'; Id = 'ShowScreenshotCapturePickerToggle'; Kind = 'Toggle'; Value = 'On' }
    @{ Section = 'ScreenshotNavigationItem'; Id = 'ShowScreenshotCapturePickerAfterCaptureToggle'; Kind = 'Toggle'; Value = 'Off' }
    @{ Section = 'ScreenshotNavigationItem'; Id = 'ScreenshotCountdownToggle'; Kind = 'Toggle'; Value = 'Off' }
)

try {
    foreach ($setting in $configuration) {
        Invoke-UI invoke $setting.Section @settingsTarget | Out-Null
        $setting.Original = (Invoke-UI get-value $setting.Id @settingsTarget --json | ConvertFrom-Json).text
        if ($null -eq $setting.Original) { throw "Could not read the original value of $($setting.Id)." }
        $originalSettings += $setting
        Set-Setting $setting $setting.Value
    }

    foreach ($reveal in @($false, $true, $false)) {
        try { Test-CaptureReveal $reveal }
        catch {
            $failures++
            Write-Host "FAIL: $_" -ForegroundColor Red
        }
    }
}
catch {
    $failures++
    Write-Host "FAIL: Test setup: $_" -ForegroundColor Red
}
finally {
    for ($i = $originalSettings.Count - 1; $i -ge 0; $i--) {
        try { Set-Setting $originalSettings[$i] $originalSettings[$i].Original }
        catch {
            $failures++
            Write-Host "FAIL: Restore $($originalSettings[$i].Id): $_" -ForegroundColor Red
        }
    }
    try {
        foreach ($state in @(Get-ExplorerState)) {
            if ($state.Hwnd -notin $originalExplorerWindows -and
                @($state.Selected | Where-Object { $_ -in $capturedFiles }).Count -gt 0) {
                $state.Window.Quit()
            }
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $CaptureDirectory -Filter "$prefix*.png" -File)) {
            Remove-Item -LiteralPath $file.FullName
        }
    }
    catch {
        $failures++
        Write-Host "FAIL: Test capture cleanup: $_" -ForegroundColor Red
    }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}

if ($failures -gt 0) { exit 1 }
exit 0
