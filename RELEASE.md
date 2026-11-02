# Building and releasing MasronDroid

The version is defined in the [WPF project](src/MasronDroid/MasronDroid.csproj). The supported distribution target is self-contained Windows x64 on .NET 10.

## Local checks

```powershell
dotnet build src/MasronDroid/MasronDroid.csproj -c Release
dotnet run --project tests/MasronDroid.Checks -c Release
```

Pass an output folder after `--` to save simulated UI renders. The optional `--live-mirror` check requires one authorized and unlocked device. It opens a real mirror and sends Home, Recent apps, and Back.

## Packaging

Run `.\scripts\Build-Portable.ps1` for a local standalone payload. The publish folder contains MasronDroid.exe and the tools/platform-tools and tools/scrcpy subdirectories. Keep the whole folder together. This development payload is separate from the update-enabled distribution.

Run `.\scripts\Build-Release.ps1` to restore the pinned Velopack CLI and build the installer, portable ZIP, full update package, and feed. Output goes to `artifacts/github/v<version>/`. The release entry point runs Velopack before WPF starts.

[Prepare-Tools.ps1](scripts/Prepare-Tools.ps1) verifies the pinned Windows archives for Google Platform-Tools 37.0.1 and scrcpy 5.0.1. It extracts the named files and preserves NOTICE.txt and LICENSE.txt. See the [upstream credits](README.md#credits).

## GitHub Releases

Increment the project version for a new release. Tag its source revision. Attach the installer, portable ZIP, matching full nupkg, RELEASES, and releases.win.json to the same stable GitHub Release. Verify the asset sizes and SHA-256 digests, as well as the version, file name, and digest in the update index. Publish the release after these checks.

Release distributions check the public f22r/MasronDroid feed at startup. Velopack stages an available update and applies it on a subsequent launch. A clean Windows machine and an actual update from the previous public version are separate verification steps.
