# SyncForge

SyncForge adalah aplikasi desktop Windows untuk menyinkronkan tabel PostgreSQL secara terjadwal. Aplikasi ini terdiri dari UI WPF untuk konfigurasi dan monitoring, serta Worker Service yang dapat dipasang sebagai Windows Service.

## Fitur utama

- Konfigurasi koneksi Source dan Target PostgreSQL melalui UI; kata sandi disimpan dengan Windows DPAPI.
- Mapping tabel dan kolom eksplisit dengan pilihan dari skema database, bukan input nama bebas.
- Sinkronisasi `Incremental` dengan checkpoint dari histori run sukses terakhir.
- Sinkronisasi `Truncate & Reload` yang aman dengan staging table dan atomic rename-swap.
- Guard rail sebelum setiap job: batas jumlah minimum, deteksi penurunan jumlah baris, dan quiescence check.
- Histori eksekusi, retry exponential backoff, dan log rolling file.

## Struktur solusi

- `src/SyncForge.Core` - model domain, SQLite config store, enkripsi, guard rail, introspeksi PostgreSQL, serta engine sync.
- `src/SyncForge.Worker` - proses terjadwal yang dapat berjalan sebagai Windows Service.
- `src/SyncForge.Desktop` - aplikasi WPF untuk Connections, Jobs/Mapping, dan History.
- `tests/SyncForge.Core.Tests` - unit test guard rail.

## Menjalankan di Windows

1. Install [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) dan PostgreSQL client access ke kedua server.
2. Dari root solusi, jalankan `dotnet restore` lalu `dotnet build SyncForge.sln`.
3. Jalankan UI dengan `dotnet run --project src/SyncForge.Desktop`.
4. Setelah konfigurasi tervalidasi, publish worker: `dotnet publish src/SyncForge.Worker -c Release -r win-x64 --self-contained false`.
5. Daftarkan executable worker hasil publish sebagai Windows Service dan atur recovery policy di Services.

Database konfigurasi dibuat otomatis pada `%ProgramData%\\SyncForge\\syncforge.db`. Worker membaca ulang konfigurasi di setiap siklus, sehingga perubahan mapping tidak memerlukan restart service.

## Catatan operasional

- Mode `Truncate & Reload` selalu menjalankan guard rail. Job akan berstatus **Skipped unstable** jika sumber sedang kosong, jumlah baris turun melewati ambang, atau masih berubah selama quiescence check.
- Checkpoint incremental hanya ditulis dalam `sync_history` setelah transaksi target berhasil commit. Jangan mengedit checkpoint secara manual.
- Kredensial memakai DPAPI level mesin agar UI dan Windows Service dapat memakai config store yang sama; batasi akses ke `%ProgramData%\\SyncForge` hanya untuk administrator dan akun service.
