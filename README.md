# MasronDroid

MasronDroid adalah aplikasi desktop Windows x64 untuk mengelola perangkat Android melalui ADB dan Fastboot. Antarmuka tersedia dalam Bahasa Indonesia dan English.

## Mulai menggunakan

1. Unduh installer atau ZIP portabel dari [GitHub Releases](https://github.com/f22r/MasronDroid/releases/latest). Ekstrak seluruh ZIP sebelum membuka MasronDroid. Saat memindahkan aplikasi, salin seluruh folder, termasuk `tools`. Paket menyertakan runtime .NET, ADB, Fastboot, dan scrcpy.
2. Hubungkan perangkat dengan kabel data USB. Untuk ADB, aktifkan **USB debugging**, buka kunci layar, dan izinkan komputer pada dialog Android. Pasang driver USB yang cocok jika Windows belum mengenali perangkat. [Panduan Android](https://developer.android.com/studio/run/device)
3. MasronDroid memindai otomatis setiap dua detik. Jika HP hanya terdeteksi melalui MTP, ikuti petunjuk USB debugging di layar. Jika beberapa perangkat terdeteksi, pilih satu pada **Perangkat aktif**. Gunakan pilihan bahasa di kiri bawah.

## Mengelola perangkat

- **Perangkat** menampilkan mode, perangkat aktif, dan petunjuk koneksi. **Lihat layar** menampilkan HP di halaman Perangkat dan menerima klik, seret, serta input keyboard. Saat Android melaporkan layar terkunci, buka kunci langsung di HP. Panel layar tegak di kanan tetap terlihat saat berpindah halaman. Gunakan tombol Kembali, Beranda, dan Aplikasi terbaru di bawah video.
- **Instal APK** memasang satu berkas APK atau memperbarui aplikasi yang sudah terpasang. Periksa berkas dan serial pada dialog konfirmasi.
- **Debloat** memuat paket aktif atau nonaktif untuk pengguna Android 0. Cari nama paket, pilih satu, lalu nonaktifkan atau aktifkan kembali. MasronDroid membaca ulang status paket sesudah perubahan.
- **Fastboot** menyediakan reboot ke bootloader, pembacaan nama produk dalam Fastboot, serta reboot ke Android. Pindai ulang setelah perpindahan mode selesai.
- **Recovery** menyediakan reboot ke recovery dan kembali ke sistem jika recovery mendukung ADB. Untuk mengirim ZIP, aktifkan *Apply update from ADB* pada perangkat, pindai ulang dan pastikan status target `sideload`. Hasil pemasangan pembaruan ditentukan oleh recovery perangkat.

## Menjalankan dari source

Gunakan Windows x64, SDK .NET 10 atau SDK kompatibel yang lebih baru, dan PowerShell. Dari root repositori, jalankan:

```powershell
.\scripts\Prepare-Tools.ps1
dotnet run --project src/MasronDroid/MasronDroid.csproj
```

Skrip persiapan mengunduh dua arsip resmi dengan versi terkunci, memverifikasi SHA-256, dan menyiapkan binari lokal. Untuk menghasilkan folder siap pakai, ikuti [panduan build portabel](RELEASE.md).

## Peta source

- [Proyek WPF](src/MasronDroid/MasronDroid.csproj) dan [entry point](src/MasronDroid/App.xaml) mendefinisikan target aplikasi dan jendela pertama.
- [MainWindow](src/MasronDroid/MainWindow.xaml), [interaksi](src/MasronDroid/MainWindow.xaml.cs), [tema](src/MasronDroid/Theme.xaml), dan [terjemahan](src/MasronDroid/Locales) membentuk antarmuka.
- [AndroidBridge](src/MasronDroid/AndroidBridge.cs) menangani mode perangkat, validasi target, dan perintah. [PlatformTools](src/MasronDroid/PlatformTools.cs) menjalankan proses, sedangkan [ScreenMirror](src/MasronDroid/ScreenMirror.cs) mengelola sesi scrcpy.
- [Preferences](src/MasronDroid/Preferences.cs) menyimpan bahasa di data aplikasi lokal pengguna. Pemeriksaan source dan UI menggunakan [proyek uji](tests/MasronDroid.Checks/Program.cs).

Lihat [aturan kontribusi](AGENTS.md), [desain](DESIGN.md), [checklist](CHECKLIST.md), dan [hasil verifikasi](VERIFICATION.md).

Terminal log di bawah mencatat perubahan perangkat, perintah, dan hasil selama sesi. Tarik pegangan di atas terminal untuk mengatur tingginya.

Nama pemasaran dicocokkan dengan katalog Google Play di dalam aplikasi. Ilustrasi kecil diambil dari LineageOS Wiki untuk kode perangkat yang cocok. Model lain memakai ilustrasi generik. [Daftar Google Play](https://storage.googleapis.com/play_public/supported_devices.html) · [Ilustrasi perangkat](https://github.com/LineageOS/lineage_wiki/tree/bf49224b3a8838ad67f42e014435d5feeae5a668/images/devices)

Installer dan paket portabel resmi memeriksa pembaruan dari GitHub Releases ketika dibuka. Paket yang lebih baru diunduh dan diterapkan saat aplikasi dibuka kembali.
