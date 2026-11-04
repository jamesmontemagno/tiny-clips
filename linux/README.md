# Tiny Clips for Linux — GNOME preview

A development build of the [native GNOME port](../plans/linux-gtk-port-plan.md), not a stable release or a Flathub listing. The Rust workspace contains the GTK capture bar, GStreamer media service, screenshot editor/library and opt-in local automation host. Its app ID is `com.refractored.tinyclips.Devel`. Opening the app never grants capture or starts a recording.

## Try it on GNOME Wayland

Use an Ubuntu or Fedora Workstation GNOME desktop with Flatpak, a running session bus, `xdg-desktop-portal` and its GNOME backend. On Windows, create a Generation 2 Hyper-V VM with a GNOME desktop image **matching the host CPU architecture** (x86-64 on Intel/AMD; aarch64 on Windows on Arm). Use the basic console rather than an RDP/enhanced session. In the guest terminal, `uname -m` identifies the bundle architecture and `echo "$XDG_SESSION_TYPE"` should print `wayland`. The standard build is x86-64; on Arm select **arm64** when manually running CI for an experimental native tester bundle. Cross-CPU emulation is useful for compatibility checks, not performance certification.

In the **Linux guest's terminal**, check `flatpak --version`. If the command is missing, install it with `sudo apt install flatpak` on Ubuntu or `sudo dnf install flatpak` on Fedora. Do not run guest setup commands in Windows PowerShell.

1. Download and unzip **TinyClips-Preview-x86_64** (or **TinyClips-Preview-aarch64** for an Arm guest) from a successful [Linux Flatpak workflow](../.github/workflows/linux-build.yml) run, or build the bundle locally as described below.
2. In the directory containing the bundle, run:

   ```sh
   flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
   flatpak install --user ./TinyClips-Preview.flatpak
   flatpak run com.refractored.tinyclips.Devel
   ```

3. **Screenshot → Region, Screen, or Window.** GNOME asks for the selected source type unless a matching source is already explicitly shared. For a region, select the part to keep on the frozen frame; an already-shared window is sufficient and does not require sharing the whole screen. Cancel saves nothing. The editor keeps the image central: pick a tool, open its options if needed, make a selection and **Apply**. Copy or save a new PNG from the header. The original stays in **Clips Library**. **Use desktop screenshot picker…** is an alternative if you prefer GNOME's own still-image workflow.
4. **Record → Region, Screen, or Window.** Configure optional microphone, system audio and countdown in **Main menu → Settings**. During a take the capture bar switches to elapsed time, **Pause/Resume** and **Stop and save**. Reopening Tiny Clips brings you back to those controls without a tray extension or shortcut. The saved MP4 appears in Clips Library. If a take fails, keep the private data directory: at next launch the app tries to recover completed segments without overwriting a capture.
5. **Agents (optional):** local captures do not share new sources with agents. To authorize unattended capture, use **Main menu → Choose shared source…**, then turn on **Agent Control** in **Settings**. Pair using the [CLI setup](../docs/agent-integration.md) and approve only the scopes required. Until a tool release includes Linux discovery, run `dotnet run --project agent/src/TinyClips.Cli -- pair --name "Linux tester"` from this checkout. `tinyclips capture.targets` exposes only the explicitly shared live source. **Release shared source** invalidates it; turning off Agent Control closes the socket.

The sandbox uses the Wayland and graphics sockets/device for the UI, and PulseAudio access for microphone/output-monitor audio. The latter is broader than a per-recording portal grant: check the audio settings and pair only trusted clients. No unrestricted home-directory, network or raw input device access is requested. To inspect permissions use `flatpak info --user --show-permissions com.refractored.tinyclips.Devel`. Captures are under `~/.var/app/com.refractored.tinyclips.Devel/data/tinyclips/`. **Back them up before uninstalling with `--delete-data`**; that flag also deletes the library and recovery segments.

## Build the same Flatpak locally

From this directory on Linux, with `flatpak` and `flatpak-builder` installed and the user Flathub remote configured as above:

```sh
sh prepare-flatpak.sh
flatpak-builder --user --install-deps-from=flathub --assumeyes --force-clean \
  --default-branch=devel --repo=.build/repo --run-tests --install .build/app com.refractored.tinyclips.Devel.json
flatpak run --branch=devel com.refractored.tinyclips.Devel
```

To produce a file that another tester can install:

```sh
flatpak build-bundle --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo \
  .build/repo .build/TinyClips-Preview.flatpak com.refractored.tinyclips.Devel devel
sha256sum .build/TinyClips-Preview.flatpak
```

The manifest selects the GNOME 50 SDK/runtime and matching Rust extension; that is the **bundle's runtime**, not the host desktop version. The crate builds with `cargo --frozen` from the checksummed [Flatpak source list](packaging/cargo-sources.json). If Cargo dependencies change, update the lockfile and regenerate the list, not its hashes by hand. From this directory, using [`flatpak-cargo-generator`](https://github.com/flatpak/flatpak-builder-tools/tree/9d86d336a39b9af38b86f114b6ce5820fe260f91/cargo) and its documented Python dependencies:

```sh
python3 /path/to/flatpak-cargo-generator.py Cargo.lock -o packaging/cargo-sources.json
```

## What to test before calling recording ready

- **Toolkit:** `flatpak run com.refractored.tinyclips.Devel --self-test` must map a GTK window and exit without requesting a source. This is not evidence of portal consent.
- **Installed codecs and timing:** CI runs a synthetic moving picture and two generated tones through the actual software encoder, segmenter and MP4 finalizer in the Flatpak. The [independent verifier](tests/verify_recording.py) checks H.264/AAC, **media duration against time actually requested**, both input tones at the beginning and end, track timestamp skew, and decoding the last second. Audio and video can agree perfectly and still both be substantially too short. Select **endurance** on a manual workflow run to record 1,800 real elapsed seconds and compare post-warm-up and final memory usage. This is not a test of a real microphone, monitor or Wayland authorization.
- **Live release gate:** use the installed app on a GNOME desktop with a moving visual target, mic, and audible system playback. Verify the two inputs independently, then record 30 minutes with both, with visible/audible markers near each end. Check for drift, memory growth, and loss of the last seconds; test a separate take with pause/resume, portal revocation and interrupted finalization. With the installed capture bar open and `ffmpeg`/`ffprobe` on the guest, run `python3 tests/observe_live_recording.py --seconds 1800` from this directory **before starting the take**. The observer samples the recorder's RSS, tells you when to stop, and checks the saved file. It never starts/stops a recording or grants portal consent. Keep distro, compositor, runtime, source, audio devices, marker observations and the resulting JSON with the test report. Do not upload private footage. Thirty minutes is a test case, not a recording cap.

In a live region-recording test on an emulated x86-64 Ubuntu guest, Stop detected a timestamp regression. Tiny Clips retained the closed segments and recovered a playable 26.88-second MP4 on reopen. This verified recovery, not a clean save on Stop or the 30-minute release gate.

The current preview does not claim full macOS/Windows parity. In particular, GIF recording, Studio, scrolling capture, OCR, webcam overlays, tray/shortcut integration and hardware encode certification are not part of this build. Unavailable agent commands must not be advertised. See the [port plan](../plans/linux-gtk-port-plan.md) for acceptance and scope and the [Linux Studio implementation plan](../plans/linux-studio-plan.md) for the edit-first follow-up.
