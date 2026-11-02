# Build dan rilis MasronDroid

Rilis awal adalah `0.0.1`. Rilis `0.0.2` menambahkan sakelar layar tetap menyala. Target aplikasi adalah, `net10.0-windows`, Windows x64. Versi aplikasi berada di [file proyek](src/MasronDroid/MasronDroid.csproj).

## Build diagnostik

Dari root repositori dengan SDK .NET 10 atau SDK yang mendukung target tersebut:

```powershell
dotnet build src/MasronDroid/MasronDroid.csproj -c Release
dotnet run --project tests/MasronDroid.Checks -c Release
```

Build diagnostik tidak mengunggah file. Uji UI menggunakan transport simulasi. Untuk menyimpan render UI dengan perangkat contoh, berikan direktori output sebagai argumen terakhir:

```powershell
dotnet run --project tests/MasronDroid.Checks -c Release -- artifacts/verification
```

## Folder portabel

```powershell
.\scripts\Build-Portable.ps1
```

[Skrip build](scripts/Build-Portable.ps1) menyiapkan dependensi dan menjalankan `dotnet publish` lokal secara self-contained untuk `win-x64`. Jalur distribusinya adalah `artifacts/MasronDroid-win-x64/`. Jalankan EXE dari sana atau salin seluruh folder ke komputer Windows x64.

```text
MasronDroid-win-x64/
  MasronDroid.exe
  tools/
    platform-tools/    adb.exe, fastboot.exe, DLL pendukung, NOTICE.txt, source.properties
    scrcpy/            scrcpy.exe, scrcpy-server, DLL, ikon, LICENSE.txt
```

[Skrip persiapan](scripts/Prepare-Tools.ps1) mengutamakan cache unduhan yang hash-nya sesuai. Skrip mengekstrak file yang tercantum ke `.runtime/tools/`. Kedua folder hasil diabaikan Git. Aplikasi mencari ADB dan Fastboot di dalam subfolder alat bawaan di sebelah EXE. scrcpy menggunakan ADB terpilih dan server dari paket yang sama dengan klien.

## Asal dependensi

- Android SDK Platform-Tools 37.0.1 berasal dari arsip Windows Google. SHA-1 arsip cocok dengan indeks Google dan SHA-256 dikunci di skrip. [Platform-Tools](https://developer.android.com/tools/releases/platform-tools) · [Indeks SDK](https://dl.google.com/android/repository/repository2-3.xml)
- scrcpy 5.0.1 berasal dari ZIP Windows x64 pada rilis Genymobile. SHA-256 cocok dengan digest aset rilis. Daftar DLL, file server, ikon, dan lisensi mengikuti isi arsip versi tersebut. [Rilis scrcpy 5.0.1](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1)

## Memeriksa hasil

Periksa versi ketiga executable dari folder paket dan pertahankan file pemberitahuan serta lisensi. Buka aplikasi, pindai perangkat, dan uji layar pada target ADB yang diizinkan. Catat hasil perangkat terpisah dari hasil proses lokal.

```powershell
$bundle = 'artifacts/MasronDroid-win-x64'
& "$bundle/tools/platform-tools/adb.exe" version
& "$bundle/tools/platform-tools/fastboot.exe" --version
& "$bundle/tools/scrcpy/scrcpy.exe" --version
Get-FileHash "$bundle/MasronDroid.exe" -Algorithm SHA256
```

Publikasi GitHub merupakan langkah tersendiri. Untuk rilis publik yang diotorisasi, periksa materi distribusi, tag dan revisi, lalu unduh ulang aset publik dan bandingkan SHA-256 dengan paket yang disiapkan.

## Paket pembaruan GitHub

Jalankan `./scripts/Build-Release.ps1` pada Windows x64 untuk membangun setup, ZIP portabel, paket lengkap dan indeks Velopack dalam `artifacts/github/v<versi>/`. Terbitkan sebagai aset pada satu GitHub Release stabil di f22r/MasronDroid, termasuk `releases.win.json` dan berkas .nupkg. Naikkan versi proyek untuk setiap rilis berikutnya. Build diagnostik biasa tidak terdaftar dalam mekanisme pembaruan.
