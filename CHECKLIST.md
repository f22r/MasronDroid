# Project checklist

- [x] Verify repository identity, project entry point, version, architecture, dependency pins, and release outputs.
- [x] Review device targeting, mode validation, timeouts, package readback, and confirmation behavior.
- [x] Check both locale dictionaries and the documentation links.
- [x] Build the Windows application and run the automated WPF and command checks.
- [x] Inspect the frameless window, logo, language switching, cancellation, and small-window recovery layout.
- [x] Validate archive hashes and the bundled versions of ADB, Fastboot, and scrcpy.
- [x] Open a mirror for an authorized physical device, confirm the portrait host, and exercise the three Android navigation commands.
- [ ] Test installation, package state changes, reboot, and sideload on a designated physical test device.
- [ ] Measure keep-awake behavior beyond the phone's normal screen timeout.
- [ ] Test on a clean Windows x64 installation.

App preferences use per-user JSON. Database migrations do not apply. See the [verification record](VERIFICATION.md) for dated evidence.
