# Verification record

## November 2, 2026 · Initial development build

The public repository was empty before initialization. The initial local build carried version 0.1.0. The public release sequence starts at 0.0.1.

The WPF application and checks built with zero errors and warnings using the .NET 11 RC3 SDK targeting .NET 10 Windows. The original 44 simulated checks covered transport selection, disconnects, mode changes, paths containing spaces, package confirmation and readback, and mirror failures. Additional work brought the total to 45 for MTP discovery and 47 for the embedded native mirror host. The local package included 18 verified tool files, with ADB 1.0.41 and Fastboot from Platform-Tools 37.0.1 and scrcpy 5.0.1.

UI checks covered all five pages, language switching, the frameless shell, maximize/restore, and unauthorized devices. Synthetic renders can be regenerated in artifacts/verification. Device writes and visual pixel comparisons on a real phone were not part of these checks.

## November 2, 2026 · 0.0.1

56 automated checks passed. They covered maximized window bounds against the monitor work area, device catalog matching, locked and unlocked states, selected target retention, and serial-bound Android navigation.

A physical device reported an active keyguard and was then unlocked by its operator. The app identified its model, the illustration source returned HTTP 200, and scrcpy attached to the right portrait panel. Home, Recent apps, and Back commands completed on that target. Physical mouse gestures and frame content throughout a complete lock/unlock cycle were not independently verified.

## November 2, 2026 · 0.0.2

59 automated checks passed. An initial mirror omits the keep-awake flag. Toggling the switch reconnects the selected mirror with the flag, then removes it when toggled off. The physical phone's timeout interval has not been measured.

For future checks, record the version, platform, device mode, steps, expected and observed results, and remaining gaps. Use synthetic or anonymized identifiers for public evidence.
