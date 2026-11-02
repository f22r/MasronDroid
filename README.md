# MasronDroid

MasronDroid is a Windows x64 desktop app for managing Android devices over USB. It combines ADB, Fastboot, and an interactive phone screen in one workspace. The interface is available in English and Indonesian.

**[Download the latest release](https://github.com/f22r/MasronDroid/releases/latest)**. Choose **MasronDroid-win-Setup.exe** to install, or extract the entire **MasronDroid-win-Portable.zip** and open MasronDroid.exe. Both distributions include the required tools and check GitHub Releases for updates. Downloaded updates take effect on a subsequent launch.

## Connect a device

1. Connect the phone with a USB data cable, enable **USB debugging** in Developer options, and accept the authorization prompt on the phone. If Windows does not recognize the device, check its [USB driver](https://developer.android.com/studio/run/device).
2. MasronDroid scans every two seconds. An MTP-only connection shows setup instructions. If several devices are present, select the desired target.
3. Choose **View screen**. The portrait panel on the right accepts mouse and keyboard input and includes **Back**, **Home**, and **Recent apps**. Unlock a locked phone on the device. Turn on **Keep screen on** to keep a USB-powered phone awake while mirroring. Changing this switch briefly reconnects the video.

## Device management

- **Devices** shows the active connection and lock state. Model names are matched against an offline Google Play device catalog. A matching LineageOS Wiki illustration is loaded when available, with a generic phone illustration as a fallback.
- **Install APK** installs one APK or updates an existing app. Review the file and target before confirming.
- **Debloat** disables or re-enables a selected package for Android user 0 and reads the state back from the device.
- **Fastboot** reboots to the bootloader, reads the product name in Fastboot mode, or restarts Android.
- **Recovery** reboots to recovery and sends an update ZIP when the device is in ADB sideload mode. Recovery determines whether the update can be applied.

The session log records device changes, commands, and results. Drag its upper edge to resize it.

## Build from source

Use Windows x64, PowerShell, and a .NET SDK that supports .NET 10. Run these commands from the repository root.

```powershell
.\scripts\Prepare-Tools.ps1
dotnet run --project src/MasronDroid/MasronDroid.csproj
```

The [preparation script](scripts/Prepare-Tools.ps1) downloads pinned upstream archives and verifies their SHA-256 hashes. Run [Build-Release.ps1](scripts/Build-Release.ps1) to generate the installer, portable ZIP, and GitHub update feed. Run `dotnet run --project tests/MasronDroid.Checks -c Release` for the [automated checks](tests/MasronDroid.Checks/Program.cs).

## Credits

**Screen mirroring and input are powered by scrcpy**, developed by **Genymobile and the scrcpy contributors**. MasronDroid bundles the Windows client and matching Android server from scrcpy 5.0.1. Its Apache License 2.0 is included in the application payload at `tools/scrcpy/LICENSE.txt`. [scrcpy project](https://github.com/Genymobile/scrcpy) · [upstream release](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1) · [license](https://github.com/Genymobile/scrcpy/blob/v5.0.1/LICENSE)

ADB and Fastboot come from Google's Android SDK Platform-Tools. Device marketing names come from Google Play's supported device list. Model illustrations come from contributors to LineageOS Wiki. Velopack provides Windows packaging and updates. [Platform-Tools](https://developer.android.com/tools/releases/platform-tools) · [device catalog](https://storage.googleapis.com/play_public/supported_devices.html) · [device illustrations](https://github.com/LineageOS/lineage_wiki/tree/bf49224b3a8838ad67f42e014435d5feeae5a668/images/devices) · [Velopack](https://github.com/velopack/velopack)
