[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("mac", "windows")]
    [string]$Platform,

    [Parameter(Mandatory = $false)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..\..\..")).Path
}

function Get-PlistValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$KeyName
    )

    $plist = New-Object System.Xml.XmlDocument
    $plist.XmlResolver = $null
    $plist.LoadXml([System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8))
    $dict = $plist.SelectSingleNode("/plist/dict")
    if (-not $dict) {
        throw "Could not parse plist dict in $Path"
    }

    $nodes = @($dict.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })
    for ($i = 0; $i -lt $nodes.Count - 1; $i++) {
        if ($nodes[$i].Name -eq "key" -and $nodes[$i].InnerText -eq $KeyName) {
            return $nodes[$i + 1].InnerText
        }
    }

    throw "Could not find key '$KeyName' in $Path"
}

function Assert-CleanWorkingTree {
    $status = (git status --porcelain)
    if ($status) {
        throw "Working tree must be clean before tagging."
    }
}

function Get-ReleaseHeading {
    param(
        [string]$PlatformName,
        [string]$TagVersion,
        [string]$DateString
    )

    if ($PlatformName -eq "mac") {
        return "## $TagVersion - $DateString"
    }

    return "## [$TagVersion] - $DateString"
}

function Build-TagMessage {
    param(
        [string[]]$Lines,
        [string]$ReleaseHeading,
        [string]$TagVersion
    )

    $capture = $false
    $body = New-Object System.Collections.Generic.List[string]

    foreach ($line in $Lines) {
        if (-not $capture) {
            if ($line -eq $ReleaseHeading) {
                $capture = $true
            }
            continue
        }

        if ($line -match '^## ') {
            break
        }

        if ($line -match '^###\s+') {
            $body.Add(($line -replace '^###\s+', '') + ":")
        } else {
            $body.Add($line)
        }
    }

    if (-not ($body | Where-Object { $_ -match '^.+:\s*$' -or $_ -match '^\-\s+' })) {
        throw "Release notes for $TagVersion are missing after changelog update."
    }

    $messageLines = New-Object System.Collections.Generic.List[string]
    $messageLines.Add("Release $TagVersion")
    $messageLines.Add("")
    $messageLines.AddRange($body)
    return ($messageLines -join "`n")
}

$repoRoot = Get-RepoRoot
Set-Location $repoRoot

Assert-CleanWorkingTree

$today = Get-Date -Format "yyyy-MM-dd"

if ($Platform -eq "mac") {
    $changelogPath = Join-Path $repoRoot "CHANGELOG.md"
    $unreleasedLine = "## Unreleased"
    $infoPlistPath = Join-Path $repoRoot "mac\TinyClips\Info.plist"
    $appVersion = Get-PlistValue -Path $infoPlistPath -KeyName "CFBundleShortVersionString"

    if (-not $Version) {
        if ($appVersion -notmatch '^[0-9]+\.[0-9]+$') {
            throw "mac app version must be X.Y in Info.plist, got: $appVersion"
        }
        $Version = "v$appVersion.0-mac"
    }

    if ($Version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(-mac)?$') {
        throw "Invalid mac version format: $Version (expected vX.Y.Z or vX.Y.Z-mac)"
    }
    if ($Version -notmatch '-mac$') {
        $Version = "$Version-mac"
    }
    if ($Version -notmatch "^v$([regex]::Escape($appVersion))\.[0-9]+-mac$") {
        throw "mac tag version ($Version) does not align with Info.plist version ($appVersion)."
    }
} else {
    $changelogPath = Join-Path $repoRoot "windows\CHANGELOG.md"
    $unreleasedLine = "## [Unreleased]"

    if (-not $Version) {
        $latestWindowsTag = (git tag --list 'v*-windows' --sort=-creatordate | Select-Object -First 1)
        if (-not $latestWindowsTag) {
            throw "Could not infer a windows version. Provide -Version."
        }
        if ($latestWindowsTag -notmatch '^v([0-9]+)\.([0-9]+)\.([0-9]+)-windows$') {
            throw "Latest windows tag does not match expected pattern: $latestWindowsTag"
        }
        $Version = "v$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)-windows"
    }

    if ($Version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+-windows$') {
        throw "Invalid windows version format: $Version (expected vX.Y.Z-windows)"
    }
}

if (git rev-parse -q --verify "refs/tags/$Version" 2>$null) {
    throw "Tag already exists: $Version"
}

if (-not (Test-Path $changelogPath)) {
    throw "Changelog not found: $changelogPath"
}

$originalLines = [System.IO.File]::ReadAllLines($changelogPath, [System.Text.Encoding]::UTF8)
$unreleasedIndex = [Array]::IndexOf($originalLines, $unreleasedLine)
if ($unreleasedIndex -lt 0) {
    throw "Could not find expected Unreleased heading in $changelogPath"
}

$releaseHeading = Get-ReleaseHeading -PlatformName $Platform -TagVersion $Version -DateString $today
$updatedLines = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $originalLines.Count; $i++) {
    $updatedLines.Add($originalLines[$i])
    if ($i -eq $unreleasedIndex) {
        $updatedLines.Add("")
        $updatedLines.Add($releaseHeading)
    }
}

$tagMessage = Build-TagMessage -Lines $updatedLines -ReleaseHeading $releaseHeading -TagVersion $Version

if ($DryRun) {
    Write-Host "Dry run only; no commit or tag created."
    Write-Host "Platform: $Platform"
    Write-Host "Version: $Version"
    Write-Host "Changelog: $changelogPath"
    Write-Host ""
    Write-Host "Tag message preview:"
    Write-Host $tagMessage
    exit 0
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($changelogPath, ($updatedLines -join "`n"), $utf8NoBom)

git add $changelogPath
git commit -m @"
Mark $Version release

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
"@

$tagMessageFile = New-TemporaryFile
try {
    [System.IO.File]::WriteAllText($tagMessageFile, $tagMessage, $utf8NoBom)
    git tag -a $Version -F $tagMessageFile
} finally {
    Remove-Item -Force $tagMessageFile -ErrorAction SilentlyContinue
}

Write-Host "Created commit and tag: $Version"
Write-Host ""
git --no-pager show --no-patch --format=fuller $Version
Write-Host ""
Write-Host "Next steps:"
Write-Host "  git push origin main"
Write-Host "  git push origin $Version"
