<#
.SYNOPSIS
    Collects Tiny Clips diagnostics from this machine into one zip that can be sent for analysis.

.DESCRIPTION
    Run this on the machine that shows the problem (x64 or ARM64). It never uploads anything; it
    writes a folder and a zip to the Desktop. It gathers:

      - system.txt        OS build, CPU, native architecture, VM/model, GPU adapters and drivers,
                          displays, power state.
      - package.txt       Installed Tiny Clips packages, the running process (native or emulated,
                          memory, threads, start time) and bundled-runtime architecture.
      - logs\             crash.log, crash.previous.log, capture-flow-trace.log and
                          webcam-diagnostics.log from both places the app can write them.
      - events.txt        Application event log entries for Tiny Clips (crashes, hangs, .NET).
      - wer\              Windows Error Reporting summaries (Report.wer) for Tiny Clips.
      - responsiveness.*  Only with -LiveSeconds: how long the app's UI thread took to answer,
                          sampled while you reproduce the lag.

    Settings, captures, credentials and memory dumps are not collected.

.PARAMETER LiveSeconds
    Watch the running app's UI thread for this many seconds and record every stall. Start the
    script, then reproduce the lag (open the tray menu, take a screenshot, open the editor).

.PARAMETER EnableTrace
    Turn on the app's capture-flow timing log (sets the user environment variable
    TINYCLIPS_CAPTURE_TRACE=1) and exit. Restart Tiny Clips afterwards, reproduce the lag, then
    run this script again to collect the log.

.PARAMETER DisableTrace
    Remove the TINYCLIPS_CAPTURE_TRACE user environment variable and exit.

.PARAMETER Days
    How far back to read the event log and error reports. Default 7.

.PARAMETER OutputDirectory
    Where to write the results folder and zip. Default: Desktop.

.EXAMPLE
    .\Collect-Diagnostics.ps1
    Collect logs and system details.

.EXAMPLE
    .\Collect-Diagnostics.ps1 -EnableTrace
    # restart Tiny Clips, then:
    .\Collect-Diagnostics.ps1 -LiveSeconds 90
    Record UI-thread stalls and capture-flow timings while reproducing a slowdown.
#>
[CmdletBinding()]
param(
    [int] $LiveSeconds = 0,
    [switch] $EnableTrace,
    [switch] $DisableTrace,
    [int] $Days = 7,
    [string] $OutputDirectory = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Continue'
$packageFamilyName = 'Refractored.TinyClips_vmshqmcyy894t'
$processName = 'TinyClips.App'
$traceVariable = 'TINYCLIPS_CAPTURE_TRACE'

if ($EnableTrace -or $DisableTrace) {
    $value = if ($EnableTrace) { '1' } else { $null }
    [Environment]::SetEnvironmentVariable($traceVariable, $value, 'User')
    if ($EnableTrace) {
        Write-Host "Capture-flow tracing is ON for your user account."
        Write-Host "Exit Tiny Clips from its tray menu, start it again, reproduce the slowdown,"
        Write-Host "then run this script again (add -LiveSeconds 90 to also record UI stalls)."
    }
    else {
        Write-Host "Capture-flow tracing is OFF. Restart Tiny Clips for it to take effect."
    }
    return
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class TinyClipsDiag
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    public sealed class WindowInfo
    {
        public IntPtr Handle;
        public uint ThreadId;
        public bool Visible;
        public string Title;
        public string ClassName;
    }

    public static List<WindowInfo> GetWindows(int processId)
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hwnd, lParam) =>
        {
            uint owner;
            uint thread = GetWindowThreadProcessId(hwnd, out owner);
            if (owner == (uint)processId)
            {
                var title = new StringBuilder(256);
                var className = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                GetClassName(hwnd, className, className.Capacity);
                windows.Add(new WindowInfo { Handle = hwnd, ThreadId = thread, Visible = IsWindowVisible(hwnd), Title = title.ToString(), ClassName = className.ToString() });
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    // Round-trip time for a no-op message: how long the window's thread took to get back to its
    // message loop. Returns -1 when the window has gone away, or the timeout when it never answered.
    public static double PingMilliseconds(IntPtr hwnd, uint timeoutMs)
    {
        if (!IsWindow(hwnd)) { return -1; }
        IntPtr result;
        long start = Stopwatch.GetTimestamp();
        IntPtr ok = SendMessageTimeout(hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0 /* SMTO_NORMAL: wait for the thread */, timeoutMs, out result);
        double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (ok == IntPtr.Zero && !IsWindow(hwnd)) { return -1; }
        return ok == IntPtr.Zero ? Math.Max(elapsed, timeoutMs) : elapsed;
    }

    public static string DescribeMachine(ushort machine)
    {
        switch (machine)
        {
            case 0x0000: return "native";
            case 0x014c: return "x86";
            case 0x8664: return "x64";
            case 0xAA64: return "ARM64";
            case 0x01c4: return "ARM";
            default: return "0x" + machine.ToString("X4");
        }
    }

    public static string ProcessArchitecture(IntPtr processHandle)
    {
        ushort processMachine, nativeMachine;
        if (!IsWow64Process2(processHandle, out processMachine, out nativeMachine)) { return "unknown"; }
        return "process=" + DescribeMachine(processMachine) + " os=" + DescribeMachine(nativeMachine);
    }
}
'@

function Write-Section([string] $Path, [string] $Title, [scriptblock] $Body) {
    "===== $Title =====" | Out-File $Path -Append -Encoding utf8
    try {
        & $Body | Out-String -Width 220 | ForEach-Object { $_.TrimEnd() } | Out-File $Path -Append -Encoding utf8
    }
    catch {
        "(failed: $($_.Exception.Message))" | Out-File $Path -Append -Encoding utf8
    }
    '' | Out-File $Path -Append -Encoding utf8
}

function Get-PeMachine([string] $Path) {
    try {
        $stream = [IO.File]::OpenRead($Path)
        try {
            $reader = [IO.BinaryReader]::new($stream)
            $stream.Position = 0x3C
            $stream.Position = $reader.ReadInt32() + 4
            return [TinyClipsDiag]::DescribeMachine($reader.ReadUInt16())
        }
        finally { $stream.Dispose() }
    }
    catch { return 'unreadable' }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultName = "TinyClips-diagnostics-$env:COMPUTERNAME-$stamp"
$result = Join-Path $OutputDirectory $resultName
New-Item -ItemType Directory -Force -Path $result, (Join-Path $result 'logs'), (Join-Path $result 'wer') | Out-Null
$since = (Get-Date).AddDays(-[Math]::Abs($Days))

# ---- Live UI-thread responsiveness (run first so the rest of the collection reflects it) -----
if ($LiveSeconds -gt 0) {
    $app = Get-Process $processName -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1
    if (-not $app) {
        Write-Host "Tiny Clips is not running; start it and run again with -LiveSeconds." -ForegroundColor Yellow
        "Tiny Clips was not running, so no responsiveness data was recorded." | Out-File (Join-Path $result 'responsiveness.txt') -Encoding utf8
    }
    else {
        Write-Host ""
        Write-Host "Watching Tiny Clips (PID $($app.Id)) for $LiveSeconds seconds." -ForegroundColor Cyan
        Write-Host "Reproduce the slowdown now: open the tray menu, take a screenshot, open the editor..." -ForegroundColor Cyan
        $csv = Join-Path $result 'responsiveness.csv'
        'time,elapsedSeconds,uiThreadDelayMs,cpuPercentOfOneCore,workingSetMB,threads,visibleWindows' | Out-File $csv -Encoding utf8
        $stalls = New-Object System.Collections.Generic.List[string]
        $samples = New-Object System.Collections.Generic.List[double]
        $windowLog = New-Object System.Collections.Generic.HashSet[string]
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $lastCpu = $app.TotalProcessorTime
        $lastCpuAt = $watch.Elapsed
        $uiThread = 0
        $target = [IntPtr]::Zero
        $exited = $false
        while ($watch.Elapsed.TotalSeconds -lt $LiveSeconds) {
            $app.Refresh()
            if ($app.HasExited) { $exited = $true; break }

            $windows = [TinyClipsDiag]::GetWindows($app.Id)
            foreach ($w in $windows) {
                if ($w.Visible -and $w.Title) { [void] $windowLog.Add($w.Title) }
            }
            # The thread that owns the visible XAML windows is the UI thread; fall back to the
            # thread owning the most windows while only the tray icon exists.
            if ($uiThread -eq 0 -or -not ($windows | Where-Object { $_.Handle -eq $target })) {
                $xaml = $windows | Where-Object { $_.ClassName -eq 'WinUIDesktopWin32WindowClass' } | Select-Object -First 1
                if ($xaml) { $uiThread = $xaml.ThreadId }
                elseif ($windows.Count -gt 0) { $uiThread = ($windows | Group-Object ThreadId | Sort-Object Count -Descending | Select-Object -First 1).Group[0].ThreadId }
                $target = ($windows | Where-Object { $_.ThreadId -eq $uiThread } | Select-Object -First 1).Handle
            }

            $delay = if ($target -and $target -ne [IntPtr]::Zero) { [TinyClipsDiag]::PingMilliseconds($target, 10000) } else { -1 }
            $now = $watch.Elapsed
            $cpuNow = $app.TotalProcessorTime
            $interval = ($now - $lastCpuAt).TotalMilliseconds
            $cpuPercent = if ($interval -gt 0) { [Math]::Round(($cpuNow - $lastCpu).TotalMilliseconds / $interval * 100, 0) } else { 0 }
            $lastCpu = $cpuNow; $lastCpuAt = $now
            $visible = @($windows | Where-Object { $_.Visible -and $_.Title }).Count
            if ($delay -ge 0) { $samples.Add($delay) }
            ('{0:HH:mm:ss.fff},{1:F2},{2:F0},{3},{4},{5},{6}' -f (Get-Date), $now.TotalSeconds, $delay, $cpuPercent, [int]($app.WorkingSet64 / 1MB), $app.Threads.Count, $visible) | Out-File $csv -Append -Encoding utf8
            if ($delay -ge 250) {
                $titles = (@($windows | Where-Object { $_.Visible -and $_.Title } | ForEach-Object Title) -join '; ')
                $line = '{0:HH:mm:ss.fff}  UI thread did not respond for {1,6:F0} ms  (CPU {2}% of one core; windows: {3})' -f (Get-Date), $delay, $cpuPercent, $titles
                $stalls.Add($line)
                Write-Host "  $line" -ForegroundColor Yellow
            }
            Start-Sleep -Milliseconds 100
        }

        $summary = Join-Path $result 'responsiveness.txt'
        $sorted = @($samples | Sort-Object)
        "Tiny Clips PID $($app.Id), watched for $([int]$watch.Elapsed.TotalSeconds) s, $($samples.Count) samples." | Out-File $summary -Encoding utf8
        if ($exited) { "The process EXITED during the watch (exit code $($app.ExitCode))." | Out-File $summary -Append -Encoding utf8 }
        if ($sorted.Count -gt 0) {
            $p50 = $sorted[[int][Math]::Floor(($sorted.Count - 1) * 0.50)]
            $p95 = $sorted[[int][Math]::Floor(($sorted.Count - 1) * 0.95)]
            ('UI-thread response time: median {0:F0} ms, 95th percentile {1:F0} ms, worst {2:F0} ms.' -f $p50, $p95, $sorted[-1]) | Out-File $summary -Append -Encoding utf8
            ('Stalls of 250 ms or longer: {0}. Of 1 s or longer: {1}.' -f $stalls.Count, @($samples | Where-Object { $_ -ge 1000 }).Count) | Out-File $summary -Append -Encoding utf8
        }
        "Windows seen: $((@($windowLog) | Sort-Object) -join '; ')" | Out-File $summary -Append -Encoding utf8
        '' | Out-File $summary -Append -Encoding utf8
        $stalls | Out-File $summary -Append -Encoding utf8
        Write-Host ""
        Get-Content $summary | Select-Object -First 5 | ForEach-Object { Write-Host $_ }
    }
}

# ---- System ---------------------------------------------------------------------------------
$system = Join-Path $result 'system.txt'
Write-Section $system 'Operating system' {
    $os = Get-CimInstance Win32_OperatingSystem
    $ubr = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).UBR
    [pscustomobject]@{
        Caption = $os.Caption
        Version = "$($os.Version).$ubr"
        DisplayVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).DisplayVersion
        OSArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        ShellArchitecture = $env:PROCESSOR_ARCHITECTURE
        LastBoot = $os.LastBootUpTime
        FreeMemoryMB = [int]($os.FreePhysicalMemory / 1KB)
        TotalMemoryMB = [int]($os.TotalVisibleMemorySize / 1KB)
    } | Format-List
}
Write-Section $system 'Computer (a virtual machine shows here)' {
    Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, SystemType, HypervisorPresent, NumberOfLogicalProcessors | Format-List
    Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed | Format-List
}
Write-Section $system 'Display adapters' {
    Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, Status | Format-List
}
Write-Section $system 'Monitors and scaling' {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.Screen]::AllScreens | Select-Object DeviceName, Primary, Bounds, WorkingArea | Format-List
    "AppliedDPI (primary): $((Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI)"
}
Write-Section $system 'Power' {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SystemInformation]::PowerStatus | Format-List
    "Energy saver active: $((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' -ErrorAction SilentlyContinue).ActiveOverlayAcPowerScheme)"
    powercfg /getactivescheme 2>$null
}
Write-Section $system 'Session' {
    "Remote session (RDP): $([System.Windows.Forms.SystemInformation]::TerminalServerSession)"
    "Transparency effects enabled: $((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue).EnableTransparency)"
    "PowerShell: $($PSVersionTable.PSVersion) ($([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) process)"
}

# ---- Package and process --------------------------------------------------------------------
$packageReport = Join-Path $result 'package.txt'
$packages = @(Get-AppxPackage -Name '*TinyClips*' -ErrorAction SilentlyContinue)
Write-Section $packageReport 'Installed Tiny Clips packages' {
    if ($packages.Count -eq 0) { 'No Tiny Clips package is installed for this user.' }
    $packages | Select-Object Name, Version, Architecture, SignatureKind, IsDevelopmentMode, PackageFamilyName, InstallLocation | Format-List
}
Write-Section $packageReport 'Package binaries (architecture)' {
    foreach ($package in $packages) {
        $location = $package.InstallLocation
        if (-not $location -or -not (Test-Path $location)) { continue }
        "[$($package.PackageFullName)]"
        foreach ($name in 'TinyClips.App.exe', 'TinyClips.App.dll', 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'libSkiaSharp.dll', 'Microsoft.Graphics.Canvas.dll') {
            $file = Join-Path $location $name
            if (Test-Path $file) { '  {0,-34} {1,-8} {2,8:N1} MB  {3}' -f $name, (Get-PeMachine $file), ((Get-Item $file).Length / 1MB), (Get-Item $file).VersionInfo.FileVersion }
            else { '  {0,-34} (not in package)' -f $name }
        }
    }
}
Write-Section $packageReport 'Running Tiny Clips processes' {
    $running = @(Get-Process $processName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { 'Tiny Clips is not running.' }
    foreach ($p in $running) {
        [pscustomobject]@{
            Id = $p.Id
            Path = $p.Path
            Architecture = [TinyClipsDiag]::ProcessArchitecture($p.Handle)
            StartTime = $p.StartTime
            Responding = $p.Responding
            CpuSeconds = [Math]::Round($p.TotalProcessorTime.TotalSeconds, 1)
            WorkingSetMB = [int]($p.WorkingSet64 / 1MB)
            PrivateMB = [int]($p.PrivateMemorySize64 / 1MB)
            Threads = $p.Threads.Count
            Handles = $p.HandleCount
        } | Format-List
        'Windows:'
        [TinyClipsDiag]::GetWindows($p.Id) | Where-Object { $_.Visible -or $_.Title } | ForEach-Object { '  visible={0,-5} thread={1,-6} class={2,-34} "{3}"' -f $_.Visible, $_.ThreadId, $_.ClassName, $_.Title }
    }
}
Write-Section $packageReport 'Tracing' {
    "$traceVariable (user): $([Environment]::GetEnvironmentVariable($traceVariable, 'User'))"
}

# ---- App logs -------------------------------------------------------------------------------
$logRoots = @(
    (Join-Path $env:LOCALAPPDATA 'TinyClips'),
    (Join-Path $env:LOCALAPPDATA "Packages\$packageFamilyName\LocalCache\Local\TinyClips")
) + @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter '*TinyClips*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\TinyClips' })
$logNames = 'crash.log', 'crash.previous.log', 'capture-flow-trace.log', 'webcam-diagnostics.log', 'tinyclips.log'
$copied = 0
$logIndex = Join-Path $result 'logs\_where-these-came-from.txt'
foreach ($root in ($logRoots | Select-Object -Unique)) {
    if (-not (Test-Path $root)) { "$root  (does not exist)" | Out-File $logIndex -Append -Encoding utf8; continue }
    $found = @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $logNames -contains $_.Name })
    "$root  ($($found.Count) log files)" | Out-File $logIndex -Append -Encoding utf8
    foreach ($file in $found) {
        $prefix = if ($root -like '*\Packages\*') { 'package' } else { 'localappdata' }
        $destination = Join-Path $result ("logs\{0}-{1}" -f $prefix, $file.Name)
        # Trace logs can grow large; the most recent part is what matters.
        if ($file.Length -gt 2MB) { Get-Content $file.FullName -Tail 20000 | Out-File $destination -Encoding utf8 }
        else { Copy-Item $file.FullName $destination -Force }
        "    $($file.FullName)  ($($file.Length) bytes, modified $($file.LastWriteTime))" | Out-File $logIndex -Append -Encoding utf8
        $copied++
    }
}

# ---- Event log and Windows Error Reporting ---------------------------------------------------
$events = Join-Path $result 'events.txt'
Write-Section $events "Application event log, Tiny Clips entries since $since" {
    $entries = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'Application Hang', 'Windows Error Reporting' -and $_.Message -match 'TinyClips' } |
        Select-Object -First 200
    if (-not $entries) { 'No crash or hang entries for Tiny Clips.' }
    foreach ($entry in $entries) {
        '[{0:yyyy-MM-dd HH:mm:ss}] {1} (Id {2})' -f $entry.TimeCreated, $entry.ProviderName, $entry.Id
        $entry.Message
        '-----'
    }
}
$werRoots = @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WER\ReportArchive'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WER\ReportQueue'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue')
)
$werCount = 0
foreach ($root in $werRoots) {
    Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'TinyClips|Refractored\.Tiny' -and $_.LastWriteTime -ge $since } |
        ForEach-Object {
            $report = Join-Path $_.FullName 'Report.wer'
            if (Test-Path $report) {
                Copy-Item $report (Join-Path $result ("wer\{0}.wer" -f $_.Name)) -Force -ErrorAction SilentlyContinue
                $werCount++
            }
        }
}

# ---- Zip -------------------------------------------------------------------------------------
$zip = "$result.zip"
Compress-Archive -Path (Join-Path $result '*') -DestinationPath $zip -Force
Write-Host ""
Write-Host "Collected $copied app log file(s) and $werCount error report(s)."
Write-Host "Send this file: $zip" -ForegroundColor Green
