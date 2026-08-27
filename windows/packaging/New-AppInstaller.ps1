[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,

    [Parameter(Mandatory)]
    [string] $ManifestOutputPath,

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

$manifestDocument = [Xml.XmlDocument]::new()
$manifestDocument.PreserveWhitespace = $true
$manifestDocument.Load((Resolve-Path $ManifestPath))

$package = $manifestDocument.DocumentElement
$identity = $package.SelectSingleNode("*[local-name()='Identity']")
$properties = $package.SelectSingleNode("*[local-name()='Properties']")
if (-not $identity -or -not $properties) {
    throw "The package manifest must contain Identity and Properties elements."
}

$uap13Namespace = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/13'
$xmlNamespace = 'http://www.w3.org/2000/xmlns/'
if (-not $package.GetAttribute('uap13', $xmlNamespace)) {
    $namespaceAttribute = $manifestDocument.CreateAttribute('xmlns', 'uap13', $xmlNamespace)
    $namespaceAttribute.Value = $uap13Namespace
    [void] $package.Attributes.Append($namespaceAttribute)
}

$ignorableNamespaces = @($package.GetAttribute('IgnorableNamespaces') -split '\s+' | Where-Object { $_ })
if ($ignorableNamespaces -notcontains 'uap13') {
    $package.SetAttribute('IgnorableNamespaces', (($ignorableNamespaces + 'uap13') -join ' '))
}

$namespaceManager = [Xml.XmlNamespaceManager]::new($manifestDocument.NameTable)
$namespaceManager.AddNamespace('uap13', $uap13Namespace)
$existingAutoUpdate = $properties.SelectSingleNode('uap13:AutoUpdate', $namespaceManager)
if ($existingAutoUpdate) {
    [void] $properties.RemoveChild($existingAutoUpdate)
}

$autoUpdate = $manifestDocument.CreateElement('uap13', 'AutoUpdate', $uap13Namespace)
$appInstallerDeclaration = $manifestDocument.CreateElement('uap13', 'AppInstaller', $uap13Namespace)
$appInstallerDeclaration.SetAttribute('File', 'install.appinstaller')
[void] $autoUpdate.AppendChild($appInstallerDeclaration)
[void] $properties.AppendChild($autoUpdate)

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

Save-XmlDocument -Document $manifestDocument -Path $ManifestOutputPath

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
