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

    $appRuntime = Require-File 'Microsoft.WindowsAppRuntime.dll'
    $xamlRuntime = Require-File 'Microsoft.UI.Xaml.dll'
    $appExecutable = Require-File 'TinyClips.App.exe'

    foreach ($forbiddenFile in @(
        'TinyClips.App.dll',
        'coreclr.dll',
        'clrjit.dll',
        'hostfxr.dll',
        'hostpolicy.dll')) {
        if (Get-ChildItem $verifyDirectory -Recurse -File |
            Where-Object { $_.Name -ieq $forbiddenFile } |
            Select-Object -First 1) {
            throw "Direct NativeAOT package unexpectedly contains '$forbiddenFile'."
        }
    }

    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($appExecutable.FullName)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        $expectedMachine = if ($Architecture -eq 'arm64') { 'Arm64' } else { 'Amd64' }
        $actualMachine = [string] $peReader.PEHeaders.CoffHeader.Machine
        if ($actualMachine -ne $expectedMachine) {
            throw "Native executable machine '$actualMachine' does not match expected '$expectedMachine'."
        }
        if ($null -ne $peReader.PEHeaders.CorHeader) {
            throw "TinyClips.App.exe contains a CLR header; expected a NativeAOT executable."
        }
        if ($peReader.PEHeaders.PEHeader.AddressOfEntryPoint -eq 0) {
            throw "TinyClips.App.exe does not have a native entry point."
        }
    }
    finally {
        if ($peReader) {
            $peReader.Dispose()
        }
        $stream.Dispose()
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
        NativeExecutableMiB = [Math]::Round($appExecutable.Length / 1MB, 2)
        WindowsAppRuntimeMiB = [Math]::Round($appRuntime.Length / 1MB, 2)
        XamlRuntimeMiB = [Math]::Round($xamlRuntime.Length / 1MB, 2)
    } | Format-List
}
finally {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $verifyDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
