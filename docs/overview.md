# Overview SyncForge

SyncForge adalah aplikasi desktop Windows untuk sinkronisasi tabel PostgreSQL dari database Source ke database Target. Konfigurasi dikelola melalui aplikasi WPF, sedangkan eksekusi berjalan di background sebagai Windows Service.

## Tujuan

- Menyediakan konfigurasi koneksi, jadwal, tabel, dan mapping kolom tanpa mengedit JSON.
- Menjalankan sinkronisasi yang aman untuk tabel besar maupun tabel referensi.
- Mencegah target menerima data kosong atau parsial ketika Source sedang menjalankan proses reload.
- Menyimpan histori, checkpoint incremental, dan error agar operasi mudah diaudit.

## Komponen

| Komponen | Tanggung jawab |
| --- | --- |
| Desktop WPF | Konfigurasi connection, mapping, jadwal, dan monitoring histori. |
| Worker Service | Membaca job terjadwal dan menjalankan pipeline sinkronisasi. |
| SQLite config store | Menyimpan configuration, mapping, histori, serta checkpoint. |
| PostgreSQL source | Sumber data; worker hanya membutuhkan akses baca. |
| PostgreSQL target | Tujuan data; digunakan untuk staging, upsert, dan rename-swap. |

## Mode sinkronisasi

`Incremental` mengambil baris dengan timestamp lebih besar daripada checkpoint sukses terakhir, lalu melakukan upsert berdasarkan primary key mapping. Mode ini tidak menangkap delete fisik pada source.

`Truncate & Reload` mengambil seluruh data source ke staging table lalu menukar staging dengan tabel final secara atomik. Mode ini menghasilkan target identik dengan source, termasuk penghapusan data, tetapi tidak dapat digunakan jika target direferensikan foreign key dari tabel lain.

## Batas keamanan

Sebelum setiap job, SyncForge memvalidasi mapping, menghitung jumlah baris source, memeriksa penurunan jumlah baris, dan - bila diaktifkan - membaca ulang jumlah baris setelah quiescence delay. Job yang tidak stabil tidak mengubah target dan dicatat sebagai `SkippedUnstable`.
