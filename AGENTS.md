# Bekerja di MasronDroid

MasronDroid adalah aplikasi WPF untuk Windows x64. API perangkat yang digunakan adalah CLI Android Platform-Tools dan scrcpy. Mulai dari [README](README.md) dan [panduan rilis](RELEASE.md).

## Alur perubahan

- Letakkan pemilihan target, pembatasan mode, dan kontrak argumen di [AndroidBridge](src/MasronDroid/AndroidBridge.cs). Panggil executable menggunakan path yang terselesaikan dan `ProcessStartInfo.ArgumentList`. Validasi format nama paket sebelum mengirimkannya melalui shell Android.
- Semua perintah perangkat harus terikat pada serial. Periksa ulang transport, serial, dan mode sebelum menjalankan tindakan. Putus koneksi atau perubahan mode harus mengembalikan kendali ke pengguna.
- Pertahankan konfirmasi untuk instalasi, perubahan status paket, reboot, dan sideload. Uji perintah tersebut dengan transport simulasi. Pengujian tulis pada perangkat fisik memerlukan target dan izin yang jelas.
- Untuk layar perangkat, gunakan pemilik sesi [ScreenMirror](src/MasronDroid/ScreenMirror.cs). Gunakan ADB yang sama dengan aplikasi, target eksplisit, serta input mouse dan keyboard setelah perangkat dibuka kuncinya.
- Perbarui kedua file di [Locales](src/MasronDroid/Locales) pada perubahan teks UI. Ikuti [DESIGN.md](DESIGN.md) untuk jendela, fokus keyboard, dan status tombol.

## Materi untuk repositori publik

Periksa file baru satu per satu. Source, dokumen, logo proyek, dan referensi unduhan resmi dapat ditinjau dalam Git. Simpan binari alat, hasil build, APK, firmware, file penandatanganan, log, serial perangkat nyata, dan preferensi lokal di luar Git. Pertahankan versi, hash, dan daftar file yang diizinkan di [skrip persiapan](scripts/Prepare-Tools.ps1).

Jalankan build dan pemeriksaan di [CHECKLIST.md](CHECKLIST.md). Bedakan bukti kompilasi, uji kontrak simulasi, inspeksi UI, proses CLI nyata, dan hasil pada perangkat fisik dalam [VERIFICATION.md](VERIFICATION.md).
