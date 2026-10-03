[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MsixPath,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture
)

$ErrorActionPreference = 'Stop'
$resolvedMsix = (Resolve-Path $MsixPath).Path
$verifyDirectory = Join-Path ([IO.Path]::GetTempPath()) "tinyclips-package-$([Guid]::NewGuid().ToString('N'))"
$zipPath = "$resolvedMsix.zip"

function Require-File([string] $Name) {
    $file = Get-ChildItem $verifyDirectory -Recurse -File |
        Where-Object { $_.Name -ieq $Name } |
        Select-Object -First 1
    if (-not $file) {
        throw "Direct package is missing required payload '$Name'."
    }

    return $file
}

try {
    Copy-Item $resolvedMsix $zipPath -Force
    Expand-Archive $zipPath -DestinationPath $verifyDirectory -Force

    [xml] $manifest = Get-Content (Join-Path $verifyDirectory 'AppxManifest.xml') -Raw
    $manifestArchitecture = [string] $manifest.Package.Identity.ProcessorArchitecture
    if ($manifestArchitecture -ine $Architecture) {
        throw "Package architecture '$manifestArchitecture' does not match expected '$Architecture'."
    }

    $frameworkDependencies = $manifest.SelectNodes("//*[local-name()='PackageDependency']")
    $windowsAppRuntimeDependency = $frameworkDependencies |
        Where-Object { $_.Name -like 'Microsoft.WindowsAppRuntime*' }
    if ($windowsAppRuntimeDependency) {
        throw "Direct package still declares a Windows App Runtime framework dependency."
    }

    # With an embedded App Installer declaration, Windows validates the package on first launch
    # and that first process crashes activating WinUI (CLASS_E_CLASSNOTAVAILABLE, exit code
    # 0xC0000409), which failed microsoft/winget-pkgs#442954. Auto-updating installs use the
    # standalone .appinstaller release asset instead.
    if ($manifest.SelectNodes("//*[local-name()='AutoUpdate']").Count -gt 0) {
        throw "Direct package declares uap13:AutoUpdate; the App Installer configuration must not be embedded."
    }
    if (Get-ChildItem $verifyDirectory -Recurse -File -Filter *.appinstaller | Select-Object -First 1) {
        throw "Direct package contains an embedded .appinstaller file."
    }

    $appRuntime = Require-File 'Microsoft.WindowsAppRuntime.dll'
    $xamlRuntime = Require-File 'Microsoft.UI.Xaml.dll'
    $appExecutable = Require-File 'TinyClips.App.exe'

    # Direct packages bundle the .NET runtime and run on CoreCLR, like the Store flavor. A missing
    # managed assembly or runtime means the package is NativeAOT or framework-dependent instead.
    $managedApp = Require-File 'TinyClips.App.dll'
    $coreClr = Require-File 'coreclr.dll'
    $null = Require-File 'hostfxr.dll'
    $null = Require-File 'hostpolicy.dll'

    Add-Type -AssemblyName System.Reflection.Metadata
    $expectedMachine = if ($Architecture -eq 'arm64') { 'Arm64' } else { 'Amd64' }
    foreach ($nativeFile in @($appExecutable, $coreClr)) {
        $stream = [IO.File]::OpenRead($nativeFile.FullName)
        $peReader = $null
        try {
            $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            $actualMachine = [string] $peReader.PEHeaders.CoffHeader.Machine
            if ($actualMachine -ne $expectedMachine) {
                throw "$($nativeFile.Name) machine '$actualMachine' does not match expected '$expectedMachine'."
            }
        }
        finally {
            if ($peReader) {
                $peReader.Dispose()
            }
            $stream.Dispose()
        }
    }

    $executableText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($appExecutable.FullName))
    if ($executableText.IndexOf('activatableClass', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "TinyClips.App.exe is missing embedded registration-free WinRT activatable-class metadata."
    }

    $msix = Get-Item $resolvedMsix
    $expandedBytes = (Get-ChildItem $verifyDirectory -Recurse -File | Measure-Object Length -Sum).Sum
    [pscustomobject]@{
        Architecture = $Architecture
        MsixMiB = [Math]::Round($msix.Length / 1MB, 2)
        ExpandedMiB = [Math]::Round($expandedBytes / 1MB, 2)
        ManagedAppMiB = [Math]::Round($managedApp.Length / 1MB, 2)
        WindowsAppRuntimeMiB = [Math]::Round($appRuntime.Length / 1MB, 2)
        XamlRuntimeMiB = [Math]::Round($xamlRuntime.Length / 1MB, 2)
    } | Format-List
}
finally {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $verifyDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
