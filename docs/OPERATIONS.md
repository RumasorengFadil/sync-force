# Operasional SyncForge

## Sebelum instalasi

Pastikan komputer Windows 10/11 bisa mencapai kedua PostgreSQL server melalui host dan port yang dikonfigurasi. Akun Source memerlukan `SELECT` atas tabel yang akan disinkronkan dan akses `information_schema`; akun Target memerlukan izin membuat staging table, `INSERT`, `UPDATE`, `ALTER TABLE`, serta `DROP TABLE` pada skema tujuan untuk job `Truncate & Reload`.

Konfirmasi juga jadwal proses truncate-reload upstream. Jadwalkan SyncForge dengan buffer setelah proses itu selesai, meskipun guard rail tetap aktif.

## Publish dan installer

Jalankan dari root repo pada Windows dengan .NET 8 SDK:

```powershell
dotnet restore
dotnet test
dotnet publish src/SyncForge.Desktop -c Release -r win-x64 --self-contained false -o publish/desktop
dotnet publish src/SyncForge.Worker -c Release -r win-x64 --self-contained false -o publish/worker
```

Kompilasi `installer/SyncForge.iss` menggunakan Inno Setup. Installer mendaftarkan `SyncForge Worker` sebagai Windows Service otomatis dan menambahkan recovery restart tiga kali.

## Konfigurasi awal

1. Jalankan aplikasi desktop sebagai akun Windows yang sama dengan akun yang akan menjalankan service.
2. Tambahkan satu koneksi Source dan satu Target, kemudian gunakan **Test connection**.
3. Buat job: pilih kedua koneksi, gunakan **Load schema**, pilih tabel, lalu **Auto-suggest mapping** atau tambah mapping baris demi baris.
4. Tandai primary key tujuan. Untuk `Incremental`, pilih timestamp yang merepresentasikan perubahan baris.
5. Tentukan batas minimum baris, toleransi drop, quiescence delay, dan jadwal cron lima kolom.

## Guard rail dan pemulihan

Sebelum melakukan ekstraksi, worker menghitung source table sekali atau dua kali (quiescence). Run tidak dilanjutkan bila:

- jumlah baris di bawah `Minimum rows`;
- jumlah baris turun melewati `Max drop (%)` dari run sukses terakhir; atau
- jumlah berubah selama quiescence check.

Run tersebut tercatat sebagai `SkippedUnstable`; tabel target tidak disentuh. Periksa jadwal upstream dan sesuaikan buffer/ambang sebelum mengaktifkan ulang job.

Jika koneksi putus, Worker mencoba lagi tiga kali dengan exponential backoff. Checkpoint hanya ada pada baris `success` terakhir di `sync_history`; kegagalan tidak mengubah checkpoint sehingga run berikutnya tetap aman.

## Catatan mode Truncate & Reload

Mode ini membuat staging table, mengisi dengan binary `COPY`, lalu menjalankan rename-swap dalam satu transaksi. Untuk mencegah foreign key tujuan menjadi referensi ke tabel backup, aplikasi menolak mode ini bila tabel tujuan direferensikan oleh foreign key dari tabel lain. Gunakan incremental untuk kasus tersebut.

## Data dan log

Konfigurasi dan histori berada di `%ProgramData%\SyncForge\syncforge.db`; log harian worker berada di `%ProgramData%\SyncForge\logs` dan dipertahankan 30 hari. Password dalam SQLite dienkripsi dengan Windows DPAPI level mesin agar companion UI dan Windows Service memakai config store yang sama. Batasi ACL folder tersebut ke administrator dan akun service.
