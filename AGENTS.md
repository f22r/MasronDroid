# Working on MasronDroid

MasronDroid is a Windows x64 WPF application. Read the [user guide](README.md), [design notes](DESIGN.md), and [release guide](RELEASE.md) before changing their respective workflows.

- Keep device discovery, mode checks, target validation, and Android command arguments in [AndroidBridge](src/MasronDroid/AndroidBridge.cs). Use explicit serials and argument lists. Validate package identifiers before passing them to the Android shell.
- Recheck the transport and state before running a device action. Keep confirmations for installation, package changes, reboot, and sideload. Use simulated devices for write-operation tests unless the operator has authorized a specific physical test.
- Keep the client, server, selected ADB binary, session cleanup, and keep-awake setting together in [ScreenMirror](src/MasronDroid/ScreenMirror.cs). Pause mirroring when Android reports a locked device.
- Update both [locale dictionaries](src/MasronDroid/Locales) for interface changes. Keep GitHub documentation and release notes in English.
- Keep real serials, local preferences, credentials, user APKs, firmware, logs, and build output out of Git. Preserve upstream credits, licenses, pinned versions, and hashes.
- Record build, simulation, visual, device, and release results separately in [VERIFICATION.md](VERIFICATION.md).
