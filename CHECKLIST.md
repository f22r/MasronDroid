# Checklist MasronDroid

## Init dan implementasi

- [x] Identitas repositori publik dan kondisi awal kosong diperiksa. Dokumen, source, dan aset logo dibuat khusus untuk MasronDroid.
- [x] Periksa entry point WPF, manifest, versi, target platform, jalur output, serta skrip persiapan dan build.
- [x] Periksa perintah per mode, validasi nama paket, konfirmasi target, timeout, dan pembacaan ulang status paket.
- [x] Periksa kunci terjemahan Indonesia dan Inggris serta tautan dokumentasi lokal.
- Database dan migrasi tidak berlaku. Preferensi bahasa dan folder alat menggunakan JSON lokal pengguna.

## Build, UI dan workflow

- [x] Build aplikasi dan proyek uji Release.
- [x] Jalankan pengujian AndroidBridge dan UI WPF dengan transport simulasi. Hasil tercatat di [VERIFICATION.md](VERIFICATION.md).
- [x] Periksa layar utama tanpa bilah judul native, logo, perubahan bahasa, dialog batal, dan layout Recovery ukuran minimum.
- [x] Verifikasi arsip sumber, salinan alat portabel, nomor versi CLI, dan diagnostik scrcpy ketika target uji tidak tersedia.
- [ ] Selesaikan instal APK, nonaktifkan/aktifkan kembali, reboot, dan sideload pada perangkat uji yang disetujui. Pemindaian terakhir tidak mendeteksi perangkat.
- [ ] Periksa streaming video langsung dan driver pada perangkat fisik. Target ADB berstatus `device` diperlukan.

## Distribusi

- [x] Buat folder Windows x64 self-contained dengan ketiga alat di sebelah EXE.
- [ ] Uji di Windows x64 bersih. Build diperiksa pada mesin pengembangan.
- [ ] Publikasikan tag/aset dan verifikasi unduhan remote jika ada permintaan rilis publik.
