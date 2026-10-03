[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,

    [Parameter(Mandatory)]
    [string] $AppInstallerPath,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture,

    [Parameter(Mandatory)]
    [string] $PackageUri,

    [Parameter(Mandatory)]
    [string] $AppInstallerUri
)

$ErrorActionPreference = 'Stop'

foreach ($uriValue in @($PackageUri, $AppInstallerUri)) {
    $uri = $null
    if (-not [Uri]::TryCreate($uriValue, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -ne 'https') {
        throw "App Installer URLs must be absolute HTTPS URLs. Got '$uriValue'."
    }
}

# Only the standalone .appinstaller is generated. The MSIX must not embed this configuration
# (uap13:AutoUpdate): with it embedded, Windows validates the package on first launch and that
# first process crashes activating WinUI (microsoft/winget-pkgs#442954).
$manifestDocument = [Xml.XmlDocument]::new()
$manifestDocument.Load((Resolve-Path $ManifestPath))

$identity = $manifestDocument.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
if (-not $identity) {
    throw "The package manifest must contain an Identity element."
}

function Save-XmlDocument {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlDocument] $Document,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $directory = Split-Path -Parent $Path
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace

    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

$appInstallerNamespace = 'http://schemas.microsoft.com/appx/appinstaller/2018'
$appInstallerDocument = [Xml.XmlDocument]::new()
[void] $appInstallerDocument.AppendChild($appInstallerDocument.CreateXmlDeclaration('1.0', 'utf-8', $null))

$appInstaller = $appInstallerDocument.CreateElement('AppInstaller', $appInstallerNamespace)
$appInstaller.SetAttribute('Uri', $AppInstallerUri)
$appInstaller.SetAttribute('Version', $identity.Version)
[void] $appInstallerDocument.AppendChild($appInstaller)

$mainPackage = $appInstallerDocument.CreateElement('MainPackage', $appInstallerNamespace)
$mainPackage.SetAttribute('Name', $identity.Name)
$mainPackage.SetAttribute('Publisher', $identity.Publisher)
$mainPackage.SetAttribute('Version', $identity.Version)
$mainPackage.SetAttribute('ProcessorArchitecture', $Architecture)
$mainPackage.SetAttribute('Uri', $PackageUri)
[void] $appInstaller.AppendChild($mainPackage)

$updateSettings = $appInstallerDocument.CreateElement('UpdateSettings', $appInstallerNamespace)
$onLaunch = $appInstallerDocument.CreateElement('OnLaunch', $appInstallerNamespace)
$onLaunch.SetAttribute('HoursBetweenUpdateChecks', '24')
$onLaunch.SetAttribute('ShowPrompt', 'false')
$onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
[void] $updateSettings.AppendChild($onLaunch)
[void] $updateSettings.AppendChild($appInstallerDocument.CreateElement('AutomaticBackgroundTask', $appInstallerNamespace))
[void] $appInstaller.AppendChild($updateSettings)

Save-XmlDocument -Document $appInstallerDocument -Path $AppInstallerPath
