---
description: "Use when changing the Linux GTK application, Wayland portal authorization, GStreamer capture or Flatpak packaging."
applyTo: "linux/**"
---
# Linux / GNOME

- Follow the [Linux build and verification guide](../../linux/README.md). Keep the capture bar usable on stock GNOME without a tray extension or global hotkeys.
- Never open a portal picker from an agent command. Retain only live, user-authorized source IDs, invalidate them on closure, and do not substitute a different source. Stream coordinates are not necessarily display-physical pixels.
- Use bounded media queues and recoverable on-disk segments; never replace the source file when editing or delete completed segments on failed finalization.
- Keep the Flatpak offline build reproducible: regenerate the checksummed Cargo sources after dependency changes. Do not add broad host filesystem, device, bus or network grants to make tests pass.
- Separate native unit tests, installed-bundle synthetic tests and real GNOME consent/device evidence. Do not claim a hardware or 30-minute live-recording pass based on compilation or generated input.
