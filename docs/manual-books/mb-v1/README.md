# SyncForge Manual Book v1

Panduan pengguna untuk SyncForge, aplikasi desktop Windows yang menyinkronkan tabel PostgreSQL secara terjadwal.

## Daftar isi

1. [Pengenalan](#1-pengenalan)
2. [Sebelum memulai](#2-sebelum-memulai)
3. [Membuka aplikasi](#3-membuka-aplikasi)
4. [Menambahkan koneksi database](#4-menambahkan-koneksi-database)
5. [Membuat job sinkronisasi](#5-membuat-job-sinkronisasi)
6. [Memahami mode sinkronisasi](#6-memahami-mode-sinkronisasi)
7. [Mengatur guard rail dan jadwal](#7-mengatur-guard-rail-dan-jadwal)
8. [Memantau histori](#8-memantau-histori)
9. [Operasional rutin](#9-operasional-rutin)
10. [Troubleshooting](#10-troubleshooting)
11. [Keamanan dan backup](#11-keamanan-dan-backup)

---

## 1. Pengenalan

SyncForge memiliki dua bagian:

- **Desktop UI**: dipakai administrator untuk mengatur koneksi, mapping tabel, guard rail, jadwal, dan membaca histori.
- **SyncForge Worker**: Windows Service yang menjalankan job pada jadwalnya, walaupun UI sedang ditutup.

Data mengalir dari **Source** ke **Target**. Source biasanya adalah database operasional dengan akses baca, sedangkan Target adalah database reporting atau mirror dengan akses tulis.

> UI tidak perlu dibiarkan terbuka agar sinkronisasi berjalan. Pastikan service `SyncForge Worker` berstatus `Running`.

## 2. Sebelum memulai

Siapkan informasi berikut untuk setiap database:

| Informasi | Contoh |
| --- | --- |
| Host | `db-source.internal` |
| Port | `5432` |
| Database | `production` |
| Username | `syncforge_reader` |
| Password | kredensial database |
| Tabel yang disinkronkan | `public.customers` |

Pastikan:

- komputer Windows dapat terhubung ke kedua PostgreSQL server;
- akun Source memiliki `SELECT` dan akses metadata `information_schema`;
- akun Target memiliki izin `SELECT`, `INSERT`, dan `UPDATE` untuk incremental;
- mode Truncate & Reload juga membutuhkan `CREATE TABLE`, `ALTER TABLE`, dan `DROP TABLE` pada schema target;
- Anda mengetahui jadwal proses ETL/reload di Source agar jadwal SyncForge tidak bertabrakan dengannya.

## 3. Membuka aplikasi

1. Jalankan **SyncForge** dari Start Menu.
2. Halaman **Overview** menampilkan jumlah koneksi, job aktif, run sukses, dan job yang dilewati guard rail.
3. Klik **Refresh data** bila Anda ingin memuat ulang data konfigurasi dan ringkasan.

Jika aplikasi menampilkan pesan startup error, periksa apakah folder `%ProgramData%\SyncForge` dapat diakses dan lihat log Worker di `%ProgramData%\SyncForge\logs`.

## 4. Menambahkan koneksi database

Buka tab **Connections**, lalu lakukan langkah berikut untuk Source dan Target.

1. Klik **New**.
2. Isi **Name** dengan nama yang mudah dikenali, misalnya `Production Source`.
3. Pilih **Role**:
   - `Source` untuk database asal.
   - `Target` untuk database tujuan.
4. Isi Host, Port, Database, Username, dan Password.
5. Klik **Test connection**. Lanjutkan hanya jika koneksi berhasil.
6. Klik **Save connection**.

Password tersimpan di SQLite dalam bentuk terenkripsi dengan Windows DPAPI level mesin. Password tidak ditampilkan di histori atau log.

Untuk mengubah koneksi, pilih baris koneksi pada daftar, ubah nilai yang diperlukan, lalu klik **Save connection**. Koneksi yang masih dipakai job tidak dapat dihapus sampai job tersebut dihapus atau diperbarui.

## 5. Membuat job sinkronisasi

Buka tab **Jobs & mapping** lalu klik **New job**.

### 5.1 Pilih koneksi dan tabel

1. Isi **Job name**, misalnya `Sync customers`.
2. Pilih mode `Incremental` atau `Truncate & Reload`.
3. Pilih Source connection dan Target connection.
4. Klik **Load schema** untuk membaca tabel yang tersedia dari kedua database.
5. Pilih **Source table** dan **Target table**.

Nama tabel dan kolom dipilih dari metadata database, bukan diketik sebagai SQL bebas.

### 5.2 Buat mapping kolom

1. Klik **Auto-suggest mapping** untuk memasangkan kolom dengan nama yang sama/serupa.
2. Periksa semua hasil mapping. Tambahkan baris melalui **Add mapping row** jika diperlukan.
3. Pilih kolom source dan target pada setiap baris mapping.
4. Centang **Primary key** pada satu atau beberapa kolom yang membentuk key unik target.

Contoh mapping:

| Source column | Target column | Primary key |
| --- | --- | --- |
| `customer_id` | `id` | Ya |
| `full_name` | `name` | Tidak |
| `updated_at` | `updated_at` | Tidak |

Untuk mode Incremental, target harus memiliki primary key atau unique constraint dengan kolom yang Anda tandai. Bila tidak, upsert akan gagal.

### 5.3 Simpan job

Atur guard rail dan jadwal seperti pada bagian berikut, lalu klik **Save job**. Worker akan membaca konfigurasi baru pada siklus berikutnya; restart service tidak diperlukan.

## 6. Memahami mode sinkronisasi

### Incremental

Gunakan untuk tabel besar dan sering berubah.

- Pilih **Timestamp column** dari Source, biasanya `updated_at`.
- SyncForge mengambil baris dengan timestamp lebih besar daripada checkpoint run sukses terakhir.
- Data masuk ke staging lalu di-upsert ke Target berdasarkan primary key mapping.
- Delete fisik pada Source tidak ikut diterapkan di Target.

Pilih kolom timestamp yang stabil dan cukup presisi. Bila aplikasi Source bisa membuat baris baru dengan timestamp yang sama persis dengan checkpoint terakhir, gunakan timestamp monotonik atau perbaiki proses Source agar tidak ada data terlewat.

### Truncate & Reload

Gunakan untuk tabel referensi yang kecil atau tabel yang Target-nya harus identik 100% dengan Source.

- SyncForge menyalin seluruh data ke staging table.
- Setelah berhasil, staging ditukar dengan target secara atomik melalui rename-swap.
- Delete pada Source otomatis tercermin di Target.
- Mode ini selalu menjalankan guard rail.

Jangan gunakan mode ini untuk target yang direferensikan foreign key oleh tabel lain. Aplikasi akan menolak job tersebut untuk menghindari referensi relasional menjadi tidak aman.

## 7. Mengatur guard rail dan jadwal

### Guard rail

| Field | Fungsi | Contoh awal |
| --- | --- | --- |
| Minimum rows | Batas minimum row Source yang dianggap valid. | `100` |
| Max drop (%) | Penurunan maksimum dari count run sukses terakhir. | `30` |
| Stability check enabled | Mengaktifkan pembacaan count kedua. | Aktif |
| Quiescence delay | Jeda sebelum count kedua. | `15` detik |

Jika Source sementara kosong, jumlah baris turun terlalu besar, atau count berubah selama delay, status run menjadi `SkippedUnstable`. Target tidak diubah.

Tentukan nilai minimum dan max drop dari pola data normal. Jangan menggunakan nilai `0` dan `100%` untuk semua job tanpa alasan, karena hal itu melemahkan perlindungan utama aplikasi.

### Jadwal cron

Field **Schedule** menggunakan format lima kolom:

```text
minute hour day-of-month month day-of-week
```

Contoh umum:

| Nilai | Arti |
| --- | --- |
| `0 2 * * *` | Setiap hari pukul 02:00. |
| `30 1 * * 1-5` | Senin-Jumat pukul 01:30. |
| `0 */6 * * *` | Setiap enam jam, pada menit 00. |

Jadwal dievaluasi menggunakan waktu lokal komputer yang menjalankan Worker. Sisakan buffer setelah ETL/reload Source selesai.

## 8. Memantau histori

Buka tab **Run history** untuk melihat hasil run terbaru.

| Status | Arti | Tindakan |
| --- | --- | --- |
| `Success` | Target berhasil commit. | Tidak ada tindakan khusus. |
| `Failed` | Validasi, koneksi, COPY, atau transaksi gagal. | Baca pesan error dan log Worker. |
| `SkippedUnstable` | Guard rail mendeteksi Source tidak aman. | Periksa jadwal/upstream dan threshold guard rail. |

Kolom **Rows** adalah jumlah baris yang diproses pada run. **Source rows** adalah count Source yang digunakan guard rail. Checkpoint incremental hanya berubah setelah `Success`.

## 9. Operasional rutin

Lakukan pemeriksaan berikut secara berkala:

1. Periksa halaman Overview dan Run history setiap hari setelah jadwal utama.
2. Tinjau `SkippedUnstable` berulang; ini umumnya berarti jadwal Source dan SyncForge terlalu berdekatan.
3. Periksa log error Worker jika terdapat `Failed` berulang.
4. Setelah perubahan schema Source/Target, buka job, klik **Load schema**, perbarui mapping, lalu simpan sebelum jadwal berikutnya.
5. Simpan backup database konfigurasi sebelum upgrade aplikasi.

Untuk memeriksa Worker melalui PowerShell:

```powershell
Get-Service -Name "SyncForge Worker"
Get-Content "$env:ProgramData\SyncForge\logs\worker-*.log" -Tail 50
```

## 10. Troubleshooting

### Test connection gagal

- Pastikan host, port, database, username, dan password benar.
- Uji koneksi dari mesin Worker menggunakan `psql` atau tool database lain.
- Periksa firewall, VPN, DNS, dan aturan PostgreSQL `pg_hba.conf`.

### Job gagal dengan mapping tidak valid

Schema Source atau Target mungkin berubah. Buka job, klik **Load schema**, perbaiki mapping kolom, lalu simpan. Pastikan tipe data source dan target kompatibel.

### Job gagal saat incremental upsert

Pastikan kolom yang ditandai primary key memiliki primary key atau unique constraint pada target. Periksa juga bahwa target account mempunyai izin `INSERT` dan `UPDATE`.

### Job selalu SkippedUnstable

- Periksa apakah Source sedang di-truncate/reload saat job berjalan.
- Tambahkan buffer pada jadwal Cron.
- Periksa apakah `Minimum rows` terlalu tinggi atau `Max drop (%)` terlalu rendah untuk pola data normal.
- Tingkatkan quiescence delay bila ETL Source memuat data bertahap.

### Worker tidak berjalan

1. Jalankan `Get-Service -Name "SyncForge Worker"`.
2. Jika status bukan `Running`, jalankan PowerShell sebagai Administrator lalu gunakan `Start-Service -Name "SyncForge Worker"`.
3. Baca log di `%ProgramData%\SyncForge\logs`.
4. Pastikan service memiliki akses jaringan ke database dan akses folder `%ProgramData%\SyncForge`.

## 11. Keamanan dan backup

- Batasi hak akses `%ProgramData%\SyncForge` kepada administrator dan akun service.
- Jangan mencetak connection string atau password ke tiket, email, maupun log.
- Backup `syncforge.db` sebelum upgrade besar atau perubahan konfigurasi massal.
- Backup hanya dapat dipulihkan di mesin yang sama karena password dilindungi DPAPI level mesin. Untuk migrasi ke komputer lain, buat ulang koneksi dari UI.
- Uji job baru pada database Target non-produksi sebelum diaktifkan pada produksi.

## Referensi lanjutan

- [Overview](../../overview.md)
- [Architecture](../../architecture.md)
- [Sync flow](../../sync-flow.md)
- [Database mapping](../../database-mapping.md)
- [Deployment](../../deployment.md)
- [Operational guide](../../OPERATIONS.md)
