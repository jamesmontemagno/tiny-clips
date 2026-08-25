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
cd windows

# (Dev only) create + trust a local signing cert
winapp cert generate --publisher "CN=Refractored LLC, O=Refractored LLC, L=Seattle, S=Washington, C=US"
winapp cert install

# Produce framework-dependent MSIX packages (x64 and arm64). The .NET and Windows App SDK
# runtimes are NOT bundled; they are declared as winget package dependencies instead.
dotnet publish src/TinyClips.App/TinyClips.App.csproj -c Release -p:Platform=x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -p:PublishTrimmed=false
dotnet publish src/TinyClips.App/TinyClips.App.csproj -c Release -p:Platform=arm64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -p:PublishTrimmed=false
winapp package src\TinyClips.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64 --output TinyClips-x64.msix
winapp package src\TinyClips.App\bin\arm64\Release\net10.0-windows10.0.26100.0\win-arm64 --output TinyClips-arm64.msix

# Sign the package(s)
winapp sign --package <path-to.msix>
```

Attach the signed `.msix` files to a GitHub Release (e.g. `v1.0.0`).

### Automated Windows release workflow

`.github/workflows/windows-release.yml` runs for tags like `v1.0.1-windows` and maps them to
MSIX/winget versions like `1.0.1.0`. It builds x64 + ARM64 as **framework-dependent** MSIX
packages, signs them with Azure Artifact Signing, runs WACK, computes winget hashes, generates
a versioned winget manifest artifact, and creates the GitHub Release.

#### Framework-dependent + declared winget dependencies (and how)

The package is kept small by **not** bundling the runtimes. The winget installer manifest declares
both runtimes as package dependencies, so winget installs them before the app:

| winget `PackageDependency` | Provides |
|---|---|
| `Microsoft.WindowsAppRuntime.1.8` | The Windows App SDK runtime (WinUI 3, etc.) |
| `Microsoft.DotNet.DesktopRuntime.10` | The .NET 10 Desktop Runtime |

The matching MSBuild properties are `-p:SelfContained=false` (do not bundle .NET) and
`-p:WindowsAppSDKSelfContained=false` (do not bundle the Windows App SDK runtime). The release
workflow unpacks each MSIX and asserts it is genuinely framework-dependent (the AppxManifest
declares the `WindowsAppRuntime` dependency and the payload contains no bundled `coreclr.dll`)
before signing.

> ⚠️ **Both runtimes are delivered differently.** The Windows App SDK runtime is an MSIX
> *framework package*, so on machines with the Store/App Installer the OS can auto-acquire it
> during deployment, and winget can also install the declared `Microsoft.WindowsAppRuntime.1.8`
> dependency. The .NET Desktop Runtime is **not** an MSIX framework — the OS will not auto-deliver
> it — so it is installed via the declared `Microsoft.DotNet.DesktopRuntime.10` winget dependency.

> ⚠️ **Do not verify a framework-dependent build in Windows Sandbox.** Sandbox has no Microsoft
> Store, so MSIX framework auto-acquisition fails and the install dies at ~95% with `0x80073cf3`
> (`This package has a dependency missing from your system`). That is a Sandbox artifact, not a
> real failure. Validate on a real machine (with the runtimes absent, then `winget install` and
> let winget resolve the dependencies) or rely on winget's Installation Validation pipeline.

#### Verifying on a real clean machine

On a normal Windows machine (with the Store/App Installer) that does not yet have the runtimes,
`winget install --manifest <dir>` should install `Microsoft.DotNet.DesktopRuntime.10` and
`Microsoft.WindowsAppRuntime.1.8` first, then the app. A pass is the app process running with the
tray icon present and no `.NET Desktop Runtime` prompt.

Do not add `Scope: user` to the installer manifest. The TinyClips MSIX installs per-user by
default, but the runtime dependency installers are machine-scope/unknown-scope packages; forcing
user scope causes winget validation to reject those dependencies with "No suitable installer found."

```pwsh
# Build a framework-dependent, packaged MSIX (x64)
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Release `
  -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=false -p:WindowsAppSDKSelfContained=false `
  -p:EnableMsixTooling=true -p:GenerateAppxPackageOnBuild=true `
  -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
  -p:AppxPackageDir=<out>\ -p:AppxPackageSigningEnabled=false
```

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
4. Build with the Store flavor flag so Store-only distribution behavior is enabled:
   `dotnet publish src/TinyClips.App/TinyClips.App.csproj -c Release -p:Platform=x64 -p:TinyClipsStoreBuild=true`
   (repeat for ARM64 as needed).
5. Complete the listing metadata, privacy, and screen-recording capability declarations.
   - Privacy policy URL: `https://tinyclips.app/privacy.html`

> ⚠️ Requires a Partner Center account; cannot be completed from the repo alone.

## Capabilities

The current feature set (Graphics.Capture, toast notifications, file save to
Pictures/Videos) runs under `runFullTrust` with package identity — no extra manifest
capabilities are required. The `microphone` device capability is already declared in
`Package.appxmanifest` to support audio recording.
