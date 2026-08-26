<#
.SYNOPSIS
  One-command Windows Sandbox validation of a Tiny Clips MSIX, mirroring winget's Installation
  Validation: fresh OS, runtimes installed from their installers, MSIX installed, app launched
  from C:\Program Files\WindowsApps with an unrelated working directory, first-run onboarding
  clicked through, process must stay alive.

.DESCRIPTION
  Run from the repository root on the host. Requires Windows Sandbox (optional feature
  "Containers-DisposableClientVM"), the Windows 10/11 SDK (signtool), and - for -Source Build -
  the .NET 10 SDK. Windows Sandbox is x64-only, so this validates the x64 package; use the
  "Windows Launch Smoke" GitHub workflow for ARM64.

  Only one Sandbox instance can run at a time; close any open Sandbox window first.

.PARAMETER Source
  Release  - download TinyClips-<Version>-x64.msix from the GitHub release tag v<Version>-windows
             (already Azure-signed; no certificate needed).
  Build    - build the MSIX from the working tree with the exact release recipe and sign it with a
             throwaway self-signed certificate that the Sandbox trusts.

.PARAMETER Version
  Asset version, e.g. 1.7.4 (used for the download name or to stamp the build).

.PARAMETER WaitSeconds
  How long the app must stay alive to pass. Default 60.

.PARAMETER WorkDir
  Host folder mapped into the Sandbox as C:\share. Default: %TEMP%\tinyclips-sandbox.

.EXAMPLE
  .\windows\packaging\sandbox\Invoke-SandboxValidation.ps1 -Source Release -Version 1.7.4
.EXAMPLE
  .\windows\packaging\sandbox\Invoke-SandboxValidation.ps1 -Source Build -Version 1.7.4
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Build')] [string] $Source = 'Build',
    [Parameter(Mandatory)] [string] $Version,
    [int] $WaitSeconds = 60,
    [string] $WorkDir = (Join-Path $env:TEMP 'tinyclips-sandbox')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$appProject = Join-Path $repo 'windows\src\TinyClips.App\TinyClips.App.csproj'
$manifestPath = Join-Path $repo 'windows\src\TinyClips.App\Package.appxmanifest'
$msixName = "TinyClips-$Version-x64.msix"

if (-not (Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue)) { throw 'Windows Sandbox is not installed (Turn Windows features on or off -> Windows Sandbox).' }
if (Get-Process WindowsSandbox* -ErrorAction SilentlyContinue) { throw 'A Windows Sandbox instance is already running; close it first.' }

New-Item -ItemType Directory -Force $WorkDir | Out-Null
Remove-Item (Join-Path $WorkDir 'sandbox-result.txt'), (Join-Path $WorkDir '*.png'), (Join-Path $WorkDir 'smoke.cer') -ErrorAction SilentlyContinue

# --- Runtimes (cached between runs) ---
$dotnet = Join-Path $WorkDir 'windowsdesktop-runtime.exe'
$war = Join-Path $WorkDir 'WindowsAppRuntimeInstall.exe'
if (-not (Test-Path $dotnet)) { Write-Host 'Downloading .NET 10 Desktop Runtime installer...'; Invoke-WebRequest 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe' -OutFile $dotnet -MaximumRedirection 10 }
if (-not (Test-Path $war)) {
    # Same runtime build winget's Microsoft.WindowsAppRuntime.1.8 (1.8.9) installs: 8000.879.2017.0.
    Write-Host 'Downloading Windows App Runtime 1.8 installer...'
    Invoke-WebRequest 'https://aka.ms/windowsappsdk/1.8/1.8.260529003/windowsappruntimeinstall-x64.exe' -OutFile $war -MaximumRedirection 10
}

# --- Package ---
$msixPath = Join-Path $WorkDir $msixName
if ($Source -eq 'Release') {
    Write-Host "Downloading $msixName from release v$Version-windows..."
    gh release download "v$Version-windows" --repo jamesmontemagno/tiny-clips --pattern $msixName --dir $WorkDir --clobber
    if ($LASTEXITCODE -ne 0) { throw 'gh release download failed.' }
} else {
    Write-Host "Building framework-dependent x64 MSIX $Version from the working tree..."
    $packageDir = Join-Path $WorkDir 'build'
    Remove-Item $packageDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $packageDir | Out-Null
    $manifestBackup = Get-Content $manifestPath -Raw
    try {
        ($manifestBackup -creplace '(<Identity[\s\S]*?Version=")[^"]+(")', "`${1}$Version.0`${2}") | Set-Content $manifestPath -Encoding UTF8 -NoNewline
        dotnet build $appProject -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
            -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -p:PublishTrimmed=false `
            -p:EnableMsixTooling=true -p:GenerateAppxPackageOnBuild=true `
            -p:AppxPackageDir="$packageDir\" -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
            -p:AppxPackageSigningEnabled=false -nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed.' }
    } finally {
        Set-Content $manifestPath $manifestBackup -NoNewline
    }
    $produced = Get-ChildItem $packageDir -Recurse -Filter *.msix | Where-Object Name -notmatch 'symbols' | Select-Object -First 1
    if (-not $produced) { throw 'No MSIX produced.' }
    Copy-Item $produced.FullName $msixPath -Force

    Write-Host 'Signing with a throwaway self-signed certificate...'
    $subject = ([xml](Get-Content $manifestPath)).Package.Identity.Publisher
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'TinyClips sandbox validation' `
        -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    try {
        Export-Certificate -Cert $cert -FilePath (Join-Path $WorkDir 'smoke.cer') -Force | Out-Null
        $pfx = Join-Path $WorkDir 'smoke.pfx'
        $pwd = ConvertTo-SecureString 'sandbox' -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd | Out-Null
        $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory | Where-Object Name -match '^10\.' | Sort-Object Name -Descending | Select-Object -First 1
        if (-not $kits) { throw 'Windows SDK (signtool) not found.' }
        & (Join-Path $kits.FullName 'x64\signtool.exe') sign /fd SHA256 /f $pfx /p sandbox $msixPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }
    } finally {
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $WorkDir 'smoke.pfx') -ErrorAction SilentlyContinue
    }
}

# Report what the package demands vs. what winget can install.
$verify = Join-Path $WorkDir 'verify'
Remove-Item $verify -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $msixPath "$msixPath.zip" -Force; Expand-Archive "$msixPath.zip" $verify -Force; Remove-Item "$msixPath.zip"
$dep = ([xml](Get-Content (Join-Path $verify 'AppxManifest.xml'))).Package.Dependencies.PackageDependency | Where-Object Name -eq 'Microsoft.WindowsAppRuntime.1.8'
Write-Host "Package requires Microsoft.WindowsAppRuntime.1.8 >= $($dep.MinVersion) (winget 1.8.9 provides 8000.879.2017.0)"
Remove-Item $verify -Recurse -Force -ErrorAction SilentlyContinue

# --- Sandbox ---
Copy-Item (Join-Path $PSScriptRoot 'Validate-TinyClips.ps1') (Join-Path $WorkDir 'Validate-TinyClips.ps1') -Force
@{ Msix = $msixName; WaitSeconds = $WaitSeconds } | ConvertTo-Json | Set-Content (Join-Path $WorkDir 'config.json')
$wsb = Join-Path $WorkDir 'TinyClips.wsb'
@"
<Configuration>
  <vGPU>Disable</vGPU>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$WorkDir</HostFolder>
      <SandboxFolder>C:\share</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell -ExecutionPolicy Bypass -NoProfile -File C:\share\Validate-TinyClips.ps1</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content $wsb -Encoding UTF8

Write-Host "Starting Windows Sandbox (runtime install takes ~8-10 minutes; leave the window alone)..."
Start-Process $wsb
$result = Join-Path $WorkDir 'sandbox-result.txt'
$deadline = (Get-Date).AddMinutes(20)
while ((Get-Date) -lt $deadline) {
    Start-Sleep 15
    if ((Test-Path $result) -and (Select-String -Path $result -Pattern '^.*DONE$' -Quiet)) { break }
}
Write-Host ''
if (Test-Path $result) { Get-Content $result } else { Write-Warning 'No result file produced (Sandbox did not start or the logon command failed).' }
Write-Host ''
Write-Host "Screenshots: $(Join-Path $WorkDir 'welcome.png'), $(Join-Path $WorkDir 'after-onboarding.png')"
Write-Host 'Close the Sandbox window when done.'
if ((Test-Path $result) -and (Select-String -Path $result -Pattern 'RESULT: PASS' -Quiet)) { exit 0 } else { exit 1 }
