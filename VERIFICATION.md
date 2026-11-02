# Verifikasi MasronDroid

## 2 November 2026 · versi lokal 0.1.0

Repositori [f22r/MasronDroid](https://github.com/f22r/MasronDroid) berstatus publik dan kosong saat init. Pemeriksaan refs, branch, tag, dan GitHub Releases dilakukan sebelum pembuatan source lokal.

**Build.** Aplikasi dan proyek uji berhasil dibangun dalam konfigurasi Release dengan 0 error dan 0 warning. SDK yang digunakan adalah .NET 11 RC3 dengan target .NET 10 Windows. Build portabel memakai runtime mandiri `win-x64`. Verifikasi menggunakan SDK .NET 10 stabil pada mesin terpisah masih terbuka.

**Uji simulasi.** 44 pemeriksaan lulus. [Proyek uji](tests/MasronDroid.Checks/Program.cs) mencakup pemilihan transport dan target, perubahan mode, disconnect, pemindaian parsial, argumen APK dan ZIP dengan spasi, konfirmasi dan pembatalan perubahan paket, verifikasi status paket, serta penanganan kegagalan scrcpy. Fixture memakai identitas dan paket sintetis.

**UI Windows.** Jendela aplikasi hasil build dibuka dan diperiksa sesudah bilah judul native dihilangkan. Uji WPF memeriksa kelima halaman, perubahan bahasa, maksimalkan/pulihkan, konfirmasi, dan state yang menolak target tanpa izin. Render terbaru mencakup tombol Lihat layar dan keadaan tiga alat. Bukti lokal yang dapat diregenerasi berada di `artifacts/verification/` dengan nama `masrondroid-id.png`, `debloat-en.png`, `konfirmasi-id.png`, dan `recovery-en-small.png`. Render memakai transport simulasi. Pembukaan interaktif terakhir untuk EXE portabel tidak terverifikasi karena sesi pemeriksaan desktop dihentikan.

**Paket.** Folder `artifacts/MasronDroid-win-x64/` berisi satu EXE dan 18 file alat. Setiap file alat dalam folder distribusi cocok dengan file hasil ekstraksi arsip yang hash-nya diperiksa. ADB melaporkan 1.0.41 dari Platform-Tools 37.0.1, Fastboot melaporkan 37.0.1, dan scrcpy 5.0.1 berhasil memuat pustakanya. Percobaan scrcpy dengan serial sintetis yang tidak terhubung menghasilkan exit code 1 dan diagnostik ADB.

**Perangkat dan publikasi.** Pemindaian dengan CLI dalam paket menghasilkan 0 target ADB dan 0 target Fastboot. Transfer APK, perubahan paket, reboot, pemasangan ZIP oleh recovery, dan video perangkat nyata belum diuji. Folder portabel tersedia lokal. Tag, aset rilis, dan perubahan remote belum diterbitkan.

## Catatan pengujian berikutnya

Catat tanggal, revisi, versi Platform-Tools dan scrcpy, versi Windows/Android, mode target, langkah, hasil, dan bukti yang dapat dibagikan. Untuk pengujian perangkat, gunakan identitas anonim. Bedakan hasil perintah PC, perubahan yang dibaca ulang di perangkat, tampilan UI, dan hasil pascapemindaian.

## Pembaruan deteksi dan terminal � 2 November 2026

Build portabel diperbarui menggunakan arsip dependensi terverifikasi. Sebanyak 45 pemeriksaan simulasi lulus, termasuk transisi otomatis dari petunjuk MTP ke daftar target ADB, kondisi unauthorized, dan log kegagalan. Render halaman Perangkat memperlihatkan dropdown baru dan pegangan terminal. Transisi USB pada HP fisik dan drag dengan mouse fisik belum diuji.

## 2 November 2026 · layar HP di dalam aplikasi

Sebanyak 47 pemeriksaan otomatis lulus. Uji tambahan dengan satu HP ADB terotorisasi menjalankan scrcpy asli pada build Debug. Handle jendela scrcpy terverifikasi sebagai anak panel WPF. Tombol Hentikan tampilan mengakhiri sesi dan mengembalikan halaman Perangkat. Isi piksel layar HP tidak direkam atau diperiksa.

## Rilis 0.0.1 � 2 November 2026

56 pemeriksaan lulus, termasuk verifikasi batas jendela pada area kerja Windows, pencocokan nama Galaxy S9 dan ilustrasi berdasarkan model/kode, status keyguard terkunci dan terbuka, serta target yang tetap terpilih. Pengujian fisik mencakup pembacaan keyguard saat terkunci dan sesi scrcpy setelah perangkat dibuka. Model teridentifikasi, sumber ilustrasi merespons HTTP 200, panel kanan terverifikasi tegak, dan perintah Home, Recent, Back berhasil pada target. Ketepatan isi piksel saat transisi dan gestur mouse fisik belum diuji.
