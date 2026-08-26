<#
.SYNOPSIS
    UI automation smoke tests for the Windows Clips Library window (winapp ui / UI Automation).

.DESCRIPTION
    Drives the already-running Tiny Clips app by PID. The Library window must be open (open it from
    the tray popup: TrayClipsLibraryButton). Tests shell chrome, detail pane editing, favorites,
    sidebar filters, search + empty state, filter flyout, view modes, selection mode + batch bar,
    the delete confirmation, the context menu, and an accessibility-name audit.

.EXAMPLE
    .\ClipsLibrary.Tests.ps1 -AppPid 12345 -ShotDir .\screenshots
#>
param([Parameter(Mandatory)][int]$AppPid, [string]$ShotDir = ".")
$ErrorActionPreference = 'Continue'
$pass = 0; $fail = 0; $results = @()
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) { $script:pass++; $script:results += @{ name = $Name; status = "PASS" } }
        else { $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" } }
    } catch { $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" } }
}
function Shot($name) { winapp ui screenshot @W -o (Join-Path $ShotDir "$name.png") 2>$null | Out-Null }

# Bring the Library window to the foreground: click/send-keys refuse to act on background windows.
Add-Type -Namespace Win32 -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);' -ErrorAction SilentlyContinue
$libraryWindow = winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json | Where-Object title -match 'Library' | Select-Object -First 1
if (-not $libraryWindow) { Write-Host "Library window not found — open it from the tray popup first."; exit 1 }
$hwnd = $libraryWindow.hwnd
[Win32.Native]::ShowWindow([IntPtr]$hwnd, 9) | Out-Null; [Win32.Native]::SetForegroundWindow([IntPtr]$hwnd) | Out-Null; Start-Sleep 1
# Popups (MenuFlyout, AutoSuggest list) live in separate PopupHost windows, so those steps use -a $AppPid; everything else targets the Library HWND.
$W = @('-w', $hwnd)

# ─── Shell ───
Test-UI "Title bar exists" { winapp ui wait-for "ClipsLibraryTitleBar" @W -t 5000 }
Test-UI "Search box exists" { winapp ui wait-for "LibrarySearchBox" @W -t 3000 }
Test-UI "Filter button exists" { winapp ui wait-for "LibraryFilterButton" @W -t 3000 }
Test-UI "Grid visible" { winapp ui wait-for "ClipsGridView" @W -t 3000 }
Test-UI "Nav: All Clips" { winapp ui wait-for "Nav-Smart-AllClips" @W -t 3000 }
Test-UI "Nav: Favorites" { winapp ui wait-for "Nav-Smart-Favorites" @W -t 3000 }

# ─── Select first card → detail pane ───
$card = (winapp ui search "-Card" @W --json 2>$null | ConvertFrom-Json).matches | Select-Object -First 1
Test-UI "Focus search (foreground)" { winapp ui focus "LibrarySearchBox" @W }
Test-UI "Click first card" { winapp ui click $card.automationId @W }
Test-UI "Detail name box populated" { winapp ui wait-for "DetailNameTextBox" @W --value "TinyClips" --contains -t 3000 }
Shot "10-detail"

# ─── Tag editing + save ───
Test-UI "Type tag" { winapp ui send-keys "uitest" --target "DetailTagBox" @W --via send-input }
Test-UI "Submit tag (Enter)" { winapp ui send-keys "enter" @W --via send-input }
Test-UI "Save enabled" { winapp ui wait-for "DetailSaveButton" @W -p IsEnabled --value "True" -t 3000 }
Test-UI "Click Save" { winapp ui invoke "DetailSaveButton" @W }
Test-UI "Tag appears in sidebar" { winapp ui wait-for "Nav-Tag-uitest" @W -t 4000 }
Shot "20-tag-saved"

# ─── Favorite toggle ───
Test-UI "Toggle favorite" { winapp ui invoke "DetailFavoriteToggle" @W }
Test-UI "Favorites count = 1" { winapp ui wait-for "Nav-Smart-Favorites" @W --value "1 clips" --contains -t 4000 }

# ─── Sidebar filter by tag → 1 clip ───
Test-UI "Click tag nav" { winapp ui invoke "Nav-Tag-uitest" @W }
Start-Sleep 1
$visible = @((winapp ui search "-Card" @W --json 2>$null | ConvertFrom-Json).matches)
if ($visible.Count -eq 1) { $pass++; $results += @{ name = "Tag filter shows exactly 1 card"; status = "PASS" } } else { $fail++; $results += @{ name = "Tag filter shows exactly 1 card"; status = "FAIL"; detail = "Found $($visible.Count) cards" } }
Test-UI "Clear filters button visible" { winapp ui wait-for "ClearLibraryFiltersButton" @W -t 3000 }
Test-UI "Clear filters" { winapp ui invoke "ClearLibraryFiltersButton" @W }
Test-UI "Clear filters hidden again" { winapp ui wait-for "ClearLibraryFiltersButton" @W --gone -t 4000 }

# ─── Search ───
Test-UI "Search 'zzzznomatch'" { winapp ui send-keys "zzzznomatch" --target "LibrarySearchBox" @W --via send-input }
Test-UI "Empty state (no match)" { winapp ui wait-for "LibraryEmptyStateAction" @W -t 4000 }
Shot "30-no-match"
Test-UI "Empty-state action clears" { winapp ui invoke "LibraryEmptyStateAction" @W }
Test-UI "Grid back" { winapp ui wait-for "ClipsGridView" @W -t 4000 }

# ─── Filter flyout ───
Test-UI "Open filter flyout" { winapp ui invoke "LibraryFilterButton" @W }
Start-Sleep 0.7
Test-UI "Sort submenu present" { winapp ui wait-for "SortSubMenu" -a $AppPid -t 3000 }
Test-UI "Type submenu present" { winapp ui wait-for "TypeSubMenu" -a $AppPid -t 3000 }
Shot "35-filter-flyout"
winapp ui send-keys "escape" @W --via send-input 2>$null | Out-Null

# ─── View mode ───
Test-UI "Switch to list" { winapp ui invoke "ListViewToggle" @W }
Test-UI "List visible" { winapp ui wait-for "ClipsListView" @W -t 4000 }
Shot "40-list"
Test-UI "Ctrl+Shift+G → grid" { winapp ui send-keys "ctrl+shift+g" @W --via send-input }
Test-UI "Grid visible again" { winapp ui wait-for "ClipsGridView" @W -t 4000 }

# ─── Selection mode + batch bar ───
Test-UI "Enter selection mode" { winapp ui invoke "SelectModeToggle" @W }
Test-UI "Batch bar visible" { winapp ui wait-for "BatchSelectionCount" @W -t 4000 }
Test-UI "Select all" { winapp ui invoke "BatchSelectAll" @W }
Test-UI "Batch delete enabled" { winapp ui wait-for "BatchDelete" @W -p IsEnabled --value "True" -t 4000 }
Shot "50-selection"
Test-UI "Click batch delete" { winapp ui invoke "BatchDelete" @W }
Start-Sleep 0.8
Test-UI "Confirm dialog shown" { winapp ui wait-for "LibraryConfirmDialog" @W -t 4000 }
Shot "55-delete-confirm"
Test-UI "Cancel delete" { winapp ui invoke "Cancel" @W }
Test-UI "Dialog gone" { winapp ui wait-for "LibraryConfirmDialog" @W --gone -t 4000 }
Test-UI "Exit selection (Done)" { winapp ui invoke "BatchDone" @W }
Test-UI "Batch bar gone" { winapp ui wait-for "BatchSelectionCount" @W --gone -t 4000 }

# ─── Context menu ───
$card = (winapp ui search "-Card" @W --json 2>$null | ConvertFrom-Json).matches | Select-Object -First 1
Test-UI "Right-click card" { winapp ui click $card.automationId @W --right }
Start-Sleep 0.7
$root = $card.automationId -replace '-Card$',''
Test-UI "Context: Rename present" { winapp ui wait-for "$root-Grid-Rename" -a $AppPid -t 3000 }
Test-UI "Context: Delete present" { winapp ui wait-for "$root-Grid-Delete" -a $AppPid -t 3000 }
Shot "60-context-menu"
winapp ui send-keys "escape" @W --via send-input 2>$null | Out-Null

# ─── Cleanup: remove favorite we added ───
Test-UI "Reselect card" { winapp ui focus "LibrarySearchBox" @W; winapp ui click $card.automationId @W }
Test-UI "Unfavorite" { winapp ui invoke "DetailFavoriteToggle" @W }
Test-UI "Favorites back to 0" { winapp ui wait-for "Nav-Smart-Favorites" @W --value "0 clips" --contains -t 4000 }

# ─── Accessibility audit ───
$all = (winapp ui inspect @W --interactive --json 2>$null | ConvertFrom-Json).elements
$app = @($all | Where-Object { $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|Edit' -and $_.name -notmatch 'Minimize|Maximize|Close|System' })
$missing = @($app | Where-Object { -not $_.automationId -and -not $_.name })
if ($missing.Count -eq 0) { $pass++; $results += @{ name = "All interactive controls have AutomationId or Name"; status = "PASS" } }
else { $fail++; $results += @{ name = "A11y coverage"; status = "FAIL"; detail = (($missing | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ", ") } }

Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object { Write-Host "  FAIL: $($_.name) — $($_.detail)" -ForegroundColor Red }
$results | ConvertTo-Json | Out-File (Join-Path $ShotDir "test-results.json")
if ($fail -gt 0) { exit 1 } else { exit 0 }
