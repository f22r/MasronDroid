# Desain MasronDroid

Pengguna bekerja dari meja servis dengan satu atau beberapa perangkat Android terhubung. Tugas pertama adalah memastikan serial dan mode target. Pilihan perangkat selalu tersedia di atas isi halaman. Perintah lebih jarang digunakan ditempatkan di menu masing-masing.

## Jendela dan identitas

Sidebar biru gelap memuat logo, lima halaman, dan pemilih bahasa. Judul halaman menyatu dengan permukaan kerja. Area atas dapat diseret. Tombol minimalkan, maksimalkan, pulihkan, dan tutup berada di kanan atas. Jendela minimum 790 × 560 DIP menggunakan scroll pada isi halaman.

[Logo vektor](src/MasronDroid/Assets/masrondroid.svg) menggambarkan kepala perangkat dengan antena, terminal, dan port USB. [PNG](src/MasronDroid/Assets/masrondroid.png) digunakan di sidebar dan [ICO](src/MasronDroid/Assets/masrondroid.ico) memuat ukuran 16 hingga 256 piksel untuk Windows.

[Theme.xaml](src/MasronDroid/Theme.xaml) mengatur navigasi, tombol, dan panel. Warna utama adalah biru gelap `#152E3E`, aksen aksi `#126782`, dan latar kerja `#F5F8FA`. Gunakan Segoe UI untuk label dan Consolas untuk nama paket, berkas, dan keluaran teknis.

## Kontrak interaksi

- **Target dan mode.** Pemilih perangkat native menampilkan identitas target. Jika lebih dari satu perangkat terdeteksi pada pemindaian awal, biarkan pengguna memilih. Perubahan target mengosongkan daftar paket sebelumnya.
- **Tindakan.** Tampilkan tombol sesuai fungsi halaman dan aktifkan sesuai mode, berkas, dan pilihan. Konfirmasi menyebut tindakan, serial, objek, dan dampaknya. Fokus awal adalah **Batal**.
- **Lihat layar.** Panel kanan menampung video dan navigasi Android. Navigasi halaman MasronDroid tetap dapat digunakan. Gunakan serial yang sama untuk input dan streaming. Tangguhkan sesi pada keyguard dan lanjutkan setelah pengguna membuka HP jika target masih sama.
- **Umpan balik.** Terminal log di bagian bawah menyimpan 200 entri terakhir dengan waktu lokal. Batas atas dapat digeser. Gunakan template ComboBox bersama untuk bahasa, target, dan status paket. Abaikan klik ganda selama operasi berjalan. Data perangkat dan hasil perintah hanya berada di memori sesi.
- **Bahasa dan akses.** Bahasa Indonesia dan English memakai struktur layar dan kunci terjemahan yang sama. Kombinasi tombol, radio, listbox, dan dialog mempertahankan interaksi keyboard native. Nama teknis berasal dari alat. Scroll tersedia pada layar pendek.
