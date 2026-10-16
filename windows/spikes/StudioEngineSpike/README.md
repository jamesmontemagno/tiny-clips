# Studio engine spike

Throwaway prototype for todo `m0-win-engine-spike`: it de-risks the Windows preview and export engine of Tiny Clips Studio before the real one is built. What it found is in [FINDINGS.md](FINDINGS.md). Nothing here ships.

It is **standalone**: not part of `windows\TinyClips.Windows.slnx`, no `ProjectReference` to `TinyClips.Core` or `TinyClips.App`. The few pieces it needs from Core (sink writer wrapper, frame pacer, WinRT/Direct3D interop) are copies under `Engine\` and `Interop\`. Generated media, results and build output go to `media\`, `out\`, `bin\` and `obj\`, all ignored by git.

## What it is

One unpackaged WinUI 3 executable with a console, so the headless modes print to the terminal and the `present` mode opens a XAML window from the same binary. Every mode shares one Direct3D 11 device (`Engine\GraphicsDevice.cs`) and one Direct2D scene renderer (`Engine\SceneRenderer.cs`): gradient background, the screen recording as a padded rounded card with a soft shadow, the camera as a mirrored circle bottom-right.

| Mode | Question | Window | Typical duration |
|---|---|---|---|
| `media` | generates the test clips with ffmpeg and checks them | no | 1 min |
| `info` | adapter, Media Foundation encoders and decoders, runtime flavour | no | seconds |
| `preview` | 1: two frame-server `MediaPlayer`s on one `MediaTimelineController`, Direct2D composite | no | 18 min |
| `present` | 2: Win2D `CanvasSwapChainPanel` against `SwapChainPanel` + `ISwapChainPanelNative` | **yes** | 2 min |
| `export` | 3: source readers → Direct2D → sink writer, with audio | no | 4 min (+2.5 min for `settings`) |
| `encoders` | 4: one against two real-time sink writers | no | 7 min |

Question 5 (NativeAOT) is the same modes run from the AOT publish.

Results are never taken on trust from the code that produced them: the test clips carry their frame number as a machine-readable strip (`Engine\TestMedia.cs`, `Engine\FrameCode.cs`), and the modes read it back from composited textures, from screenshots of the window, and from every frame of the exported files.

## Requirements

- Windows 11, .NET SDK 10, `ffmpeg` and `ffprobe` on `PATH`.
- A GPU with a hardware H.264 encoder for `export` and `encoders`.
- For `present`: an interactive desktop. The window should be visible; if another application's full-screen window covers it the mode still runs and still checks screenshots, but DXGI frame statistics stay at zero (the report says so).
- For the NativeAOT publish: the Visual Studio C++ build tools (the `ilc` linker step).

## Build

```powershell
cd windows\spikes\StudioEngineSpike
dotnet build StudioEngineSpike.csproj -c Release -p:Platform=x64
$spike = '.\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\StudioEngineSpike.exe'
```

WinUI 3 has no `AnyCPU`; use `-p:Platform=x64` or `-p:Platform=ARM64`. The output is self-contained (Windows App SDK included), so nothing is installed or registered.

## Run

```powershell
& $spike media          # once; writes media\screen.mp4 and media\camera.mp4
& $spike info
& $spike preview
& $spike export
& $spike encoders
& $spike present        # opens a window for about two minutes
```

Each mode prints its report and also writes it to `out\<mode>\report.txt`, next to any PNG or MP4 it produces. A running executable locks the build output; for a long run while still building, copy the output folder and run the copy (the spike finds its own folder by walking up to the project file, or pass `--root`).

### Options

Common to all modes:

| Option | Meaning |
|---|---|
| `--root <dir>` | spike folder holding `media\` and `out\` (default: found from the executable's location) |
| `--tag <name>` | write to `out\<mode>-<name>\` instead of `out\<mode>\` |
| `--only a,b` | run only the named groups of a mode (below) |

`media`: `--force` regenerates clips that already exist.

`preview` groups: `colors, composite, play, pause, seek, seekrace, coordinator, step, rate, seekplay, drag, edges`.

| Option | Default | Meaning |
|---|---|---|
| `--skip a,b` | | leave groups out |
| `--canvas WxH` | `1920x1080` | preview canvas |
| `--texture-scale s` | `1.0` | size of the textures the players copy into, relative to the clip |
| `--play-seconds n` | `20` | length of the first playback test |
| `--race-seeks n` | `250` | seeks per batch in the lost-seek test |
| `--coordinator-seeks n` | `800` | random seeks through the seek policy |
| `--scrub-seconds n` | `60` | length of the simulated drag |
| `--seek-timeout-ms n` | `500` | watchdog timeout of the seek policy |
| `--no-seek-completed` | | seek policy without the `SeekCompleted` watchdog (timeout only) |

`export` groups: `probe, decode, seek, export, settings`.

| Option | Default | Meaning |
|---|---|---|
| `--repeat n` | `3` | runs of the full-length export |
| `--rounds n` | `3` | rounds of the encoder settings comparison |

`encoders` scenarios for `--only`: `single, dual-1080p-hardware, dual-720p-hardware, dual-1080p-software, dual-720p-software`.

| Option | Default | Meaning |
|---|---|---|
| `--seconds n` | `30` | real-time length of each scenario |
| `--repeat n` | `2` | passes over all scenarios |
| `--headroom-seconds n` | `10` | length of each flat-out run (0 skips them) |

`present` groups: `continuous, playback, block, drag, resize, scale, screenshot`.

| Option | Default | Meaning |
|---|---|---|
| `--skip a,b` | | leave groups out |
| `--window WxH` | `3000x1100` | window size in physical pixels |
| `--sync-interval n` | `0` | sync interval for presents outside the tests that compare 0 and 1 |
| `--playback-rounds n` | `3` | rounds of the playback test (each round runs sync interval 1, then 0) |
| `--capture-interval-ms n` | `1` | shortest time between screenshots; 0 leaves the system default of about 1/60 s |
| `--trace-exceptions` | | print every first-chance exception (for debugging XAML start-up) |

`present` writes three screenshots to `out\present\`: `window.png` (the whole window), `panel-a-win2d.png` and `panel-b-swapchainpanel.png` (each panel cut out).

## NativeAOT

```powershell
dotnet publish StudioEngineSpike.csproj -c Release -r win-x64 -p:PublishAot=true
$aot = '.\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\StudioEngineSpike.exe'
& $aot info
& $aot preview --tag aot
& $aot export --tag aot
& $aot encoders --tag aot
& $aot present --tag aot
```

`info` prints `runtime: NativeAOT` when the process was compiled ahead of time.

If the publish fails at the link step with `'vswhere.exe' is not recognized` followed by `MSB3073 ... link.exe ... exited with code 123`, the Visual Studio environment script could not find `vswhere.exe` by name. Put the installer folder on `PATH` for that shell and publish again:

```powershell
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
```

## Layout

| Path | What |
|---|---|
| `Program.cs` | mode dispatch and options |
| `Engine\GraphicsDevice.cs` | the shared D3D11 device, the gate that serializes its use, readbacks |
| `Engine\SceneRenderer.cs`, `SceneLayout.cs` | the Direct2D scene and the layout math from `docs\studio-project-format.md` section 6 |
| `Engine\PreviewEngine.cs` | two frame-server `MediaPlayer`s on one `MediaTimelineController` |
| `Engine\SeekCoordinator.cs` | the paused-seek policy that question 1 arrives at |
| `Engine\MediaReaders.cs` | source readers for export: video, frame cursor, audio |
| `Engine\SinkWriterEncoder.cs` | sink writer wrapper, adapted from Core's `MfSinkWriterEncoder` |
| `Engine\TestMedia.cs`, `FrameCode.cs`, `ReferenceColors.cs` | test clips and reading them back from pixels |
| `Interop\` | WinRT/Direct3D interop, `ISwapChainPanelNative`, Win2D native access, window capture item |
| `Modes\` | one file (or a few partial files) per mode |
| `Present\` | the XAML window, the two presenters, screenshot capture and analysis |
