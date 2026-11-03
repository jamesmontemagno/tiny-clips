# Tiny Clips for Linux — developer probe

This is the first experiment from the [native GNOME port plan](../plans/linux-gtk-port-plan.md), **not a Linux release of Tiny Clips**. Its purpose is to check one boundary on a real desktop: can a sandboxed GTK app request a screenshot with the user's consent and display the image GNOME returns? Opening the app must not request capture. The development ID `com.refractored.tinyclips.Devel` is separate from the proposed release ID.

## Try it on GNOME Wayland

Use an x86-64 Ubuntu or Fedora Workstation desktop with Flatpak, a running session bus, `xdg-desktop-portal` and its GNOME backend. For Hyper-V, use the guest's basic console rather than an RDP/enhanced session. In the guest terminal, `echo "$XDG_SESSION_TYPE"` should print `wayland`. A compilation check or an Xvfb session does not verify portal consent.

1. Download and unzip **TinyClips-Portal-Probe-x86_64** from a successful [Linux portal probe workflow](../.github/workflows/linux-portal-probe.yml) run. This is a test bundle, not a Flathub listing. A manually dispatched workflow also installs that bundle on a fresh runner and checks that its window maps on headless Wayland; the manual consent test below is still necessary.
2. In the directory containing the bundle, run:

   ```sh
   flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
   flatpak install --user ./TinyClips-Portal-Probe.flatpak
   flatpak run com.refractored.tinyclips.Devel
   ```

3. Optionally run `flatpak run com.refractored.tinyclips.Devel --self-test` to map a GTK window and exit without requesting capture. With a non-sensitive test image on screen, launch normally and request a screenshot. Use GNOME's picker; check that the preview matches the approved image. Cancel a second request and make sure another capture is still possible. Check keyboard navigation and a run with the permission denied. Record the desktop, portal backend, result, and whether you could retry; don't attach private screenshots.
4. Inspect permissions with `flatpak info --user --show-permissions com.refractored.tinyclips.Devel`. The manifest requests Wayland and graphics rendering, not network, microphone, home-directory, or raw input-device access. To remove the probe: `flatpak uninstall --user --delete-data com.refractored.tinyclips.Devel`.

## Build the same Flatpak locally

From this directory on Linux, with `flatpak` and `flatpak-builder` installed and the user Flathub remote configured as above:

```sh
mkdir -p .build
flatpak-builder --user --install-deps-from=flathub --assumeyes --force-clean \
  --run-tests --install .build/app com.refractored.tinyclips.Devel.json
flatpak run com.refractored.tinyclips.Devel
```

The manifest selects the GNOME 50 SDK/runtime and matching Rust extension; that is the **bundle's runtime**, not the host desktop version. The crate builds with `cargo --frozen` from the checksummed [Flatpak source list](packaging/cargo-sources.json). If Cargo dependencies change, update the lockfile and regenerate the list, not its hashes by hand. From this directory, using [`flatpak-cargo-generator`](https://github.com/flatpak/flatpak-builder-tools/tree/9d86d336a39b9af38b86f114b6ce5820fe260f91/cargo) and its documented Python dependencies:

```sh
python3 /path/to/flatpak-cargo-generator.py spikes/portal-capture/Cargo.lock -o packaging/cargo-sources.json
```

## Boundaries

This is an **interactive portal diagnostic**. It does not record video, register shortcuts, manage a library, listen for agent connections, or advertise `capture.screenshot`. The existing [Agent Control contract](../shared/automation/README.md) requires unattended commands never to open a picker. The product implementation must expose already-authorized sources through a paired, scoped endpoint; calling this probe from the CLI is not a substitute.

The next media milestone is a ScreenCast/PipeWire stream and a seekable MP4 recorded from inside the package. Video is **not ready** until a 30-minute software-encoded screen + mic + system-audio take passes the [endurance and recovery gates](../plans/linux-gtk-port-plan.md#3-architecture-and-long-recordings).
