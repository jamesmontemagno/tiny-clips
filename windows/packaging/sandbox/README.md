# Windows Sandbox validation

Reproduces winget's **Installation Validation** step locally, on a throwaway Windows install, in
one command. Use it before tagging a Windows release and before opening a winget-pkgs PR.

What it checks, in order:

1. Fresh Windows (Sandbox), with networking disabled so App Installer cannot acquire dependencies.
2. Reports whether .NET 10 Desktop Runtime or Windows App Runtime 1.8 happens to be present; it
   deliberately installs neither.
3. Validates and installs the NativeAOT self-contained MSIX (`Add-AppxPackage`).
4. Launches `TinyClips.App.exe` by full path from `C:\Program Files\WindowsApps\…` with an unrelated
   working directory (`C:\Windows\Temp`) — exactly how the winget harness starts the app.
5. Brings the first-run Welcome window to the foreground and presses Enter three times
   (Next → Next → Get started).
6. Requires the process to still be alive after `WaitSeconds` (default 60).
7. Writes `sandbox-result.txt`, two screenshots, the app's `crash.log` (if any) and any
   `Application Error` / `.NET Runtime` event-log entries to the shared folder.

## Prerequisites (host)

- Windows 10/11 Pro/Enterprise with **Windows Sandbox** enabled
  (*Turn Windows features on or off → Windows Sandbox*). Sandbox is x64-only.
- Windows 10/11 SDK (for `signtool.exe`) — needed for `-Source Build`.
- .NET 10 SDK — needed for `-Source Build`.
- `gh` CLI, authenticated — needed for `-Source Release`.
- **No other Sandbox instance running.** Only one can run at a time; the script refuses to start
  if one is open. If you close the window mid-run, re-run the script from scratch.

## Usage

From the repository root:

```pwsh
# Validate a published release (Azure-signed, no certificate juggling)
.\windows\packaging\sandbox\Invoke-SandboxValidation.ps1 -Source Release -Version 1.8.0

# Validate the current working tree: builds the MSIX with the release recipe and signs it with a
# throwaway self-signed certificate that only the Sandbox trusts
.\windows\packaging\sandbox\Invoke-SandboxValidation.ps1 -Source Build -Version 1.8.0
```

The Welcome window appears, gets clicked through, and the script prints the result:

```
18:05:28   installed
18:05:31   Refractored.TinyClips_1.8.0.0_x64__vmshqmcyy894t
18:05:32 Launching C:\Program Files\WindowsApps\...\TinyClips.App.exe (cwd C:\Windows\Temp)
18:05:52 Activating 'Welcome to Tiny Clips' and clicking through onboarding (3x Enter)
18:06:34 RESULT: PASS - alive after 60s
18:06:34 No crash.log
```

Exit code 0 = PASS. Anything else: read `sandbox-result.txt` — a `RESULT: FAIL` line carries the
exit code (`0xC000027B` = XAML stowed exception), followed by `crash.log` and the event-log
stack.

Artifacts land in `%TEMP%\tinyclips-sandbox\` (override with `-WorkDir`).

## Files

| File | Where it runs | Purpose |
|---|---|---|
| `Invoke-SandboxValidation.ps1` | host | Downloads or builds + validates + signs the MSIX, writes the offline `.wsb`, starts Sandbox, waits for and prints the result. |
| `Validate-TinyClips.ps1` | inside Sandbox (LogonCommand) | Installs the MSIX without runtime prerequisites, launches and drives the app, and collects evidence. Reads `config.json` written by the host script. |

## Gotchas we hit

- **`Add-AppxPackage` can print success while the package never registers.**
  `Validate-TinyClips.ps1` checks `Get-AppxPackage` afterwards and fails loudly.
- **Networking is disabled intentionally.** A framework-dependent regression must fail instead of
  being masked by App Installer or Store dependency acquisition.
- **The host validates package structure before starting Sandbox.** This catches the wrong
  architecture, managed/CLR output, missing bundled Windows App SDK files, a stale
  `WindowsAppRuntime` framework dependency, or missing registration-free activation metadata.
- **Sandbox ≠ ARM64.** For ARM64 use the *Windows Launch Smoke* GitHub workflow
  (`windows-11-arm` runner), or a real ARM64 machine with the gist-style test script.
- Don't hand-edit `Validate-TinyClips.ps1` with regex replacements in a shell: a `$_` in a
  replacement string expands to the whole script and you get a self-reinstalling loop.
