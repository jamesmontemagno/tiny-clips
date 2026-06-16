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

# Produce fully self-contained MSIX packages (x64 and arm64): bundle both the
# .NET runtime (SelfContained) and the Windows App SDK runtime (WindowsAppSDKSelfContained)
dotnet publish src/TinyClips.App/TinyClips.App.csproj -c Release -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false
dotnet publish src/TinyClips.App/TinyClips.App.csproj -c Release -p:Platform=arm64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false
winapp package src\TinyClips.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64 --output TinyClips-x64.msix
winapp package src\TinyClips.App\bin\arm64\Release\net10.0-windows10.0.26100.0\win-arm64 --output TinyClips-arm64.msix

# Sign the package(s)
winapp sign --package <path-to.msix>
```

Attach the signed `.msix` files to a GitHub Release (e.g. `v1.0.0`).

### Automated Windows release workflow

`.github/workflows/windows-release.yml` runs for tags like `v1.0.1-windows` and maps them to
MSIX/winget versions like `1.0.1.0`. It builds x64 + ARM64 as **fully self-contained** MSIX
packages, signs them with Azure Artifact Signing, runs WACK, computes winget hashes, generates
a versioned winget manifest artifact, and creates the GitHub Release.

#### Why fully self-contained (and how)

winget does **not** auto-install MSIX framework dependencies. A framework-dependent package
(one that declares `Microsoft.WindowsAppRuntime.*` as a dependency) therefore fails to install
on a clean machine — winget lists the dependency, installs the app anyway, and the install
fails at ~95% with `0x80073cf3` (`This package has a dependency missing from your system`). The
same failure happens on winget's clean, network-isolated Installation Validation VMs.

To avoid that, the package bundles **both** runtimes and declares **no** dependency. This needs
two MSBuild properties, and the build + MSIX packaging must happen in a **single** `dotnet build`
invocation:

| Property | Bundles |
|---|---|
| `-p:WindowsAppSDKSelfContained=true` | The Windows App SDK runtime (WinUI 3, etc.) |
| `-p:SelfContained=true` | The .NET Desktop Runtime |

> ⚠️ **Both are required.** With only `WindowsAppSDKSelfContained=true`, the app still
> framework-depends on the .NET Desktop Runtime and shows a "You must install .NET Desktop
> Runtime to run this application" dialog on a clean machine.

> ⚠️ **Do not split publish and packaging into separate steps.** The WinAppSDK
> `CreateWinRTRegistration` target merges the reg-free WinRT `activatableClass` registrations
> into the app EXE's embedded manifest *during the packaging build*. Those registrations are
> what let a self-contained package activate WinRT types without the framework package present.
> A build that runs `dotnet publish` and then packages the output separately ships an EXE
> without them, and the installed app crashes immediately at `Application.Start` with
> `REGDB_E_CLASSNOTREG` (`0xc000027b`). The workflow keeps build + package in one `dotnet build`
> and then asserts (by unpacking the MSIX) that the EXE contains `activatableClass` and that the
> AppxManifest has no `WindowsAppRuntime` dependency, failing the build if either check trips.

#### Verifying on a clean machine (Windows Sandbox)

The authoritative test is a clean machine with no runtimes pre-installed. Build a self-contained
MSIX locally, self-sign it, and install it inside Windows Sandbox:

```pwsh
# Build a fully self-contained, packaged MSIX (x64)
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Release `
  -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
  -p:EnableMsixTooling=true -p:GenerateAppxPackageOnBuild=true `
  -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
  -p:AppxPackageDir=<out>\ -p:AppxPackageSigningEnabled=false
```

In the sandbox, confirm `Get-AppxPackage Microsoft.WindowsAppRuntime*` returns nothing (true
clean machine), then `Add-AppxPackage` the signed MSIX and launch it. A pass is the app process
running with the tray icon present and **no** `.NET Desktop Runtime` prompt and **no**
`REGDB_E_CLASSNOTREG` crash.

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
