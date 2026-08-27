# TinyClips for Windows — Packaging & Distribution

This folder holds the artifacts for shipping TinyClips via **direct MSIX / winget** and the
**Microsoft Store**. The app is a packaged (MSIX, identity-bearing) WinUI 3 app, which is what
enables toast notifications, startup tasks, and Store distribution.

## Prerequisites

- Windows App Development CLI (`winapp`): `winget install Microsoft.winappcli`
- Windows App SDK + Windows SDK (restored by `dotnet restore` / `dotnet publish`)
- A code-signing certificate (dev: self-signed; release: trusted CA or Store-signed)

## 1. Build a signed MSIX (direct distribution)

```pwsh
# From repo root
$packageDir = (Resolve-Path .).Path + "\artifacts\windows\"

dotnet build windows\src\TinyClips.App\TinyClips.App.csproj -c Release `
  -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:TinyClipsDirectReleaseBuild=true `
  -p:EnableMsixTooling=true -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageDir=$packageDir -p:AppxBundle=Never `
  -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false

.\windows\packaging\Assert-DirectPackage.ps1 `
  -MsixPath <path-to-x64.msix> -Architecture x64
```

Repeat with `Platform=ARM64`, `RuntimeIdentifier=win-arm64`, and
`Assert-DirectPackage.ps1 -Architecture arm64`. Sign the resulting packages before distribution.
NativeAOT requires Visual Studio's **Desktop development with C++** workload.

### Automated Windows release workflow

`.github/workflows/windows-release.yml` runs for tags like `v1.0.1-windows` and maps them to
MSIX/winget versions like `1.0.1.0`. It builds x64 + ARM64 as NativeAOT self-contained MSIX
packages, signs them with Azure Artifact Signing, runs WACK, computes winget hashes, generates a
versioned winget manifest artifact, and creates the GitHub Release.

#### Direct release runtime model

Direct GitHub Release and winget artifacts use the `TinyClipsDirectReleaseBuild=true` profile:

| MSBuild property | Direct release effect |
|---|---|
| `PublishAot=true` | Compiles Tiny Clips and reachable .NET code to an architecture-specific native executable; no JIT or machine-installed .NET runtime is used. |
| `SelfContained=true` | Makes the .NET deployment contract explicitly self-contained (NativeAOT also implies this). |
| `WindowsAppSDKSelfContained=true` | Includes WinUI 3 and Windows App SDK runtime files inside the MSIX instead of declaring `Microsoft.WindowsAppRuntime` as a framework dependency. |
| `PublishTrimmed=true` | Required by NativeAOT; app JSON serialization, COM interop, and XAML bindings use source-generated/compiled paths. |

Build and package in one MSBuild invocation. Splitting `dotnet publish` from packaging can lose the
embedded registration-free WinRT `activatableClass` metadata and cause `REGDB_E_CLASSNOTREG` on a
clean machine.

`Assert-DirectPackage.ps1` unpacks every x64/ARM64 candidate and requires:

- a native PE for the requested architecture with a native entry point and no CLR header;
- no `TinyClips.App.dll`, `coreclr.dll`, `clrjit.dll`, `hostfxr.dll`, or `hostpolicy.dll`;
- bundled `Microsoft.WindowsAppRuntime.dll` and `Microsoft.UI.Xaml.dll`;
- no `Microsoft.WindowsAppRuntime` framework dependency; and
- embedded registration-free WinRT activation metadata.

The x64 package is about **50.9 MiB compressed / 126.4 MiB expanded** at 1.8.0, versus
**22.4 MiB compressed / 72.1 MiB expanded** for a same-source framework-dependent package. The
download grows by about 28.5 MiB (2.27x). NativeAOT removes the CLR/JIT payload, but the Windows App
SDK self-contained payload includes optional runtime components, so release size still increases in
exchange for clean-machine installation.

#### Verifying locally in Windows Sandbox (recommended before every release)

```pwsh
.\windows\packaging\sandbox\Invoke-SandboxValidation.ps1 -Source Build -Version 1.8.0     # working tree
.\windows\packaging\sandbox\Invoke-SandboxValidation.ps1 -Source Release -Version 1.8.0   # published tag
```

This starts an **offline** fresh Sandbox, installs no .NET or Windows App Runtime prerequisites,
installs the MSIX, launches it exactly like winget's harness, clicks through onboarding, and
requires the process to stay alive. See [`sandbox/README.md`](sandbox/README.md). Sandbox is
x64-only; for ARM64 run the *Windows Launch Smoke* workflow (`source=release`,
`windows-11-arm` runner).

#### Verifying on a real clean machine

On a normal Windows 11 machine, direct MSIX and winget installation should not prompt for or install
.NET 10 Desktop Runtime or Windows App Runtime. A pass is the app process running with the tray icon
present. The winget installer manifest intentionally has no runtime `PackageDependencies`.

Required repository secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_ARTIFACT_SIGNING_ENDPOINT` (for example, `https://wus2.codesigning.azure.net/`)
- `AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME` (for example, `Refractored`)
- `AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME` (for example, `tinyclips-release`)

The Azure identity must have the **Artifact Signing Certificate Profile Signer** role on the
certificate profile.

## 2. Publish to winget

The three-file manifest in this folder (`*.yaml`) is the winget submission. After a signed
release exists:

1. Fill in the installer manifest:
   - `InstallerUrl` → the Release asset URL
   - `InstallerSha256` → `winget hash <path-to.msix>`
   - `SignatureSha256` → `winget hash --msix <path-to.msix>`
   - `PackageFamilyName` → from `Get-AppxPackage Refractored.TinyClips | Select PackageFamilyName`
2. Validate: `winget validate --manifest windows/packaging/winget`
3. Test locally: `winget install --manifest windows/packaging/winget`
4. Submit a PR to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) (or use
   `wingetcreate submit`). This can be automated in CI on each tagged release.

The locale manifest already includes:
- `PrivacyUrl: https://tinyclips.app/privacy.html`

> ⚠️ Requires the maintainer's GitHub account; the signed MSIX + hashes can only be produced
> from a release build with the real signing certificate.

## 3. Microsoft Store

1. Reserve the app name **Tiny Clips** in Partner Center.
2. Associate the app identity (`winapp` can pull the Store identity to override the dev
   `Package.appxmanifest` `Identity`).
3. Build the Store-configuration MSIX (Store handles signing) and upload via Partner Center
   or `winapp` Store submission.
4. Build with the Store flavor flag so Store-only distribution behavior is enabled while the
   direct NativeAOT/self-contained profile stays disabled:
   `dotnet build windows\src\TinyClips.App\TinyClips.App.csproj -c Release -p:Platform=x64 -p:TinyClipsStoreBuild=true -p:TinyClipsDirectReleaseBuild=false -p:PublishAot=false -p:SelfContained=false -p:WindowsAppSDKSelfContained=false`
   (the Store workflow creates the x64 + ARM64 upload bundle).
5. Complete the listing metadata, privacy, and screen-recording capability declarations.
   - Privacy policy URL: `https://tinyclips.app/privacy.html`

> ⚠️ Requires a Partner Center account; cannot be completed from the repo alone.

## Capabilities

The current feature set (Graphics.Capture, toast notifications, file save to
Pictures/Videos) runs under `runFullTrust` with package identity — no extra manifest
capabilities are required. The `microphone` device capability is already declared in
`Package.appxmanifest` to support audio recording.
