<#
.SYNOPSIS
  Runs INSIDE an offline Windows Sandbox (as the LogonCommand). Installs the self-contained Tiny
  Clips MSIX without installing .NET or Windows App Runtime, launches it the way winget's
  validation harness does (full exe path, unrelated working directory), clicks through first-run
  onboarding, and writes a verdict + crash evidence to C:\share\sandbox-result.txt.

  Driven by windows\packaging\sandbox\Invoke-SandboxValidation.ps1 on the host; do not run on a
  real machine (it installs an MSIX and trusts a throwaway certificate).
#>
$ErrorActionPreference = 'Continue'
$share = 'C:\share'
$log = Join-Path $share 'sandbox-result.txt'
$cfg = Get-Content (Join-Path $share 'config.json') -Raw | ConvertFrom-Json
function Out($m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; $line | Tee-Object -FilePath $log -Append }
Remove-Item $log -ErrorAction SilentlyContinue

Out "Tiny Clips sandbox validation: $($cfg.Msix)"
Out "OS: $([Environment]::OSVersion.VersionString)  Arch: $env:PROCESSOR_ARCHITECTURE"

$desktopRuntime = Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App\10.*" -ErrorAction SilentlyContinue
$windowsAppRuntime = Get-AppxPackage 'Microsoft.WindowsAppRuntime.1.8*'
Out "Preinstalled .NET 10 Desktop Runtime: $(if ($desktopRuntime) { 'yes' } else { 'no' })"
Out "Preinstalled Windows App Runtime 1.8: $(if ($windowsAppRuntime) { 'yes' } else { 'no' })"

if (Test-Path "$share\smoke.cer") {
    Import-Certificate -FilePath "$share\smoke.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Out 'Trusted throwaway signing certificate.'
}

Out 'Installing MSIX...'
try { Add-AppxPackage -Path (Join-Path $share $cfg.Msix); Out '  installed' }
catch { Out "  FAILED: $_"; Out 'DONE'; return }
Start-Sleep 3
$pkg = Get-AppxPackage -Name Refractored.TinyClips
if (-not $pkg) {
    # Add-AppxPackage can report success while the package did not register (seen when the
    # package's WindowsAppRuntime MinVersion exceeded the installed runtime).
    Out '  FAILED: package not found after install (dependency MinVersion mismatch?)'
    Get-AppxPackage 'Microsoft.WindowsAppRuntime*' | ForEach-Object { Out "    installed runtime: $($_.PackageFullName)" }
    Out 'DONE'; return
}
Out "  $($pkg.PackageFullName)"
$exe = Join-Path $pkg.InstallLocation 'TinyClips.App.exe'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$u = Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);' -Name U -Namespace W -PassThru
function Shot($name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save("$share\$name.png"); $g.Dispose(); $bmp.Dispose()
}

# Like the harness: full path, unrelated working directory.
Out "Launching $exe (cwd C:\Windows\Temp)"
$start = Get-Date
$proc = Start-Process $exe -WorkingDirectory 'C:\Windows\Temp' -PassThru
if (-not $proc) { Out '  FAILED to start'; Out 'DONE'; return }
Start-Sleep 20
$proc.Refresh(); Out "20s: exited=$($proc.HasExited)"
Shot 'welcome'

$p2 = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
if ($p2 -and $p2.MainWindowHandle -ne 0) {
    Out "Activating '$($p2.MainWindowTitle)' and clicking through onboarding (3x Enter)"
    [void]$u::ShowWindow($p2.MainWindowHandle, 5); [void]$u::SetForegroundWindow($p2.MainWindowHandle); Start-Sleep 2
    foreach ($i in 1..3) { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep 2 }
} else { Out 'No main window (onboarding already completed?)' }
Start-Sleep 3
Shot 'after-onboarding'
$proc.Refresh(); Out "after onboarding: exited=$($proc.HasExited)"

$remaining = [Math]::Max(0, $cfg.WaitSeconds - [int]((Get-Date) - $start).TotalSeconds)
Start-Sleep $remaining
$proc.Refresh()
if ($proc.HasExited) { Out "RESULT: FAIL - process exited with code $($proc.ExitCode) (0x$('{0:X8}' -f $proc.ExitCode))" }
else { Out "RESULT: PASS - alive after $($cfg.WaitSeconds)s"; Stop-Process -Id $proc.Id -Force }

$crash = "$env:LOCALAPPDATA\Packages\$($pkg.PackageFamilyName)\LocalCache\Local\TinyClips\Logs\crash.log"
if (Test-Path $crash) { Out '--- crash.log ---'; Get-Content $crash | ForEach-Object { Out $_ } } else { Out 'No crash.log' }
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error', '.NET Runtime'; StartTime = $start } -ErrorAction SilentlyContinue |
    ForEach-Object { Out "[$($_.ProviderName)] $($_.Message)" }
Out 'DONE'
