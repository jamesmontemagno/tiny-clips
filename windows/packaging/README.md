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

# Produce self-contained MSIX packages (x64 and arm64). The .NET and Windows App SDK runtimes
# are bundled, so the package has no framework dependencies and runs on a clean machine.
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
MSIX/winget versions like `1.0.1.0`. It builds x64 + ARM64 as **self-contained** MSIX packages,
signs them with Azure Artifact Signing, runs WACK, computes winget hashes, generates a versioned
winget manifest artifact, and creates the GitHub Release.

#### Self-contained packages (and why)

Since 1.7.3 the package bundles both runtimes (`-p:SelfContained=true` for .NET and
`-p:WindowsAppSDKSelfContained=true` for the Windows App SDK), with trimming explicitly off
(`-p:PublishTrimmed=false`; WinUI 3, H.NotifyIcon, NAudio and System.Drawing rely on reflection
that trimming breaks). The winget installer manifest therefore declares **no** package
dependencies. The release workflow unpacks each MSIX and asserts it is genuinely self-contained
(no `WindowsAppRuntime` framework dependency in the AppxManifest, and both `coreclr.dll` and
`Microsoft.UI.Xaml.dll` present in the payload) before signing.

The trade-off is size (~3x a framework-dependent package) and that runtime security fixes ship
with a Tiny Clips release instead of via the shared framework packages. We switched while
chasing winget's repeated `Validation-Executable-Error` on 1.7.x: it removes the validation VM's
runtime versions as a variable, and a self-contained MSIX can be verified end-to-end in Windows
Sandbox (which has no Store and so cannot auto-acquire framework packages).

Versions 1.0.x–1.7.2 were framework-dependent and declared `Microsoft.WindowsAppRuntime.1.8` and
`Microsoft.DotNet.DesktopRuntime.10` as winget dependencies. If we ever go back, restore that
`Dependencies` block in both workflows and the template, flip the workflow guards, and do not add
`Scope: user` (the dependency installers are machine-scope and winget validation rejects it).

#### Verifying on a clean machine

Install the MSIX on a clean machine or in Windows Sandbox (no runtimes required), launch
`TinyClips.App.exe` from `C:\Program Files\WindowsApps\Refractored.TinyClips_*` with an unrelated
working directory (that is what winget's harness does), and confirm the process stays alive with
the tray icon present. The `Windows Launch Smoke` workflow automates exactly this on x64 and ARM64
runners (`source=release` for a published tag, `source=build` for the current branch).

```pwsh
# Build a self-contained, packaged MSIX (x64)
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Release `
  -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false `
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
